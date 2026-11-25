#!/usr/bin/env python3
"""Approved cutover only; no primary database DROP/restore, no source enrollment.

Requires a fresh, restore-tested preparation with the identical frozen candidate.
Stops public access before checking that the source still equals that backup.
Any pre-open failure fails over to a separate restored database and old release.
"""
import argparse
import datetime
import fcntl
import gzip
import hashlib
import hmac
import json
import os
import pwd
from pathlib import Path
import re
import shutil
import subprocess
import time
import urllib.request
import urllib.error
import http.client
import socket
import ssl


def prepare_release_access(release, service_user, run):
    """Grant only the service group access; exercise the actual service identity."""
    if service_user != 'alshifa':
        raise RuntimeError('Unexpected service identity.')
    service_group = pwd.getpwnam(service_user).pw_gid
    run(['chown', '-R', 'root:' + str(service_group), str(release)])
    run(['chmod', '-R', 'u=rwX,g=rX,o=', str(release)])
    (release / 'AlShifa.Api').chmod(0o750)
    for entry in [release, *release.rglob('*')]:
        mode = '-x' if entry.is_dir() or entry.name == 'AlShifa.Api' else '-r'
        run(['runuser', '-u', service_user, '--', 'test', mode, str(entry)])


def check_public_tls_route(sync_key, schema):
    """Use the public TLS vhost over loopback while public writes stay closed."""
    host = 'shafa.novatechsoft.com'

    class LocalTLS(http.client.HTTPSConnection):
        def connect(self):
            self.sock = self._context.wrap_socket(socket.create_connection(('127.0.0.1', 443), timeout=10), server_hostname=host)

    def request(path, headers=None):
        client = LocalTLS(host, timeout=10, context=ssl.create_default_context())
        try:
            client.request('GET', path, headers=headers or {})
            response = client.getresponse()
            return response.status, response.read()
        finally:
            client.close()

    if request('/login')[0] != 200 or request('/api/auth/me')[0] != 401:
        raise RuntimeError('Public TLS login/session route verification failed.')
    path = '/api/sync/mirror/state'
    if request(path)[0] not in [401, 403]:
        raise RuntimeError('Unsigned public TLS Sync was accepted.')
    stamp = str(int(time.time()))
    message = stamp + '\nGET\n' + path + '\n' + hashlib.sha256(b'').hexdigest().upper()
    signature = hmac.new(sync_key.encode(), message.encode(), hashlib.sha256).hexdigest().upper()
    status, body = request(path, {'X-Sync-Time': stamp, 'X-Sync-Signature': signature})
    state = json.loads(body)
    if status != 200 or state.get('protocolVersion', 1) not in [1, 2] or state.get('schema') != schema:
        raise RuntimeError('Public TLS signed state/schema verification failed.')
    return state.get('protocolVersion', 1)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--preparation', required=True)
    p.add_argument('--redeployment', action='store_true',
                   help='Use a reconciled dual backup and upgrade the current failover database in place.')
    p.add_argument('--check-only', action='store_true',
                   help='Validate evidence and current source equality without stopping or changing production.')
    args = p.parse_args()
    if os.geteuid() != 0 or not re.fullmatch(r'[0-9]{8}-[a-f0-9]{8}', args.preparation):
        raise RuntimeError('Invalid approved target.')
    os.umask(0o077)
    lock = open('/run/lock/alshifa-deploy.lock', 'a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    base = Path('/var/backups/alshifa/redeployments' if args.redeployment else '/var/backups/alshifa/preparations') / args.preparation
    ready = json.loads((base / 'result.json').read_text())
    flags = (['passed', 'bothDatabaseRestoresVerified', 'installationRestoreVerified',
              'activeRowsPreservedByMigrations', 'rollbackStartupVerified', 'sharedPermissionFixVerified',
              'candidateServiceSandboxVerified', 'bothSourcesStillMatchBackups', 'schemaConstraintsMatched', 'publicTLSVerifierTested',
              'reconciliationSafeForInPlaceUpgrade']
             if args.redeployment else ['passed', 'databaseRestoreVerified', 'fileRestoreVerified',
                                       'existingRowsUnchanged', 'rollbackStartupVerified', 'applicationAccountsRestoreVerified'])
    for flag in flags:
        if not ready.get(flag):
            raise RuntimeError('Fresh preparation/rollback proof missing.')
    active_db = ready['activeDatabase'] if args.redeployment else 'alshifa_mirror'
    if active_db not in ['alshifa_mirror', 'alshifa_rb_20261123_73ad9e20']:
        raise RuntimeError('Unexpected reviewed source database.')
    database_backup = base / (active_db + '.sql.gz' if args.redeployment else 'database.sql.gz')
    if time.time() - database_backup.stat().st_mtime > 3600:
        raise RuntimeError('Preparation is too old for cutover.')
    if args.redeployment:
        for name, item in json.loads((base / 'backup-manifest.json').read_text()).items():
            file = base / name
            if file.stat().st_size != item['size'] or hashlib.sha256(file.read_bytes()).hexdigest() != item['sha256']:
                raise RuntimeError('A verified dual backup changed.')
    old = Path(ready['previousRelease'])
    if Path('/opt/alshifa/current').resolve() != old or Path('/opt/alshifa/api').resolve() != old:
        raise RuntimeError('Active release changed after preparation.')
    if hashlib.sha256((old / 'AlShifa.Api.dll').read_bytes()).hexdigest() != ready['previousApiSha256']:
        raise RuntimeError('Previous binary changed.')
    nginx = Path('/etc/nginx/sites-available/shafa.novatechsoft.com')
    original_nginx = nginx.read_bytes()
    report = {'preparation': args.preparation, 'productionChanged': False, 'passed': False,
              'publicWritesReopened': False, 'rollbackRequired': False}
    maintenance = False
    mutated = False
    stopped = False
    new = Path('/opt/alshifa/releases') / ('protocol2-' + args.preparation)

    def run(cmd, **kw):
        done = subprocess.run(cmd, capture_output=True, **kw)
        if done.returncode:
            with open(base / 'cutover-command-private.log', 'ab') as log:
                log.write(done.stderr)
            raise RuntimeError('Cutover command failed; protected VPS evidence retained.')
        return done.stdout

    def sql(query):
        # Every production DDL/metadata operation targets the CURRENT source.
        # The retained database is inspected explicitly only by proof().
        if args.redeployment:
            # Never rewrite user names in GRANT statements.
            query = query.replace("TABLE_SCHEMA='alshifa_mirror'", "TABLE_SCHEMA='" + active_db + "'")
            query = query.replace('alshifa_mirror.', active_db + '.')
        return run(['docker', 'exec', '-i', 'alshifa-mysql', 'sh', '-c',
                    'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot -N -B --binary-as-hex --binary-mode'],
                   input=query.encode()).decode().splitlines()

    def proof(columns, database=None):
        database = database or active_db
        values = {}
        for table, names in columns.items():
            if args.redeployment:
                from collections import Counter
                expr = ["IFNULL(CONCAT('V',HEX(CAST(`" + c + "` AS BINARY))),'N')" for c in names]
                query = "SELECT SHA2(CONCAT_WS('|'," + ','.join(expr) + "),256) FROM `" + database + '`.`' + table + '`'
                # Do not apply the DDL target adapter when reading the retained DB.
                output = run(['docker', 'exec', '-i', 'alshifa-mysql', 'sh', '-c',
                              'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot -N -B --binary-as-hex --binary-mode'], input=query.encode()).decode().splitlines()
                values[table] = hashlib.sha256(json.dumps(sorted(Counter(output).items())).encode()).hexdigest()
            else:
                quoted = ','.join('`' + n.replace('`', '``') + '`' for n in names)
                rows = sql('SELECT ' + quoted + ' FROM `' + database + '`.`' + table.replace('`', '``') + '`')
                values[table] = hashlib.sha256(json.dumps(sorted(rows), ensure_ascii=True).encode()).hexdigest()
        return values

    def switch(release):
        for link in ['current', 'api']:
            tmp = Path('/opt/alshifa') / (link + '.cutover-' + args.preparation)
            tmp.symlink_to(release)
            tmp.replace(Path('/opt/alshifa') / link)

    def health():
        for _ in range(60):
            try:
                with urllib.request.urlopen('http://127.0.0.1:5080/health', timeout=2) as r:
                    if r.status == 200:
                        return
            except Exception:
                time.sleep(1)
        raise RuntimeError('API health failed.')

    def assert_live_database(database):
        pid = run(['systemctl', 'show', 'alshifa-api.service', '-p', 'MainPID', '--value']).decode().strip()
        runtime = dict(v.split('=', 1) for v in Path('/proc', pid, 'environ').read_bytes().decode().split('\0') if '=' in v)
        for name in ['ConnectionStrings__Default', 'Mirror__SyncConnection']:
            if not all(re.search(pattern, runtime.get(name, ''), re.I) for pattern in
                       [r'(?:^|;)Database=' + re.escape(database) + r'(?:;|$)',
                        r'(?:^|;)Server=127\.0\.0\.1(?:;|$)', r'(?:^|;)Port=3316(?:;|$)']):
                raise RuntimeError('Effective reader/writer database mismatch; maintenance retained.')

    def signed_state():
        path = '/api/sync/mirror/state'
        stamp = str(int(time.time()))
        msg = stamp + '\nGET\n' + path + '\n' + hashlib.sha256(b'').hexdigest().upper()
        key = effective.get('Mirror__SyncKey') or Path('/etc/alshifa/sync-key.txt').read_text().strip()
        sig = hmac.new(key.encode(), msg.encode(), hashlib.sha256).hexdigest().upper()
        request = urllib.request.Request('http://127.0.0.1:5080' + path,
            headers={'X-Sync-Time': stamp, 'X-Sync-Signature': sig})
        with urllib.request.urlopen(request, timeout=10) as r:
            return json.load(r)

    def reopen():
        nginx.write_bytes(original_nginx)
        run(['nginx', '-t'])
        run(['systemctl', 'reload', 'nginx'])

    pid = run(['systemctl', 'show', 'alshifa-api.service', '-p', 'MainPID', '--value']).decode().strip()
    effective = dict(v.split('=', 1) for v in Path('/proc', pid, 'environ').read_bytes().decode().split('\0') if '=' in v)
    for name in ['ConnectionStrings__Default', 'Mirror__SyncConnection']:
        if not all(re.search(pattern, effective.get(name, ''), re.I) for pattern in
                   [r'(?:^|;)Database=' + re.escape(active_db) + r'(?:;|$)', r'(?:^|;)Server=127\.0\.0\.1(?:;|$)', r'(?:^|;)Port=3316(?:;|$)']):
            raise RuntimeError('Unexpected effective database target.')
    all_columns = json.loads((base / 'layouts-private.json').read_text()) if args.redeployment else None
    all_proofs = json.loads((base / 'proofs-private.json').read_text()) if args.redeployment else None
    columns = all_columns[active_db] if args.redeployment else json.loads((base / 'database-layout.json').read_text())
    expected = all_proofs[active_db] if args.redeployment else json.loads((base / 'database-proof.json').read_text())
    if args.check_only:
        if proof(columns) != expected:
            raise RuntimeError('Active source changed after reconciliation.')
        if args.redeployment and any(proof(cols, db) != all_proofs[db] for db, cols in all_columns.items()):
            raise RuntimeError('A reconciled database changed after backup.')
        assert_live_database(active_db)
        source = (Path('/opt/alshifa/releases') / ('rehearsal-' + args.preparation)) if args.redeployment else base / 'candidate'
        if hashlib.sha256((source / 'AlShifa.Api.dll').read_bytes()).hexdigest() != ready['candidateApiSha256']:
            raise RuntimeError('Rehearsed candidate changed.')
        print(json.dumps({'checkOnly': True, 'passed': True, 'productionChanged': False,
                          'activeDatabase': active_db, 'bothSourcesMatchVerifiedBackups': bool(args.redeployment),
                          'candidateUnchanged': True}))
        return
    try:
        # A server-level barrier also covers Sync batches and online commands.
        if original_nginx.count(b'server {') != 2:
            raise RuntimeError('Unexpected nginx configuration.')
        (base / 'nginx-before-cutover.conf').write_bytes(original_nginx)
        nginx.write_bytes(original_nginx.replace(b'server {', b'server {\n    if ($remote_addr != 127.0.0.1) { return 503; }'))
        maintenance = True
        run(['nginx', '-t'])
        run(['systemctl', 'reload', 'nginx'])
        run(['systemctl', 'stop', 'alshifa-api.service'])
        stopped = True
        if proof(columns) != expected:
            raise RuntimeError('Source changed after fresh backup; cutover refused before migration.')
        if args.redeployment and any(proof(cols, db) != all_proofs[db] for db, cols in all_columns.items()):
            raise RuntimeError('One of the reconciled databases changed; deployment refused before migration.')
        report['finalBackupMatchesQuiescedProduction'] = True
        report['backupRestoreVerified'] = True
        candidate_source = (Path('/opt/alshifa/releases') / ('rehearsal-' + args.preparation)) if args.redeployment else base / 'candidate'
        shutil.copytree(candidate_source, new)
        # Preserve existing installation-specific configuration; candidate has no secrets.
        for config in old.glob('appsettings*.json'):
            shutil.copy2(config, new / config.name)
        service_user = run(['systemctl', 'show', 'alshifa-api.service', '-p', 'User', '--value']).decode().strip()
        # Root-only rehearsal cannot prove access by the actual systemd identity.
        # Refuse BEFORE migration if any runtime/content file is inaccessible.
        prepare_release_access(new, service_user, run)
        report['serviceIdentityFileAccessVerified'] = True
        report['release'] = str(new)
        if hashlib.sha256((new / 'AlShifa.Api.dll').read_bytes()).hexdigest() != ready['candidateApiSha256']:
            raise RuntimeError('Staged binary mismatch.')
        # Exact adoption checks used in the successful isolated rehearsal.
        actual = sql("SELECT TABLE_NAME,DATA_TYPE,IS_NULLABLE,COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='alshifa_mirror' AND TABLE_NAME IN ('Visits','VisitTokens','PatientCalls') AND COLUMN_NAME='QueueRound' ORDER BY TABLE_NAME")
        if sorted(actual) != sorted(t + '\tint\tNO\t1' for t in ['Visits', 'VisitTokens', 'PatientCalls']):
            raise RuntimeError('Round column drift; no history adoption.')
        indexes = {'Visits': ('IX_Visits_QueueDay_Shift_Round_Department_Number', 'VisitDate,QueueShift,QueueRound,DepartmentId,QueueNumber'),
                   'VisitTokens': ('IX_VisitTokens_TokenDate_QueueShift_QueueRound_QueueNumber', 'TokenDate,QueueShift,QueueRound,QueueNumber')}
        for table, (index, names) in indexes.items():
            if sql("SELECT NON_UNIQUE,GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA='alshifa_mirror' AND TABLE_NAME='" + table + "' AND INDEX_NAME='" + index + "' GROUP BY NON_UNIQUE") != ['0\t' + names]:
                raise RuntimeError('Round index drift.')
        if sql("SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA='alshifa_mirror' AND INDEX_NAME IN ('IX_Visits_VisitDate_QueueShift_DepartmentId_QueueNumber','IX_VisitTokens_TokenDate_QueueShift_QueueNumber')") != ['0']:
            raise RuntimeError('Legacy queue index remains.')
        for table in ['Visits', 'VisitTokens', 'PatientCalls']:
            if sql('SELECT COUNT(*) FROM alshifa_mirror.`' + table + '` WHERE QueueRound < 1') != ['0']:
                raise RuntimeError('Invalid existing round.')
        history = sql('SELECT MigrationId FROM alshifa_mirror.__EFMigrationsHistory ORDER BY MigrationId')
        if history[-1] != ready['oldSchema'] or '20261120160000_QueueRoundColumns' in history:
            raise RuntimeError('Migration history drift.')
        mutated = True
        report['productionChanged'] = True
        sql("INSERT INTO alshifa_mirror.__EFMigrationsHistory (MigrationId,ProductVersion) VALUES ('20261120160000_QueueRoundColumns','10.0.12')")
        root_password = run(['docker', 'exec', 'alshifa-mysql', 'printenv', 'MYSQL_ROOT_PASSWORD']).decode().strip()
        env = dict(effective)
        env['ConnectionStrings__Default'] = 'Server=127.0.0.1;Port=3316;Database=' + active_db + ';User=root;Password=' + root_password + ';Pooling=false;'
        with open(base / 'production-migration-private.log', 'wb') as log:
            done = subprocess.run([str(new / 'AlShifa.Api'), '--migrate', '--backup-taken', '--contentRoot', str(new)], cwd=new, env=env, stdout=log, stderr=log)
        if done.returncode:
            raise RuntimeError('Production migration failed.')
        new_history = sql('SELECT MigrationId FROM alshifa_mirror.__EFMigrationsHistory')
        if not set(history).issubset(new_history):
            raise RuntimeError('Existing migration history entry disappeared.')
        changed = sorted(set(new_history) - set(history))
        expected_migrations = (['20261120160000_QueueRoundColumns', '20261121120000_UserChat',
                                '20261122151006_OperationsAndStaffPayroll'] if args.redeployment else ready['migrationsRehearsed'])
        if changed != sorted(expected_migrations):
            raise RuntimeError('Unexpected migration set.')
        protected = {t: c for t, c in columns.items() if t != '__EFMigrationsHistory'}
        if proof(protected) != {t: expected[t] for t in protected}:
            raise RuntimeError('Existing rows changed during migration.')
        switch(new)
        run(['systemctl', 'start', 'alshifa-api.service'])
        stopped = False
        health()
        assert_live_database(active_db)
        state = signed_state()
        if state.get('protocolVersion') != 2 or state.get('schema') != ready['newSchema']:
            raise RuntimeError('Protocol/schema verification failed.')
        if proof(protected) != {t: expected[t] for t in protected}:
            raise RuntimeError('Existing rows changed during startup.')
        try:
            urllib.request.urlopen('http://127.0.0.1:5080/api/sync/mirror/state', timeout=5)
            raise RuntimeError('Unsigned sync unexpectedly accepted.')
        except urllib.error.HTTPError as error:
            if error.code not in [401, 403]:
                raise
        report.update({'protocolVersion': 2, 'schema': state['schema'],
                       'sourceEnrollmentNeeded': state.get('sourceId') is None,
                       'existingRowsUnchanged': True, 'migrationsApplied': changed,
                       'healthVerified': True, 'unsignedSyncRejected': True})
        sync_key = effective.get('Mirror__SyncKey') or Path('/etc/alshifa/sync-key.txt').read_text().strip()
        if check_public_tls_route(sync_key, ready['newSchema']) != 2:
            raise RuntimeError('Public TLS virtual host is not using protocol 2.')
        report['publicTLSProtocol2VerifiedBeforeReopen'] = True
        reopen()
        maintenance = False
        report['publicWritesReopened'] = True
        report['passed'] = True
    except Exception as error:
        report['failure'] = str(error)
        if mutated:
            report['rollbackRequired'] = True
            run(['systemctl', 'stop', 'alshifa-api.service'])
            rollback_db = 'alshifa_rb_' + args.preparation.replace('-', '_')
            # Restore into a NEW database; never drop or replace the primary.
            sql('CREATE DATABASE `' + rollback_db + '` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci')
            with gzip.open(database_backup, 'rb') as source, open(base / 'failover-restore-private.log', 'wb') as log:
                child = subprocess.Popen(['docker', 'exec', '-i', 'alshifa-mysql', 'sh', '-c',
                    'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot ' + rollback_db], stdin=subprocess.PIPE, stdout=log, stderr=log)
                shutil.copyfileobj(source, child.stdin)
                child.stdin.close()
                if child.wait():
                    raise RuntimeError('Failover restore failed; maintenance retained.')
            if proof(columns, rollback_db) != expected:
                raise RuntimeError('Failover restore proof failed; maintenance retained.')
            grants = (base / ('grants-private.sql' if args.redeployment else 'accounts-grants-private.sql')).read_text().splitlines()
            sql('\n'.join(g.replace('`' + active_db + '`', '`' + rollback_db + '`') for g in grants if 'ON `' + active_db + '`.' in g))
            config = Path('/etc/alshifa') / ('rollback-' + args.preparation + '.env')
            lines = []
            for name in ['ConnectionStrings__Default', 'Mirror__SyncConnection']:
                value = re.sub(r'(?i)(\bDatabase=)[^;]+', r'\g<1>' + rollback_db, effective[name])
                lines.append(name + '=' + json.dumps(value))
            config.write_text('\n'.join(lines) + '\n')
            config.chmod(0o600)
            # Must sort AFTER mirror.conf, which also sets SyncConnection.
            dropin = Path('/etc/systemd/system/alshifa-api.service.d') / ('zzzz-approved-rollback-' + args.preparation + '.conf')
            if dropin.exists():
                raise RuntimeError('Existing rollback override; maintenance retained.')
            dropin.parent.mkdir(exist_ok=True)
            dropin.write_text('[Service]\nEnvironmentFile=' + str(config) + '\n')
            switch(old)
            run(['systemctl', 'daemon-reload'])
            run(['systemctl', 'start', 'alshifa-api.service'])
            health()
            assert_live_database(rollback_db)
            if signed_state().get('schema') != ready['oldSchema']:
                raise RuntimeError('Failover schema check failed; maintenance retained.')
            report.update({'rollbackSucceeded': True, 'rollbackDatabase': rollback_db,
                           'originalDatabaseRetained': True})
        elif stopped:
            run(['systemctl', 'start', 'alshifa-api.service'])
            health()
        if maintenance:
            reopen()
            maintenance = False
        raise
    finally:
        report['completedAt'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        report['maintenanceRetained'] = maintenance
        (base / 'deployment-result.json').write_text(json.dumps(report, indent=2))
        print(json.dumps(report))


if __name__ == '__main__':
    main()
