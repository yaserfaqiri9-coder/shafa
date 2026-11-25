#!/usr/bin/env python3
"""Verify old binary + restored database/accounts, confined to a preparation container."""
import argparse
import gzip
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--preparation", required=True)
    args = parser.parse_args()
    if os.geteuid() != 0 or not re.fullmatch(r"[0-9]{8}-[a-f0-9]{8}", args.preparation):
        raise RuntimeError("Invalid preparation target.")
    os.umask(0o077)
    base = Path("/var/backups/alshifa/preparations") / args.preparation
    result = json.loads((base / "result.json").read_text())
    if not result.get("candidateHealthVerified") or not result.get("existingRowsUnchanged") or result.get("productionChanged"):
        raise RuntimeError("A passing private preparation is required.")
    container = "alshifa-upgrade-" + args.preparation
    info = json.loads(subprocess.check_output(["docker", "inspect", container]))[0]
    if info["Config"]["Labels"].get("alshifa.preparation") != args.preparation:
        raise RuntimeError("Preparation container ownership mismatch.")
    api = None

    def sql(query):
        done = subprocess.run(["docker", "exec", "-i", container, "sh", "-c",
            'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot -N -B --binary-mode'], input=query.encode(), capture_output=True)
        if done.returncode:
            (base / "rollback-sql-private.log").write_bytes(done.stderr)
            raise RuntimeError("Clone-only rollback SQL check failed.")
        return done.stdout.decode().splitlines()

    try:
        subprocess.run(["docker", "start", container], check=True, stdout=subprocess.DEVNULL)
        for _ in range(60):
            try:
                sql("SELECT 1")
                break
            except RuntimeError:
                time.sleep(1)
        else:
            raise RuntimeError("Rollback clone did not become ready.")
        rollback_db = "alshifa_rb_" + secrets.token_hex(4)
        sql("CREATE DATABASE " + rollback_db + " CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci")
        with gzip.open(base / "database.sql.gz", "rb") as source, open(base / "rollback-restore-private.log", "wb") as log:
            child = subprocess.Popen(["docker", "exec", "-i", container, "sh", "-c",
                'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot ' + rollback_db], stdin=subprocess.PIPE, stdout=log, stderr=log)
            while block := source.read(1024 * 1024):
                child.stdin.write(block)
            child.stdin.close()
            if child.wait():
                raise RuntimeError("Rollback database restore failed.")
        account_sql = (base / "accounts-private.sql").read_bytes().decode()
        grant_sql = (base / "accounts-grants-private.sql").read_bytes().decode()
        if not account_sql.endswith(grant_sql):
            raise RuntimeError("Account backup structure is unexpected.")
        definitions = account_sql[:-len(grant_sql)]
        sql(definitions.replace("CREATE USER ", "CREATE USER IF NOT EXISTS "))
        # Archived databases are not part of the active mirror. Keep their grants
        # in the backup, but verify only active-database/global grants on this clone.
        active_grants = [line for line in grant_sql.splitlines() if "ON `alshifa_mirror`." in line or "ON *.*" in line]
        sql("\n".join(active_grants))
        grants = [line.replace("`alshifa_mirror`", "`" + rollback_db + "`") for line in active_grants]
        sql("\n".join(grants))
        result["applicationAccountsRestoreVerified"] = True

        pid = subprocess.check_output(["systemctl", "show", "alshifa-api.service", "-p", "MainPID", "--value"], text=True).strip()
        production = dict(v.split("=", 1) for v in Path("/proc", pid, "environ").read_bytes().decode().split("\0") if "=" in v)
        def redirect(connection):
            if (not re.search(r"(?i)(?:^|;)Database=alshifa_mirror(?:;|$)", connection)
                or not re.search(r"(?i)(?:^|;)Server=127\.0\.0\.1(?:;|$)", connection)
                or not re.search(r"(?i)(?:^|;)Port=3316(?:;|$)", connection)):
                raise RuntimeError("Unexpected production database configuration.")
            changed = re.sub(r"(?i)(\bPort=)[^;]+", r"\g<1>33486", connection)
            changed = re.sub(r"(?i)(\bDatabase=)[^;]+", r"\g<1>" + rollback_db, changed)
            return changed

        env = {k: v for k, v in os.environ.items() if not k.startswith(("ConnectionStrings__", "Mirror__", "Sync__", "ASPNETCORE_", "DOTNET_"))}
        sync_key = secrets.token_hex(32)
        env.update({"ConnectionStrings__Default": redirect(production["ConnectionStrings__Default"]),
                    "Mirror__SyncConnection": redirect(production["Mirror__SyncConnection"]),
                    "Mirror__Enabled": "true", "Mirror__SyncKey": sync_key, "Sync__Url": "",
                    "ASPNETCORE_ENVIRONMENT": "Production", "ASPNETCORE_URLS": "http://127.0.0.1:33487",
                    "Storage__Path": str(base / "rollback-storage"), "DataProtection__KeysPath": str(base / "rollback-keys")})
        root = base / "rollback-content"
        root.mkdir(mode=0o700, exist_ok=True)
        old = Path(result["previousRelease"]) / "AlShifa.Api"
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 33487))
        with open(base / "rollback-startup-private.log", "wb") as log:
            api = subprocess.Popen([str(old), "--contentRoot", str(root)], cwd=root, env=env, stdout=log, stderr=log)
        for _ in range(60):
            if api.poll() is not None:
                raise RuntimeError("The old binary failed against the restored rollback database.")
            try:
                with urllib.request.urlopen("http://127.0.0.1:33487/health", timeout=2) as response:
                    if response.status == 200:
                        break
            except Exception:
                time.sleep(1)
        else:
            raise RuntimeError("Rollback health check did not pass.")
        path = "/api/sync/mirror/state"
        stamp = str(int(time.time()))
        message = stamp + "\nGET\n" + path + "\n" + hashlib.sha256(b"").hexdigest().upper()
        signature = hmac.new(sync_key.encode(), message.encode(), hashlib.sha256).hexdigest().upper()
        request = urllib.request.Request("http://127.0.0.1:33487" + path, headers={"X-Sync-Time": stamp, "X-Sync-Signature": signature})
        with urllib.request.urlopen(request, timeout=10) as response:
            state = json.load(response)
        if state["schema"] != result["oldSchema"]:
            raise RuntimeError("Rollback did not restore the old schema.")
        result["rollbackStartupVerified"] = True
        result["rollbackUsedRestoredApplicationAccounts"] = True
        result["rollbackDidNotDropProductionDatabase"] = True
        result["passed"] = True
    except Exception:
        result["passed"] = False
        result["rollbackStartupVerified"] = False
        raise
    finally:
        if api is not None and api.poll() is None:
            api.terminate()
            try:
                api.wait(timeout=15)
            except subprocess.TimeoutExpired:
                api.kill()
                api.wait()
        subprocess.run(["docker", "stop", container], check=True, stdout=subprocess.DEVNULL)
        (base / "result.json").write_text(json.dumps(result, indent=2))
        print(json.dumps(result))


if __name__ == "__main__":
    main()
