#!/usr/bin/env python3
"""Read-only follow-up checks on restored schemas and the currently public old API."""
import argparse
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
import time


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--id', required=True)
    args = p.parse_args()
    if os.geteuid() or not re.fullmatch(r'[0-9]{8}-[a-f0-9]{8}', args.id):
        raise RuntimeError('Invalid evidence target.')
    os.umask(0o077)
    lock = open('/run/lock/alshifa-deploy.lock', 'a')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    base = Path('/var/backups/alshifa/redeployments') / args.id
    result = json.loads((base / 'result.json').read_text())
    required = ['bothDatabaseRestoresVerified', 'installationRestoreVerified', 'rollbackStartupVerified',
                'sharedPermissionFixVerified', 'activeRowsPreservedByMigrations', 'candidateServiceSandboxVerified',
                'reconciliationSafeForInPlaceUpgrade', 'bothSourcesStillMatchBackups', 'activeReleaseUnchanged']
    if not all(result.get(k) for k in required) or result.get('productionChanged'):
        raise RuntimeError('A passing read-only preparation is required.')
    clone = 'alshifa-redeploy-' + args.id
    info = json.loads(subprocess.check_output(['docker', 'inspect', clone]))[0]
    if info['Config']['Labels'].get('alshifa.redeployment') != args.id:
        raise RuntimeError('Isolated clone ownership mismatch.')
    active, retained = result['activeDatabase'], result['retainedDatabase']
    if active != 'alshifa_rb_20261123_73ad9e20' or retained != 'alshifa_mirror':
        raise RuntimeError('Unreviewed database pair.')

    def sql(query, target=clone):
        if not query.startswith('SELECT '):
            raise RuntimeError('Verification is read-only.')
        done = subprocess.run(['docker', 'exec', '-i', target, 'sh', '-c',
            'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot -N -B --binary-as-hex'], input=query.encode(), capture_output=True)
        if done.returncode:
            raise RuntimeError('Private metadata query failed.')
        return done.stdout.decode().splitlines()

    def shape(db):
        return [sql(q) for q in [
            "SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,IFNULL(HEX(COLUMN_DEFAULT),'NULL'),EXTRA,IFNULL(COLLATION_NAME,''),GENERATION_EXPRESSION FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='" + db + "' ORDER BY TABLE_NAME,ORDINAL_POSITION",
            "SELECT TABLE_NAME,INDEX_NAME,NON_UNIQUE,SEQ_IN_INDEX,COLUMN_NAME,IFNULL(SUB_PART,0),INDEX_TYPE FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA='" + db + "' ORDER BY TABLE_NAME,INDEX_NAME,SEQ_IN_INDEX",
            "SELECT TABLE_NAME,CONSTRAINT_NAME,COLUMN_NAME,ORDINAL_POSITION,IFNULL(REFERENCED_TABLE_NAME,''),IFNULL(REFERENCED_COLUMN_NAME,'') FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA='" + db + "' ORDER BY TABLE_NAME,CONSTRAINT_NAME,ORDINAL_POSITION",
            "SELECT TABLE_NAME,CONSTRAINT_NAME,UNIQUE_CONSTRAINT_NAME,UPDATE_RULE,DELETE_RULE,REFERENCED_TABLE_NAME FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='" + db + "' ORDER BY TABLE_NAME,CONSTRAINT_NAME",
        ]]

    try:
        subprocess.run(['docker', 'start', clone], check=True, stdout=subprocess.DEVNULL)
        for _ in range(60):
            try:
                sql('SELECT 1')
                break
            except RuntimeError:
                time.sleep(1)
        else:
            raise RuntimeError('Private clone unavailable.')
        if shape(active) != shape(retained):
            raise RuntimeError('Restored column types, keys or relationships differ.')
        result['schemaConstraintsMatched'] = True
        history = sql('SELECT MigrationId FROM `' + active + '`.__EFMigrationsHistory ORDER BY MigrationId')
        before = sql('SELECT MigrationId FROM alshifa_rehearsal_rollback.__EFMigrationsHistory ORDER BY MigrationId')
        if not set(before).issubset(history):
            raise RuntimeError('Prior migration history was lost.')
        result['migrationsRehearsed'] = sorted(set(history) - set(before))
        if result['migrationsRehearsed'] != ['20261120160000_QueueRoundColumns', '20261121120000_UserChat', '20261122151006_OperationsAndStaffPayroll']:
            raise RuntimeError('Unexpected migration set.')
        previous = Path(result['previousRelease'])
        restored = base / 'files-restored' / str(previous).lstrip('/') / 'AlShifa.Api.dll'
        previous_hash = hashlib.sha256((previous / 'AlShifa.Api.dll').read_bytes()).hexdigest()
        if hashlib.sha256(restored.read_bytes()).hexdigest() != previous_hash:
            raise RuntimeError('Active binary changed after installation backup.')
        result['previousApiSha256'] = previous_hash
        stage = Path('/opt/alshifa/releases') / ('rehearsal-' + args.id)
        result['candidateApiSha256'] = hashlib.sha256((stage / 'AlShifa.Api.dll').read_bytes()).hexdigest()
        archive = Path('/var/tmp/alshifa-prepare-20261123-ae3131ad/candidate.tar.gz')
        if hashlib.sha256(archive.read_bytes()).hexdigest() != result['candidateSha256']:
            raise RuntimeError('Frozen candidate archive changed.')
        with tarfile.open(archive) as tar:
            entries = [m for m in tar if m.isfile() and m.name.lstrip('./') == 'AlShifa.Api.dll']
            if len(entries) != 1 or hashlib.sha256(tar.extractfile(entries[0]).read()).hexdigest() != result['candidateApiSha256']:
                raise RuntimeError('Rehearsed candidate is not the frozen archive.')
        pending = {}
        for db in [active, retained]:
            values = sql("SELECT COUNT(*),COALESCE(SUM(`Schema` <> '20261122151006_OperationsAndStaffPayroll'),0) FROM `" + db + "`.SyncCommands WHERE CompletedAt IS NULL", 'alshifa-mysql')[0].split('\t')
            pending[db] = {'pending': int(values[0]), 'pendingWithOldSchema': int(values[1])}
        result['pendingCommands'] = pending
        result['pendingOldSchemaCommandsNeedReviewBeforeSync'] = any(v['pendingWithOldSchema'] for v in pending.values())
        # Test the actual TLS verifier without changing the existing public API.
        spec = importlib.util.spec_from_file_location('approved_cutover', Path(__file__).with_name('apply-prepared-online-upgrade.py'))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        pid = subprocess.check_output(['systemctl', 'show', 'alshifa-api.service', '-p', 'MainPID', '--value'], text=True).strip()
        env = dict(s.split('=', 1) for s in Path('/proc', pid, 'environ').read_bytes().decode().split('\0') if '=' in s)
        key = env.get('Mirror__SyncKey') or Path('/etc/alshifa/sync-key.txt').read_text().strip()
        result['currentPublicProtocolVersion'] = module.check_public_tls_route(key, result['oldSchema'])
        result['publicTLSVerifierTested'] = True
        result['backupPermissionsVerified'] = base.stat().st_mode & 0o777 == 0o700 and all(p.stat().st_mode & 0o777 == 0o600 for p in base.iterdir() if p.is_file())
        if not result['backupPermissionsVerified']:
            raise RuntimeError('Private backup permission check failed.')
        result['passed'] = True
    except Exception:
        result['passed'] = False
        raise
    finally:
        subprocess.run(['docker', 'stop', clone], capture_output=True)
        (base / 'result.json').write_text(json.dumps(result, indent=2))
        print(json.dumps(result))


if __name__ == '__main__':
    main()
