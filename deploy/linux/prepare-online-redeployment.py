#!/usr/bin/env python3
"""Read-only dual production backup/reconciliation; migrations/startups only on a clone.

Never merges rows, changes production accounts, stops the live service or changes
its configuration. Sensitive evidence stays root-only on the VPS.
"""
import argparse
from collections import Counter
import datetime
import fcntl
import gzip
import hashlib
import hmac
import importlib.util
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import subprocess
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--id', required=True)
    parser.add_argument('--archive', required=True)
    parser.add_argument('--sha256', required=True)
    args = parser.parse_args()
    if os.geteuid() or not re.fullmatch(r'[0-9]{8}-[a-f0-9]{8}', args.id):
        raise RuntimeError('Root and a valid identifier required.')
    os.umask(0o077)
    lock = open('/run/lock/alshifa-deploy.lock', 'a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    archive = Path(args.archive).resolve()
    if hashlib.sha256(archive.read_bytes()).hexdigest() != args.sha256:
        raise RuntimeError('Frozen candidate hash mismatch.')
    base = Path('/var/backups/alshifa/redeployments') / args.id
    base.mkdir(parents=True, exist_ok=False, mode=0o700)
    result = dict(id=args.id, productionChanged=False, passed=False, candidateSha256=args.sha256)
    clone = 'alshifa-redeploy-' + args.id
    clone_created = False
    unit = 'alshifa-rehearsal-' + args.id
    stage = Path('/opt/alshifa/releases') / ('rehearsal-' + args.id)
    work = Path('/var/lib/alshifa') / ('rehearsal-' + args.id)

    def run(cmd, **kw):
        done = subprocess.run(cmd, capture_output=True, **kw)
        if done.returncode:
            with open(base / 'commands-private.log', 'ab') as log:
                log.write(done.stderr)
            raise RuntimeError('Preparation command failed; inspect protected VPS evidence.')
        return done.stdout

    def sql(query, target='alshifa-mysql'):
        if target == 'alshifa-mysql' and not query.lstrip().upper().startswith(('SELECT ', 'SHOW ')):
            raise RuntimeError('Production SQL is read-only.')
        return run(['docker', 'exec', '-i', target, 'sh', '-c',
                    'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot -N -B --binary-as-hex --binary-mode'
                    + (' --protocol=TCP --host=127.0.0.1' if target != 'alshifa-mysql' else '')], input=query.encode()).decode().splitlines()

    def layout(database, target='alshifa-mysql'):
        tables = sql("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='" + database + "' AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME", target)
        return {t: sql("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='" + database + "' AND TABLE_NAME='" + t + "' ORDER BY ORDINAL_POSITION", target) for t in tables}

    def rows(database, table, cols, target):
        expr = ["IFNULL(CONCAT('V',HEX(CAST(`" + c + "` AS BINARY))),'N')" for c in cols]
        hashes = sql("SELECT SHA2(CONCAT_WS('|'," + ','.join(expr) + "),256) FROM `" + database + '`.`' + table + '`', target)
        return Counter(hashes)

    def proof(database, columns, target='alshifa-mysql'):
        return {t: hashlib.sha256(json.dumps(sorted(rows(database, t, cols, target).items())).encode()).hexdigest() for t, cols in columns.items()}

    def schema_shape(database):
        # Exclude counters/cardinality; retain types, defaults, keys and relations.
        queries = [
            "SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,IFNULL(HEX(COLUMN_DEFAULT),'NULL'),EXTRA,IFNULL(COLLATION_NAME,''),GENERATION_EXPRESSION FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='" + database + "' ORDER BY TABLE_NAME,ORDINAL_POSITION",
            "SELECT TABLE_NAME,INDEX_NAME,NON_UNIQUE,SEQ_IN_INDEX,COLUMN_NAME,IFNULL(SUB_PART,0),INDEX_TYPE FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA='" + database + "' ORDER BY TABLE_NAME,INDEX_NAME,SEQ_IN_INDEX",
            "SELECT TABLE_NAME,CONSTRAINT_NAME,COLUMN_NAME,ORDINAL_POSITION,IFNULL(REFERENCED_TABLE_NAME,''),IFNULL(REFERENCED_COLUMN_NAME,'') FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA='" + database + "' ORDER BY TABLE_NAME,CONSTRAINT_NAME,ORDINAL_POSITION",
            "SELECT TABLE_NAME,CONSTRAINT_NAME,UNIQUE_CONSTRAINT_NAME,UPDATE_RULE,DELETE_RULE,REFERENCED_TABLE_NAME FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='" + database + "' ORDER BY TABLE_NAME,CONSTRAINT_NAME",
        ]
        return [sql(q, clone) for q in queries]

    def restore(database, backup):
        sql('CREATE DATABASE `' + database + '` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci', clone)
        with gzip.open(backup, 'rb') as source, open(base / 'restore-private.log', 'ab') as log:
            child = subprocess.Popen(['docker', 'exec', '-i', clone, 'sh', '-c',
                'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot ' + database], stdin=subprocess.PIPE, stdout=log, stderr=log)
            shutil.copyfileobj(source, child.stdin)
            child.stdin.close()
            if child.wait():
                raise RuntimeError('Private database restore failed.')

    def api_check(binary, content, environment, expected_protocol):
        # PID1 reads the root-only file; credentials never appear in command arguments.
        envfile = base / ('api-' + str(expected_protocol) + '.env')
        envfile.write_text('\n'.join(k + '=' + json.dumps(v) for k, v in environment.items()) + '\n')
        cmd = ['systemd-run', '--unit=' + unit, '--collect', '--property=Type=exec',
               '--property=User=alshifa', '--property=WorkingDirectory=' + str(content),
               '--property=EnvironmentFile=' + str(envfile), '--property=ProtectSystem=strict',
               '--property=ProtectHome=true', '--property=PrivateTmp=true', '--property=NoNewPrivileges=true',
               '--property=ReadWritePaths=' + str(work), str(binary)]
        try:
            run(cmd)
            for _ in range(60):
                try:
                    with urllib.request.urlopen('http://127.0.0.1:33489/health', timeout=2) as r:
                        if r.status == 200:
                            break
                except Exception:
                    time.sleep(1)
            else:
                raise RuntimeError('API failed under the service user and systemd sandbox.')
            path = '/api/sync/mirror/state'
            stamp = str(int(time.time()))
            message = stamp + '\nGET\n' + path + '\n' + hashlib.sha256(b'').hexdigest().upper()
            sig = hmac.new(environment['Mirror__SyncKey'].encode(), message.encode(), hashlib.sha256).hexdigest().upper()
            req = urllib.request.Request('http://127.0.0.1:33489' + path, headers={'X-Sync-Time': stamp, 'X-Sync-Signature': sig})
            with urllib.request.urlopen(req, timeout=10) as response:
                state = json.load(response)
            if state.get('protocolVersion', 1) != expected_protocol:
                raise RuntimeError('Unexpected protocol on restored database.')
            return state
        finally:
            subprocess.run(['systemctl', 'stop', unit], capture_output=True)

    try:
        if shutil.disk_usage(base).free < 4 * 1024**3:
            raise RuntimeError('Insufficient space for independent backup verification.')
        previous = Path('/opt/alshifa/api').resolve()
        if previous != Path('/opt/alshifa/current').resolve() or not str(previous).startswith('/opt/alshifa/releases/'):
            raise RuntimeError('Unexpected active release topology.')
        pid = run(['systemctl', 'show', 'alshifa-api.service', '-p', 'MainPID', '--value']).decode().strip()
        effective = dict(x.split('=', 1) for x in Path('/proc', pid, 'environ').read_bytes().decode().split('\0') if '=' in x)
        sources = []
        for name in ['ConnectionStrings__Default', 'Mirror__SyncConnection']:
            cs = effective[name]
            if not re.search(r'(?i)(?:^|;)Server=127\.0\.0\.1;', cs) or not re.search(r'(?i)(?:^|;)Port=3316;', cs):
                raise RuntimeError('Unexpected live MySQL endpoint.')
            match = re.search(r'(?i)(?:^|;)Database=([^;]+)', cs)
            sources.append(match.group(1))
        if sources != ['alshifa_rb_20261123_73ad9e20'] * 2:
            raise RuntimeError('The active database changed; review the live topology.')
        active = sources[0]
        original = 'alshifa_mirror'
        databases = [active, original]
        result.update(activeDatabase=active, retainedDatabase=original, previousRelease=str(previous))
        result['previousApiSha256'] = hashlib.sha256((previous / 'AlShifa.Api.dll').read_bytes()).hexdigest()
        layouts, proofs = {}, {}
        for db in databases:
            layouts[db] = layout(db)
            proofs[db] = proof(db, layouts[db])
            with gzip.open(base / (db + '.sql.gz'), 'wb') as out, open(base / 'dump-private.log', 'ab') as log:
                child = subprocess.Popen(['docker', 'exec', 'alshifa-mysql', 'sh', '-c',
                    'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysqldump --no-defaults -uroot --single-transaction --quick --routines --events --triggers --hex-blob --no-tablespaces --set-gtid-purged=OFF ' + db], stdout=subprocess.PIPE, stderr=log)
                shutil.copyfileobj(child.stdout, out)
                child.stdout.close()
                if child.wait():
                    raise RuntimeError('Dual database backup failed.')
            if proof(db, layouts[db]) != proofs[db]:
                raise RuntimeError('Source changed during backup; repeat the read-only preparation.')
        (base / 'layouts-private.json').write_text(json.dumps(layouts))
        (base / 'proofs-private.json').write_text(json.dumps(proofs))
        paths = [str(previous).lstrip('/'), 'opt/alshifa/api', 'opt/alshifa/current', 'etc/alshifa', 'etc/nginx',
                 'etc/systemd/system/alshifa-api.service', 'etc/systemd/system/alshifa-api.service.d', 'var/lib/alshifa', 'etc/letsencrypt']
        run(['tar', '-czf', str(base / 'installation.tar.gz'), '-C', '/', *paths])
        files = base / 'files-restored'
        files.mkdir(mode=0o700)
        run(['tar', '-xzf', str(base / 'installation.tar.gz'), '-C', str(files)])
        # Compare every regular archived file to its restored bytes, never output them.
        import tarfile
        with tarfile.open(base / 'installation.tar.gz') as tar:
            for entry in tar:
                if entry.isfile() and hashlib.sha256(tar.extractfile(entry).read()).digest() != hashlib.sha256((files / entry.name).read_bytes()).digest():
                    raise RuntimeError('Installation file restore differs.')
        result['installationRestoreVerified'] = True
        definitions, grants = [], []
        for account in sql("SELECT CONCAT(QUOTE(User),'@',QUOTE(Host)) FROM mysql.user WHERE User LIKE 'alshifa%' ORDER BY User,Host"):
            raw = run(['docker', 'exec', '-i', 'alshifa-mysql', 'sh', '-c',
                'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot -N -B --raw --binary-mode'], input=('SHOW CREATE USER ' + account).encode()).rstrip(b'\n')
            definitions.append((raw if raw.startswith(b'CREATE USER ') else raw.split(b'\t', 1)[-1]) + b';')
            grants.extend(g + ';' for g in sql('SHOW GRANTS FOR ' + account))
        (base / 'accounts-private.sql').write_bytes(b'\n'.join(definitions))
        (base / 'grants-private.sql').write_text('\n'.join(grants))
        for port in [33488, 33489]:
            with socket.socket() as probe:
                probe.bind(('127.0.0.1', port))
        password = secrets.token_hex(32)
        (base / 'mysql.env').write_text('MYSQL_ROOT_PASSWORD=' + password + '\nMYSQL_ROOT_HOST=%\n')
        image = run(['docker', 'inspect', '-f', '{{.Config.Image}}', 'alshifa-mysql']).decode().strip()
        run(['docker', 'run', '-d', '--name', clone, '--label', 'alshifa.redeployment=' + args.id,
             '-p', '127.0.0.1:33488:3306', '--env-file', str(base / 'mysql.env'), '-v', str(base / 'mysql') + ':/var/lib/mysql', image, '--event-scheduler=OFF'])
        clone_created = True
        info = json.loads(run(['docker', 'inspect', clone]))[0]
        if info['Config']['Labels'].get('alshifa.redeployment') != args.id or info['HostConfig']['PortBindings']['3306/tcp'][0]['HostIp'] != '127.0.0.1':
            raise RuntimeError('Private clone isolation failed.')
        for _ in range(90):
            try:
                sql('SELECT 1', clone)
                break
            except RuntimeError:
                time.sleep(1)
        else:
            raise RuntimeError('Private clone is unavailable.')
        for db in databases:
            restore(db, base / (db + '.sql.gz'))
            if proof(db, layouts[db], clone) != proofs[db]:
                raise RuntimeError('Restored database differs from its backup source.')
        result['bothDatabaseRestoresVerified'] = True
        # Preserve salts byte-for-byte and test with the real restricted app accounts.
        sql((base / 'accounts-private.sql').read_bytes().decode(), clone)
        current_grants = [g for g in grants if 'ON *.*' in g or any('ON `' + db + '`.' in g for db in databases)]
        sql('\n'.join(current_grants), clone)
        # Separate rollback database is tested with the actual old binary.
        rollback = 'alshifa_rehearsal_rollback'
        restore(rollback, base / (active + '.sql.gz'))
        sql('\n'.join(g.replace('`' + active + '`', '`' + rollback + '`') for g in current_grants if 'ON `' + active + '`.' in g), clone)
        work.mkdir(mode=0o750)
        run(['chown', 'alshifa', str(work)])
        def environment(db):
            env = {k: v for k, v in effective.items() if k.startswith(('ALSHIFA_', 'DOTNET_'))}
            for key in ['ConnectionStrings__Default', 'Mirror__SyncConnection']:
                env[key] = re.sub(r'(?i)(\bPort=)[^;]+', r'\g<1>33488', effective[key])
                env[key] = re.sub(r'(?i)(\bDatabase=)[^;]+', r'\g<1>' + db, env[key])
            env.update(ASPNETCORE_ENVIRONMENT='Production', ASPNETCORE_URLS='http://127.0.0.1:33489',
                       Mirror__Enabled='true', Mirror__SyncKey=secrets.token_hex(32), Sync__Url='',
                       Storage__Path=str(work / 'storage'), DataProtection__KeysPath=str(work / 'keys'))
            return env
        oldstate = api_check(previous / 'AlShifa.Api', previous, environment(rollback), 1)
        result.update(rollbackStartupVerified=True, oldSchema=oldstate['schema'])
        # Extract only this approved, frozen application, never installation secrets.
        stage.mkdir(mode=0o750)
        with tarfile.open(archive) as tar:
            for entry in tar:
                if entry.name.startswith('/') or '..' in Path(entry.name).parts or entry.issym() or entry.islnk():
                    raise RuntimeError('Unsafe candidate entry.')
            tar.extractall(stage, filter='data')
        for config in previous.glob('appsettings*.json'):
            shutil.copy2(config, stage / config.name)
        spec = importlib.util.spec_from_file_location('approved_cutover', Path(__file__).with_name('apply-prepared-online-upgrade.py'))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        # Negative regression: root:root 750 reproduces the old permission failure.
        run(['chown', 'root:root', str(stage)])
        stage.chmod(0o750)
        denied = subprocess.run(['runuser', '-u', 'alshifa', '--', 'test', '-x', str(stage)], capture_output=True)
        if denied.returncode == 0:
            raise RuntimeError('Permission regression fixture did not reproduce failure.')
        module.prepare_release_access(stage, 'alshifa', run)
        result.update(permissionRegressionReproduced=True, sharedPermissionFixVerified=True,
                      candidateApiSha256=hashlib.sha256((stage / 'AlShifa.Api.dll').read_bytes()).hexdigest())
        # Adopt only the already-complete QueueRound shape, exactly as rehearsed.
        query = "SELECT TABLE_NAME,DATA_TYPE,IS_NULLABLE,COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='" + active + "' AND TABLE_NAME IN ('Visits','VisitTokens','PatientCalls') AND COLUMN_NAME='QueueRound' ORDER BY TABLE_NAME"
        if sorted(sql(query, clone)) != sorted(t + '\tint\tNO\t1' for t in ['Visits', 'VisitTokens', 'PatientCalls']):
            raise RuntimeError('Round columns incompatible on restored active database.')
        for table, index, names in [('Visits', 'IX_Visits_QueueDay_Shift_Round_Department_Number', 'VisitDate,QueueShift,QueueRound,DepartmentId,QueueNumber'), ('VisitTokens', 'IX_VisitTokens_TokenDate_QueueShift_QueueRound_QueueNumber', 'TokenDate,QueueShift,QueueRound,QueueNumber')]:
            q = "SELECT NON_UNIQUE,GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA='" + active + "' AND TABLE_NAME='" + table + "' AND INDEX_NAME='" + index + "' GROUP BY NON_UNIQUE"
            if sql(q, clone) != ['0\t' + names]:
                raise RuntimeError('Round indexes incompatible.')
        if sql("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA='" + active + "' AND INDEX_NAME IN ('IX_Visits_VisitDate_QueueShift_DepartmentId_QueueNumber','IX_VisitTokens_TokenDate_QueueShift_QueueNumber')", clone) != ['0']:
            raise RuntimeError('Unexpected legacy round indexes.')
        for table in ['Visits', 'VisitTokens', 'PatientCalls']:
            if sql('SELECT COUNT(*) FROM `' + active + '`.`' + table + '` WHERE QueueRound<1', clone) != ['0']:
                raise RuntimeError('Unexpected round values.')
        history = sql('SELECT MigrationId FROM `' + active + '`.__EFMigrationsHistory ORDER BY MigrationId', clone)
        if history[-1] != oldstate['schema'] or '20261120160000_QueueRoundColumns' in history:
            raise RuntimeError('Unexpected active history.')
        sql("INSERT INTO `" + active + "`.__EFMigrationsHistory VALUES ('20261120160000_QueueRoundColumns','10.0.12')", clone)
        migration_env = dict(os.environ)
        migration_env.update(environment(active))
        migration_env['ConnectionStrings__Default'] = 'Server=127.0.0.1;Port=33488;Database=' + active + ';User=root;Password=' + password + ';Pooling=false;'
        with open(base / 'migration-private.log', 'wb') as log:
            done = subprocess.run([str(stage / 'AlShifa.Api'), '--migrate', '--backup-taken', '--contentRoot', str(stage)], cwd=stage, env=migration_env, stdout=log, stderr=log)
        if done.returncode:
            raise RuntimeError('Candidate migration rehearsal failed.')
        new_history = sql('SELECT MigrationId FROM `' + active + '`.__EFMigrationsHistory ORDER BY MigrationId', clone)
        result['migrationsRehearsed'] = sorted(set(new_history) - set(history))
        protected = {t: c for t, c in layouts[active].items() if t != '__EFMigrationsHistory'}
        if proof(active, protected, clone) != {t: proofs[active][t] for t in protected}:
            raise RuntimeError('Migration changed existing active rows.')
        result['activeRowsPreservedByMigrations'] = True
        state = api_check(stage / 'AlShifa.Api', stage, environment(active), 2)
        result.update(candidateServiceSandboxVerified=True, candidateProtocolVersion=2, newSchema=state['schema'], sourceEnrollmentNeeded=state.get('sourceId') is None)
        if proof(active, protected, clone) != {t: proofs[active][t] for t in protected}:
            raise RuntimeError('Candidate startup changed existing active rows.')
        upgraded_layout = layout(active, clone)
        if upgraded_layout != layout(original, clone):
            raise RuntimeError('Schemas still differ after rehearsed upgrade.')
        if schema_shape(active) != schema_shape(original):
            raise RuntimeError('Column types, keys or relationships differ after rehearsal.')
        result['schemaConstraintsMatched'] = True
        differences = {}
        original_only = 0
        active_only = 0
        for table, cols in upgraded_layout.items():
            a = rows(active, table, cols, clone)
            b = rows(original, table, cols, clone)
            extra_active = sum((a - b).values())
            extra_original = sum((b - a).values())
            if extra_active or extra_original:
                differences[table] = dict(activeOnlyRows=extra_active, retainedOnlyRows=extra_original)
            active_only += extra_active
            original_only += extra_original
        result.update(differencesAfterSchemaNormalization=differences, activeOnlyRows=active_only,
                      retainedOnlyRows=original_only, noAutomaticRowMerge=True,
                      recommendedTarget=active, reconciliationSafeForInPlaceUpgrade=original_only == 0)
        # Refuse stale evidence if either source changed while the clone was tested.
        result['bothSourcesStillMatchBackups'] = all(proof(db, layouts[db]) == proofs[db] for db in databases)
        result['activeReleaseUnchanged'] = Path('/opt/alshifa/api').resolve() == previous
        manifest = {}
        for name in [*(db + '.sql.gz' for db in databases), 'installation.tar.gz', 'accounts-private.sql', 'grants-private.sql']:
            p = base / name
            manifest[name] = dict(sha256=hashlib.sha256(p.read_bytes()).hexdigest(), size=p.stat().st_size)
        (base / 'backup-manifest.json').write_text(json.dumps(manifest, indent=2))
        result['passed'] = result['bothSourcesStillMatchBackups'] and result['activeReleaseUnchanged'] and result['reconciliationSafeForInPlaceUpgrade']
    except Exception as error:
        result['failure'] = str(error)
        raise
    finally:
        subprocess.run(['systemctl', 'stop', unit], capture_output=True)
        if clone_created:
            subprocess.run(['docker', 'stop', clone], capture_output=True)
        for p in base.iterdir():
            if p.is_file():
                p.chmod(0o600)
        result['completedAt'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        result['evidencePath'] = str(base)
        (base / 'result.json').write_text(json.dumps(result, indent=2))
        print(json.dumps(result))


if __name__ == '__main__':
    main()
