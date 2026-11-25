#!/usr/bin/env python3
"""Back up the active installation and rehearse a candidate in a private MySQL container.

Never migrates the production database, starts a public API, changes its accounts,
or switches a release. Run as root on the existing AlShifa VPS.
"""
import argparse
import datetime
import fcntl
import gzip
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import subprocess
import tarfile
import time
import urllib.request


def run(args, **kwargs):
    result = subprocess.run(args, capture_output=True, **kwargs)
    if result.returncode:
        raise RuntimeError("Private preparation command failed; inspect the protected evidence on the VPS.")
    return result.stdout


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--id", required=True)
    parser.add_argument("--archive", required=True)
    parser.add_argument("--sha256", required=True)
    args = parser.parse_args()
    if os.geteuid() != 0 or not re.fullmatch(r"[0-9]{8}-[a-f0-9]{8}", args.id):
        raise RuntimeError("Root and a valid preparation identifier are required.")
    os.umask(0o077)
    lock = open("/run/lock/alshifa-deploy.lock", "a")
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    archive = Path(args.archive).resolve()
    if not archive.is_file() or hashlib.sha256(archive.read_bytes()).hexdigest() != args.sha256:
        raise RuntimeError("Candidate hash mismatch.")
    base = Path("/var/backups/alshifa/preparations") / args.id
    base.mkdir(parents=True, exist_ok=False, mode=0o700)
    result = {"id": args.id, "productionChanged": False, "passed": False}
    container = "alshifa-upgrade-" + args.id
    created = False
    api = None

    def sql(query, target="alshifa-mysql", raw=False):
        output = run(["docker", "exec", "-i", target, "sh", "-c",
                      'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot -N -B --binary-as-hex' + (' --raw' if raw else '')],
                     input=query.encode())
        return output.decode().splitlines()

    def quote(name):
        return "`" + name.replace("`", "``") + "`"

    def layout(target):
        tables = sql("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='alshifa_mirror' AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME", target)
        return {table: sql("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='alshifa_mirror' AND TABLE_NAME='" + table.replace("'", "''") + "' ORDER BY ORDINAL_POSITION", target) for table in tables}

    def proof(target, columns):
        hashes = {}
        for table, names in columns.items():
            # Keep row values entirely in this server-side process; only hashes are retained.
            rows = sql("SELECT " + ",".join(map(quote, names)) + " FROM alshifa_mirror." + quote(table), target)
            hashes[table] = hashlib.sha256(json.dumps(sorted(rows), ensure_ascii=True).encode()).hexdigest()
        return hashes

    def dump(path):
        with open(base / "dump-private.err", "ab") as error, gzip.open(path, "wb") as output:
            child = subprocess.Popen(["docker", "exec", "alshifa-mysql", "sh", "-c",
                'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysqldump --no-defaults -uroot --single-transaction --quick --routines --events --triggers --hex-blob --no-tablespaces --set-gtid-purged=OFF alshifa_mirror'], stdout=subprocess.PIPE, stderr=error)
            shutil.copyfileobj(child.stdout, output)
            child.stdout.close()
            if child.wait():
                raise RuntimeError("Database backup failed.")
        with gzip.open(path, "rb") as source:
            while source.read(1024 * 1024):
                pass

    try:
        if shutil.disk_usage(base).free < 4 * 1024**3:
            raise RuntimeError("Insufficient preparation disk space.")
        release = Path("/opt/alshifa/current").resolve()
        if not str(release).startswith("/opt/alshifa/releases/"):
            raise RuntimeError("Unexpected active release.")
        result["previousRelease"] = str(release)
        result["previousApiSha256"] = hashlib.sha256((release / "AlShifa.Api.dll").read_bytes()).hexdigest()
        columns = layout("alshifa-mysql")
        before = proof("alshifa-mysql", columns)
        dump(base / "database.sql.gz")
        if before != proof("alshifa-mysql", columns):
            raise RuntimeError("Source changed during backup; a coordinated snapshot is required.")
        (base / "database-layout.json").write_text(json.dumps(columns))
        (base / "database-proof.json").write_text(json.dumps(before))

        # Preserve application files, data, identity, certificates, service and web configuration.
        paths = [str(release).lstrip("/"), "opt/alshifa/current", "opt/alshifa/api",
                 "etc/alshifa", "etc/nginx", "etc/systemd/system/alshifa-api.service",
                 "etc/systemd/system/alshifa-api.service.d", "var/lib/alshifa",
                 "etc/letsencrypt"]
        paths = [p for p in paths if Path("/", p).exists()]
        with open(base / "archive-private.log", "wb") as log:
            done = subprocess.run(["tar", "-czf", str(base / "installation.tar.gz"), "-C", "/", *paths], stdout=log, stderr=log)
            if done.returncode:
                raise RuntimeError("Installation backup did not complete consistently.")
        restored_files = base / "files-restored"
        restored_files.mkdir(mode=0o700)
        # This archive was just created by us. Retain all file modes and symlinks.
        run(["tar", "-xzf", str(base / "installation.tar.gz"), "-C", str(restored_files)])
        with tarfile.open(base / "installation.tar.gz") as source:
            verified = 0
            for member in source:
                if member.isfile():
                    restored = restored_files / member.name
                    if hashlib.sha256(source.extractfile(member).read()).digest() != hashlib.sha256(restored.read_bytes()).digest():
                        raise RuntimeError("File restore verification failed.")
                    verified += 1
        result["restoredFiles"] = verified
        result["fileRestoreVerified"] = True

        # Independently preserve database account definitions without returning any hashes or credentials.
        accounts = sql("SELECT CONCAT(QUOTE(User),'@',QUOTE(Host)) FROM mysql.user WHERE User LIKE 'alshifa%' ORDER BY User,Host")
        statements = []
        account_grants = []
        for account in accounts:
            # Authentication salts can contain CR/LF/TAB. Never split a CREATE USER
            # result into lines or normalize its newlines; restore with --binary-mode.
            raw_definition = run(["docker", "exec", "-i", "alshifa-mysql", "sh", "-c",
                'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot -N -B --raw --binary-mode'],
                input=("SHOW CREATE USER " + account).encode()).rstrip(b"\n")
            definition = raw_definition if raw_definition.startswith(b"CREATE USER ") else raw_definition.split(b"\t", 1)[-1]
            statements.append(definition + b";")
            account_grants.extend(row + ";" for row in sql("SHOW GRANTS FOR " + account, raw=True))
        (base / "accounts-private.sql").write_bytes(b"\n".join(statements) + b"\n" + "\n".join(account_grants).encode())
        (base / "accounts-grants-private.sql").write_text("\n".join(account_grants))

        candidate = base / "candidate"
        candidate.mkdir(mode=0o700)
        with tarfile.open(archive) as source:
            for member in source:
                if member.name.startswith("/") or ".." in Path(member.name).parts or member.issym() or member.islnk():
                    raise RuntimeError("Unsafe candidate archive entry.")
            source.extractall(candidate, filter="data")
        binary = candidate / "AlShifa.Api"
        binary.chmod(0o700)
        if not (candidate / "wwwroot/index.html").is_file():
            raise RuntimeError("Candidate frontend is missing.")

        port = 33486
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", port))
        password = secrets.token_hex(32)
        (base / "mysql.env").write_text("MYSQL_ROOT_PASSWORD=" + password + "\nMYSQL_ROOT_HOST=%\n")
        image = run(["docker", "inspect", "-f", "{{.Config.Image}}", "alshifa-mysql"]).decode().strip()
        run(["docker", "run", "-d", "--name", container, "--label", "alshifa.preparation=" + args.id,
             "-p", "127.0.0.1:" + str(port) + ":3306", "--env-file", str(base / "mysql.env"),
             "-v", str(base / "mysql") + ":/var/lib/mysql", image, "--event-scheduler=OFF"])
        created = True
        info = json.loads(run(["docker", "inspect", container]))[0]
        if info["Config"]["Labels"]["alshifa.preparation"] != args.id or info["HostConfig"]["PortBindings"]["3306/tcp"][0]["HostIp"] != "127.0.0.1":
            raise RuntimeError("Clone isolation verification failed.")
        ready = False
        for _ in range(90):
            time.sleep(1)
            try:
                sql("SELECT 1", container)
                ready = True
                break
            except RuntimeError:
                pass
        if not ready:
            raise RuntimeError("Private clone did not become ready.")
        sql("CREATE DATABASE alshifa_mirror CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci", container)
        with gzip.open(base / "database.sql.gz", "rb") as source, open(base / "restore-private.log", "wb") as log:
            child = subprocess.Popen(["docker", "exec", "-i", container, "sh", "-c", 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot alshifa_mirror'], stdin=subprocess.PIPE, stdout=log, stderr=log)
            shutil.copyfileobj(source, child.stdin)
            child.stdin.close()
            if child.wait():
                raise RuntimeError("Isolated database restore failed.")
        if before != proof(container, columns):
            raise RuntimeError("Restored database differs from the verified source snapshot.")
        result["databaseRestoreVerified"] = True
        result["tablesVerified"] = len(columns)
        result["oldSchema"] = sql("SELECT MigrationId FROM alshifa_mirror.__EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1", container)[0]
        original_history = sql("SELECT MigrationId FROM alshifa_mirror.__EFMigrationsHistory ORDER BY MigrationId", container)

        # Some existing mirrors already have the complete round DDL but no history entry.
        # Never rerun ADD COLUMN or drop a populated round to make the old migration fit.
        # Adopt only this exact, fully verified physical shape, and only on the isolated clone.
        round_id = "20261120160000_QueueRoundColumns"
        if round_id not in original_history:
            expected_indexes = {
                "Visits": ("IX_Visits_QueueDay_Shift_Round_Department_Number", "VisitDate,QueueShift,QueueRound,DepartmentId,QueueNumber"),
                "VisitTokens": ("IX_VisitTokens_TokenDate_QueueShift_QueueRound_QueueNumber", "TokenDate,QueueShift,QueueRound,QueueNumber"),
            }
            round_columns = sql("SELECT TABLE_NAME,DATA_TYPE,IS_NULLABLE,COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='alshifa_mirror' AND TABLE_NAME IN ('Visits','VisitTokens','PatientCalls') AND COLUMN_NAME='QueueRound' ORDER BY TABLE_NAME", container)
            if round_columns:
                if sorted(round_columns) != sorted(table + "\tint\tNO\t1" for table in ["Visits", "VisitTokens", "PatientCalls"]):
                    raise RuntimeError("Partially applied or incompatible round columns require manual review.")
                for table, (index, names) in expected_indexes.items():
                    actual = sql("SELECT NON_UNIQUE,GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA='alshifa_mirror' AND TABLE_NAME='" + table + "' AND INDEX_NAME='" + index + "' GROUP BY NON_UNIQUE", container)
                    if actual != ["0\t" + names]:
                        raise RuntimeError("The round index shape is incompatible; history was not adopted.")
                legacy = sql("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA='alshifa_mirror' AND INDEX_NAME IN ('IX_Visits_VisitDate_QueueShift_DepartmentId_QueueNumber','IX_VisitTokens_TokenDate_QueueShift_QueueNumber')", container)
                if legacy != ["0"]:
                    raise RuntimeError("Legacy queue indexes still need reviewed reconciliation.")
                for table in ["Visits", "VisitTokens", "PatientCalls"]:
                    if sql("SELECT COUNT(*) FROM alshifa_mirror." + quote(table) + " WHERE QueueRound < 1", container) != ["0"]:
                        raise RuntimeError("Existing round values require reviewed reconciliation.")
                assert container.startswith("alshifa-upgrade-") and container != "alshifa-mysql"
                sql("INSERT INTO alshifa_mirror.__EFMigrationsHistory (MigrationId,ProductVersion) VALUES ('" + round_id + "','10.0.12')", container)
                result["queueRoundHistoryAdoptedOnClone"] = True

        env = {k: v for k, v in os.environ.items() if not k.startswith(("ConnectionStrings__", "Mirror__", "Sync__", "ASPNETCORE_", "DOTNET_"))}
        env.update({"ConnectionStrings__Default": "Server=127.0.0.1;Port=" + str(port) + ";Database=alshifa_mirror;User=root;Password=" + password + ";Pooling=false;",
                    "ASPNETCORE_ENVIRONMENT": "Production", "Mirror__Enabled": "true",
                    "Storage__Path": str(base / "isolated-storage"), "DataProtection__KeysPath": str(base / "isolated-keys")})
        with open(base / "migration-private.log", "wb") as log:
            done = subprocess.run([str(binary), "--migrate", "--backup-taken", "--contentRoot", str(candidate)], cwd=candidate, env=env, stdout=log, stderr=log)
        if done.returncode:
            raise RuntimeError("Candidate migration rehearsal failed; production was not changed.")
        preserved_columns = {table: names for table, names in columns.items() if table != "__EFMigrationsHistory"}
        new_history = sql("SELECT MigrationId FROM alshifa_mirror.__EFMigrationsHistory ORDER BY MigrationId", container)
        if not set(original_history).issubset(new_history):
            raise RuntimeError("The migration history lost an existing entry.")
        result["migrationsRehearsed"] = sorted(set(new_history) - set(original_history))
        after = proof(container, preserved_columns)
        if any(after[table] != before[table] for table in preserved_columns):
            raise RuntimeError("Migration changed existing row values; manual review is required.")
        result["existingRowsUnchanged"] = True
        result["newSchema"] = sql("SELECT MigrationId FROM alshifa_mirror.__EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1", container)[0]
        result["candidateSha256"] = args.sha256
        result["candidateApiSha256"] = hashlib.sha256((candidate / "AlShifa.Api.dll").read_bytes()).hexdigest()
        api_port = 33487
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", api_port))
        sync_key = secrets.token_hex(32)
        env.update({"Mirror__SyncKey": sync_key, "Mirror__SyncConnection": env["ConnectionStrings__Default"]})
        with open(base / "startup-private.log", "wb") as log:
            api = subprocess.Popen([str(binary), "--urls", "http://127.0.0.1:" + str(api_port), "--contentRoot", str(candidate)], cwd=candidate, env=env, stdout=log, stderr=log)
        for _ in range(60):
            if api.poll() is not None:
                raise RuntimeError("Candidate API stopped during clone startup.")
            try:
                with urllib.request.urlopen("http://127.0.0.1:" + str(api_port) + "/health", timeout=2) as response:
                    if response.status == 200:
                        break
            except Exception:
                time.sleep(1)
        else:
            raise RuntimeError("Candidate health check failed on the restored clone.")
        path = "/api/sync/mirror/state"
        timestamp = str(int(time.time()))
        message = timestamp + "\nGET\n" + path + "\n" + hashlib.sha256(b"").hexdigest().upper()
        signature = hmac.new(sync_key.encode(), message.encode(), hashlib.sha256).hexdigest().upper()
        request = urllib.request.Request("http://127.0.0.1:" + str(api_port) + path, headers={"X-Sync-Time": timestamp, "X-Sync-Signature": signature})
        with urllib.request.urlopen(request, timeout=10) as response:
            state = json.load(response)
        if state.get("protocolVersion") != 2 or state.get("schema") != result["newSchema"]:
            raise RuntimeError("Candidate protocol/schema did not match the rehearsed database.")
        result["candidateHealthVerified"] = True
        result["candidateProtocolVersion"] = state["protocolVersion"]
        result["sourceEnrollmentNeeded"] = state.get("sourceId") is None
        result["passed"] = True
    except Exception as error:
        result["failure"] = str(error)
        raise
    finally:
        if api is not None and api.poll() is None:
            api.terminate()
            try:
                api.wait(timeout=15)
            except subprocess.TimeoutExpired:
                api.kill()
                api.wait()
        if created:
            inspected = json.loads(run(["docker", "inspect", container]))[0]
            if inspected["Config"]["Labels"].get("alshifa.preparation") == args.id:
                run(["docker", "stop", container])
        result["completedAt"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        result["evidencePath"] = str(base)
        (base / "result.json").write_text(json.dumps(result, indent=2))
        # Only non-sensitive status leaves this VPS. Hashes are of binaries, never patient rows.
        print(json.dumps(result))


if __name__ == "__main__":
    main()
