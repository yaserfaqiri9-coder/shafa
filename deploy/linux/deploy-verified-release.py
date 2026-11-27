"""Guarded AlShifa release cutover; same-schema mode keeps production SQL read-only.

Preparation proves database and installation restore plus candidate startup in a
dedicated MySQL container under the real service user. Activation holds the
public vhost, stops the API, proves unchanged data, swaps only release links,
and verifies before reopening. The explicit ledger-index mode rehearses the one
allowlisted migration and a full backup restore before activating it. Its failure
restores the verified stopped-state backup and the old binary before reopening.
"""
import argparse, datetime, fcntl, gzip, hashlib, hmac, http.client, json, os
from pathlib import Path
import re, secrets, shutil, socket, ssl, subprocess, tarfile, time
import urllib.request, urllib.error

SCHEMA=None
VERSION=None
SERVICE='alshifa-api.service'
HOST='shafa.novatechsoft.com'
PERFORMANCE_PREVIOUS='20261125105153_OpticalOrderCustomerName'
PERFORMANCE_SCHEMA='20261127080000_PerformanceIndexes'
def digest(path):
    h=hashlib.sha256()
    with open(path,'rb') as f:
        for b in iter(lambda:f.read(1024*1024),b''): h.update(b)
    return h.hexdigest()
def main():
    global SCHEMA, VERSION
    p=argparse.ArgumentParser()
    p.add_argument('--id',required=True);p.add_argument('--archive',required=True);p.add_argument('--sha256',required=True)
    p.add_argument('--activate',action='store_true')
    p.add_argument('--performance-index-upgrade',action='store_true')
    p.add_argument('--version',required=True);p.add_argument('--schema',required=True);p.add_argument('--expected-database',required=True)
    a=p.parse_args();VERSION=a.version;SCHEMA=a.schema
    if a.performance_index_upgrade and SCHEMA!=PERFORMANCE_SCHEMA:raise RuntimeError('Only the rehearsed ledger index migration is allowed')
    if not re.fullmatch(r'\d+\.\d+\.\d+\.\d+', VERSION) or not re.fullmatch(r'20\d{12}_[A-Za-z0-9_]+', SCHEMA) or not re.fullmatch(r'alshifa_(mirror|rb_[0-9_a-f]+)', a.expected_database):raise RuntimeError('Invalid release identity')
    if os.geteuid() or not re.fullmatch(r'20\d{6}-[a-f0-9]{8}',a.id): raise RuntimeError('Unexpected deployment identity')
    os.umask(0o077)
    lock=open('/run/lock/alshifa-deploy.lock','a');fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
    archive=Path(a.archive).resolve()
    if digest(archive)!=a.sha256: raise RuntimeError('Frozen release hash mismatch')
    base=Path('/var/backups/alshifa/current-updates')/a.id
    if not a.activate: base.mkdir(parents=True,exist_ok=False,mode=0o700)
    elif not base.is_dir(): raise RuntimeError('Verified preparation missing')
    report=dict(id=a.id,phase='activate' if a.activate else 'prepare',passed=False,productionChanged=False,productionDatabaseWrites=False)
    def run(args,**kw):
        done=subprocess.run(args,capture_output=True,**kw)
        if done.returncode:
            with open(base/'commands-private.log','ab') as log:log.write(done.stderr)
            raise RuntimeError('Protected deployment command failed')
        return done.stdout
    def effective():
        pid=run(['systemctl','show',SERVICE,'-p','MainPID','--value']).decode().strip()
        if not pid.isdigit() or pid=='0': raise RuntimeError('Live API is not running')
        return pid,dict(x.split('=',1) for x in Path('/proc',pid,'environ').read_bytes().decode().split('\0') if '=' in x)
    pid,env=effective()
    dbs=[]
    for key in ['ConnectionStrings__Default','Mirror__SyncConnection']:
        cs=env[key]
        if not re.search(r'(?i)(?:^|;)Server=127\.0\.0\.1;',cs) or not re.search(r'(?i)(?:^|;)Port=3316;',cs):raise RuntimeError('Unexpected live database endpoint')
        match=re.search(r'(?i)(?:^|;)Database=([^;]+)',cs)
        if not match:raise RuntimeError('Database mapping missing')
        dbs.append(match[1])
    if dbs!=[a.expected_database]*2: raise RuntimeError('Active database mapping changed')
    database=dbs[0]
    if env.get('ASPNETCORE_HOSTINGSTARTUPASSEMBLIES') or any(k.startswith('Kestrel__') for k in env):raise RuntimeError('Unexpected hosting overrides')
    old=Path('/opt/alshifa/api').resolve()
    if old!=Path('/opt/alshifa/current').resolve() or not str(old).startswith('/opt/alshifa/releases/'):raise RuntimeError('Release links disagree')
    if run(['systemctl','show',SERVICE,'-p','User','--value']).decode().strip()!='alshifa':raise RuntimeError('Unexpected service user')
    clone='alshifa-current-'+a.id
    unit='alshifa-current-check-'+a.id
    release=Path('/opt/alshifa/releases')/('current-'+a.id)
    work=Path('/var/lib/alshifa')/('current-check-'+a.id)
    def sql(query,target='alshifa-mysql'):
        if target=='alshifa-mysql' and not query.lstrip().upper().startswith(('SELECT ','SHOW ')):raise RuntimeError('Production SQL must be read-only')
        raw=run(['docker','exec','-i',target,'sh','-c','MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot -N -B --binary-mode'],input=query.encode())
        return raw.decode().splitlines()
    def layout(target='alshifa-mysql'):
        rows=sql("SELECT TABLE_NAME,COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='"+database+"' ORDER BY TABLE_NAME,ORDINAL_POSITION",target)
        result={}
        for line in rows:
            table,column=line.split('\t')
            if not re.fullmatch(r'[A-Za-z0-9_]+',table) or not re.fullmatch(r'[A-Za-z0-9_]+',column):raise RuntimeError('Unexpected schema identifier')
            result.setdefault(table,[]).append(column)
        if len(result)<75:raise RuntimeError('Incomplete source schema')
        return result
    def proof(columns,target='alshifa-mysql'):
        statements=[]
        for table,names in columns.items():
            values=["IFNULL(CONCAT('V',HEX(CAST(`"+n+"` AS BINARY))),'N')" for n in names]
            rowhash="SHA2(CONCAT_WS('|',"+','.join(values)+"),256)"
            sums=["COALESCE(SUM(CAST(CONV(SUBSTRING(h,"+str(i)+",16),16,10) AS DECIMAL(65,0))),0)" for i in [1,17,33,49]]
            statements.append("SELECT '"+table+"',COUNT(*),"+','.join(sums)+" FROM (SELECT "+rowhash+" AS h FROM `"+database+'`.`'+table+'`) p;')
        result={};batch=''
        for statement in statements+['']:
            if batch and (not statement or len(batch)+len(statement)>16000):
                for line in sql(batch,target):
                    table,value=line.split('\t',1)
                    if table not in columns or table in result or not re.fullmatch(r'\d+(\t\d+){4}',value):raise RuntimeError('Content proof incomplete')
                    result[table]=value
                batch=''
            batch+=statement
        if len(result)!=len(columns):raise RuntimeError('Content proof missing tables')
        return result
    def state(url,key,public=False):
        path='/api/sync/mirror/state';stamp=str(int(time.time()))
        message=stamp+'\nGET\n'+path+'\n'+hashlib.sha256(b'').hexdigest().upper()
        headers={'X-Sync-Time':stamp,'X-Sync-Signature':hmac.new(key.encode(),message.encode(),hashlib.sha256).hexdigest().upper()}
        if public:return json.loads(tls(path,headers)[1])
        with urllib.request.urlopen(urllib.request.Request(url+path,headers=headers),timeout=15) as r:return json.load(r)
    def tls(path,headers=None):
        class LocalTLS(http.client.HTTPSConnection):
            def connect(self):self.sock=self._context.wrap_socket(socket.create_connection(('127.0.0.1',443),timeout=10),server_hostname=HOST)
        c=LocalTLS(HOST,timeout=15,context=ssl.create_default_context())
        try:
            c.request('GET',path,headers=headers or {});r=c.getresponse();return r.status,r.read()
        finally:c.close()
    def health():
        for _ in range(60):
            try:
                with urllib.request.urlopen('http://127.0.0.1:5080/health',timeout=2) as r:
                    if r.status==200 and r.read()==b'Healthy':return
            except Exception:pass
            time.sleep(1)
        raise RuntimeError('Application health failed')
    def stable_env(values):
        return {k:v for k,v in values.items() if k.startswith(('ConnectionStrings__','Mirror__','Sync__','Storage__','DataProtection__','ASPNETCORE_','DOTNET_'))}
    schema=sql('SELECT MigrationId FROM `'+database+'`.__EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1')[0]
    if schema!=(PERFORMANCE_PREVIOUS if a.performance_index_upgrade else SCHEMA):raise RuntimeError('Unexpected previous schema; deployment refused')
    sync_key=env['Mirror__SyncKey']
    initial_state=state('http://127.0.0.1:5080',sync_key)
    if initial_state.get('protocolVersion')!=2 or not initial_state.get('sourceId') or schema not in initial_state.get('acceptedSchemas',[]):raise RuntimeError('Live Sync compatibility mismatch')
    status=base/('activation.json' if a.activate else 'preparation.json')
    container_created=False;maintenance=False;stopped=False;switched=False;migration_started=False
    compatibility=Path('/etc/systemd/system/'+SERVICE+'.d')/('70-index-schema-'+a.id+'.conf')
    compatibility_env=Path('/etc/alshifa')/('index-schema-'+a.id+'.env')
    accepted=list(dict.fromkeys([*initial_state.get('acceptedSchemas',[])[1:],schema]))
    if any(not re.fullmatch(r'20\d{12}_[A-Za-z0-9_]+',s) for s in accepted):raise RuntimeError('Unexpected compatibility schema')
    def compatible_env(values):
        result=dict(values)
        if a.performance_index_upgrade:
            result={k:v for k,v in result.items() if not k.startswith('Mirror__AcceptedSourceSchemas__')}
            result.update({'Mirror__AcceptedSourceSchemas__'+str(i):s for i,s in enumerate(accepted)})
        return result
    def same_business(before,after):
        def protected(p):return {k:v for k,v in p.items() if not a.performance_index_upgrade or k.lower()!='__efmigrationshistory'}
        return protected(before)==protected(after)
    def check_sync(after):
        if not a.performance_index_upgrade:return after==initial_state
        core=lambda value:{k:v for k,v in value.items() if k!='acceptedSchemas'}
        return core(after)==core(initial_state) and set([SCHEMA,*initial_state['acceptedSchemas']]).issubset(after.get('acceptedSchemas',[]))
    def restore_backup(target):
        # Only this deployment's hash-verified, already restored backup is accepted.
        if target=='alshifa-mysql' and (not a.performance_index_upgrade or not maintenance or not stopped):raise RuntimeError('Production restore requires the guarded stopped upgrade')
        with gzip.open(base/'database.sql.gz','rb') as src, open(base / f'restore-{target}-private.log', 'wb') as log:
            child=subprocess.Popen(['docker','exec','-i',target,'sh','-c','MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot '+database],stdin=subprocess.PIPE,stdout=log,stderr=log)
            shutil.copyfileobj(src,child.stdin);child.stdin.close()
            if child.wait():raise RuntimeError('Verified backup restore failed; maintenance retained')
    def owner_write(query):
        if not a.performance_index_upgrade or not maintenance or not stopped:raise RuntimeError('Migration account requires stopped, guarded index upgrade')
        return run(['docker','exec','-i','alshifa-mysql','sh','-c','MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot --binary-mode'],input=query.encode())
    def migrate(target,port,root_password=None):
        if not a.performance_index_upgrade:raise RuntimeError('Migration was not explicitly selected')
        username='root' if target==clone else 'alshifa_mg_'+a.id[-8:]
        password=root_password if target==clone else secrets.token_hex(32)
        created=False
        try:
            if target!='alshifa-mysql' and target!=clone:raise RuntimeError('Unexpected migration target')
            if target=='alshifa-mysql':
                owner_write("CREATE USER '"+username+"'@'%' IDENTIFIED BY '"+password+"';")
                created=True;report['productionDatabaseWrites']=True;report['productionChanged']=True
                owner_write("GRANT ALL PRIVILEGES ON `"+database+"`.* TO '"+username+"'@'%';")
            cs='Server=127.0.0.1;Port='+str(port)+';Database='+database+';User='+username+';Password='+password+';Pooling=false;'
            values=compatible_env({k:v for k,v in env.items() if k.startswith(('Mirror__','ASPNETCORE_','DOTNET_')) and not k.startswith('ASPNETCORE_HOSTINGSTARTUP')})
            values.update(ConnectionStrings__Default=cs,Mirror__SyncConnection=cs,Mirror__Enabled='true',Sync__Url='',ASPNETCORE_ENVIRONMENT='Production',DataProtection__KeysPath=str(work/'migration-keys'),Storage__Path=str(work/'migration-storage'))
            migration_env=base/('migration-'+target+'-private.env')
            migration_env.write_text('\n'.join(k+'='+json.dumps(v) for k,v in values.items())+'\n')
            run(['systemd-run','--quiet','--wait','--collect','--unit=alshifa-index-'+a.id+'-'+target,'--property=Type=exec','--property=User=alshifa','--property=WorkingDirectory='+str(release),'--property=EnvironmentFile='+str(migration_env),str(release/'AlShifa.Api'),'--migrate','--backup-taken'])
        finally:
            if created:
                owner_write("DROP USER '"+username+"'@'%';")
                report['temporaryMigrationAccountRemoved']=True
    def verify_index(columns,before,target):
        if layout(target)!=columns or not same_business(before,proof(columns,target)):raise RuntimeError('Index migration altered existing business rows or columns')
        history=sql('SELECT MigrationId FROM `'+database+'`.__EFMigrationsHistory ORDER BY MigrationId',target)
        if history!=history_before+[SCHEMA]:raise RuntimeError('Unexpected migration history change')
        actual=sql("SELECT INDEX_NAME,SEQ_IN_INDEX,COLUMN_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA='"+database+"' AND TABLE_NAME='LedgerEntries' ORDER BY INDEX_NAME,SEQ_IN_INDEX",target)
        expected=[x for x in indexes_before if not x.startswith('IX_LedgerEntries_AccountCode\t')]
        expected += ['IX_LedgerEntries_AccountCode_TransactionId_Debit_Credit\t'+str(i)+'\t'+name for i,name in enumerate(['AccountCode','TransactionId','Debit','Credit'],1)]
        if sorted(actual)!=sorted(expected):raise RuntimeError('Unexpected ledger index layout')
        reservations=sql("SELECT INDEX_NAME,SEQ_IN_INDEX,COLUMN_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA='"+database+"' AND TABLE_NAME='SaleItem' ORDER BY INDEX_NAME,SEQ_IN_INDEX",target)
        expected_reservations=reservation_indexes_before+['IX_SaleItem_BatchId_SaleId_Quantity\t'+str(i)+'\t'+name for i,name in enumerate(['BatchId','SaleId','Quantity'],1)]
        if sorted(reservations)!=sorted(expected_reservations):raise RuntimeError('Unexpected stock reservation index layout')
    history_before=sql('SELECT MigrationId FROM `'+database+'`.__EFMigrationsHistory ORDER BY MigrationId')
    indexes_before=sql("SELECT INDEX_NAME,SEQ_IN_INDEX,COLUMN_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA='"+database+"' AND TABLE_NAME='LedgerEntries' ORDER BY INDEX_NAME,SEQ_IN_INDEX")
    reservation_indexes_before=sql("SELECT INDEX_NAME,SEQ_IN_INDEX,COLUMN_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA='"+database+"' AND TABLE_NAME='SaleItem' ORDER BY INDEX_NAME,SEQ_IN_INDEX")
    nginx=Path('/etc/nginx/sites-available/'+HOST)
    nginx_before=nginx.read_bytes()
    def switch(target):
        for name in ['api','current']:
            temporary=Path('/opt/alshifa')/(name+'.current-'+a.id)
            if temporary.exists() or temporary.is_symlink():raise RuntimeError('Unexpected staging link')
            temporary.symlink_to(target);os.replace(temporary,Path('/opt/alshifa')/name)
    def reopen():
        nginx.write_bytes(nginx_before);run(['nginx','-t']);run(['systemctl','reload','nginx'])
    try:
        if not a.activate:
            if shutil.disk_usage(base).free<5*1024**3:raise RuntimeError('Insufficient backup space')
            columns=layout();before=proof(columns)
            backup=base/'database.sql.gz'
            with gzip.open(backup,'wb') as out,open(base/'dump-private.log','wb') as log:
                child=subprocess.Popen(['docker','exec','alshifa-mysql','sh','-c','MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysqldump --no-defaults -uroot --single-transaction --quick --routines --events --triggers --hex-blob --no-tablespaces --set-gtid-purged=OFF '+database],stdout=subprocess.PIPE,stderr=log)
                shutil.copyfileobj(child.stdout,out);child.stdout.close()
                if child.wait():raise RuntimeError('Production backup failed')
            if before!=proof(columns):raise RuntimeError('Source changed during backup; retry preparation')
            (base/'columns-private.json').write_text(json.dumps(columns));(base/'proof-private.json').write_text(json.dumps(before));(base/'environment-private.json').write_text(json.dumps(stable_env(env)))
            paths=[str(old).lstrip('/'),'opt/alshifa/api','opt/alshifa/current','etc/alshifa','etc/nginx','etc/systemd/system/alshifa-api.service','etc/systemd/system/alshifa-api.service.d','var/lib/alshifa','etc/letsencrypt']
            paths=[x for x in paths if Path('/',x).exists()]
            installation=base/'installation.tar.gz'
            run(['tar','-czf',str(installation),'-C','/',*paths])
            files=base/'files-restored';files.mkdir(mode=0o700)
            # Stream gzip once. Reopening an earlier member repeatedly would
            # decompress the entire prefix for each installed runtime file.
            with tarfile.open(installation,'r|gz') as tar:
                for entry in tar:
                    if not entry.isfile():continue
                    if Path(entry.name).is_absolute() or '..' in Path(entry.name).parts:raise RuntimeError('Unsafe backup path')
                    dest=files/entry.name;dest.parent.mkdir(parents=True,exist_ok=True)
                    h=hashlib.sha256()
                    with tar.extractfile(entry) as src,open(dest,'wb') as out:
                        for b in iter(lambda:src.read(1024*1024),b''):
                            h.update(b);out.write(b)
                    if h.hexdigest()!=digest(dest):raise RuntimeError('Installation restore verification failed')
            report['installationRestoreVerified']=True
            release.mkdir(mode=0o750)
            with tarfile.open(archive) as tar:
                for e in tar:
                    if Path(e.name).is_absolute() or '..' in Path(e.name).parts or e.issym() or e.islnk():raise RuntimeError('Unsafe candidate path')
                    if Path(e.name).name.lower() in ['appsettings.production.json','appsettings.development.json']:raise RuntimeError('Private candidate configuration prohibited')
                tar.extractall(release,filter='data')
            marker=json.loads((release/'release.json').read_text(encoding='utf-8-sig'))
            if marker.get('version')!=VERSION or marker.get('schema')!=SCHEMA:raise RuntimeError('Candidate marker differs from approved release')
            for cfg in old.glob('appsettings*.json'):shutil.copy2(cfg,release/cfg.name)
            (release/'release.json').write_text(json.dumps(dict(version=VERSION,schema=SCHEMA,archiveSha256=a.sha256)))
            run(['chown','-R','root:alshifa',str(release)]);run(['chmod','-R','u=rwX,g=rX,o=',str(release)]);(release/'AlShifa.Api').chmod(0o750)
            for entry in [release,*release.rglob('*')]:run(['runuser','-u','alshifa','--','test','-x' if entry.is_dir() or entry.name=='AlShifa.Api' else '-r',str(entry)])
            for port in [33486,33487]:
                with socket.socket() as s:s.bind(('127.0.0.1',port))
            password=secrets.token_hex(32);(base/'mysql-private.env').write_text('MYSQL_ROOT_PASSWORD='+password+'\nMYSQL_ROOT_HOST=%\n')
            image=run(['docker','inspect','-f','{{.Config.Image}}','alshifa-mysql']).decode().strip()
            run(['docker','run','-d','--name',clone,'--label','alshifa.current-update='+a.id,'-p','127.0.0.1:33486:3306','--env-file',str(base/'mysql-private.env'),'-v',str(base/'mysql')+':/var/lib/mysql',image,'--event-scheduler=OFF','--performance-schema=OFF','--innodb-buffer-pool-size=128M']);container_created=True
            info=json.loads(run(['docker','inspect',clone]))[0]
            if info['Config']['Labels'].get('alshifa.current-update')!=a.id or info['HostConfig']['PortBindings']['3306/tcp'][0]['HostIp']!='127.0.0.1':raise RuntimeError('Clone isolation failed')
            for _ in range(90):
                try:sql('SELECT 1',clone);break
                except Exception:time.sleep(1)
            else:raise RuntimeError('Isolated MySQL unavailable')
            sql('CREATE DATABASE `'+database+'` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci',clone)
            with gzip.open(backup,'rb') as src,open(base/'restore-private.log','wb') as log:
                child=subprocess.Popen(['docker','exec','-i',clone,'sh','-c','MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot '+database],stdin=subprocess.PIPE,stdout=log,stderr=log)
                shutil.copyfileobj(src,child.stdin);child.stdin.close()
                if child.wait():raise RuntimeError('Isolated database restore failed')
            if proof(columns,clone)!=before:raise RuntimeError('Restored content differs')
            report['databaseRestoreVerified']=True
            # Actual application accounts are restored only into the dedicated clone.
            statements=[]
            for account in sql("SELECT CONCAT(QUOTE(User),'@',QUOTE(Host)) FROM mysql.user WHERE User LIKE 'alshifa%' ORDER BY User,Host"):
                raw=run(['docker','exec','-i','alshifa-mysql','sh','-c','MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --no-defaults -uroot -N -B --raw --binary-mode'],input=('SHOW CREATE USER '+account).encode()).rstrip(b'\n')
                statements.append((raw if raw.startswith(b'CREATE USER ') else raw.split(b'\t',1)[-1])+b';')
                # The live account also has legacy grants on a retained database.
                # Restore only the active schema's grants into this single-DB lab.
                statements.extend((g+';').encode() for g in sql('SHOW GRANTS FOR '+account)
                                  if 'ON *.*' in g or 'ON `'+database+'`.' in g)
            accounts=b'\n'.join(statements);(base/'accounts-private.sql').write_bytes(accounts)
            sql(accounts.decode(),clone)
            work.mkdir(mode=0o750);run(['chown','alshifa',str(work)])
            if a.performance_index_upgrade:
                migrate(clone,33486,password);verify_index(columns,before,clone)
                restore_backup(clone)
                if proof(columns,clone)!=before:raise RuntimeError('Migration rollback rehearsal lost restored data')
                report['migrationRollbackRestoreVerified']=True
                migrate(clone,33486,password);verify_index(columns,before,clone)
                report['indexMigrationVerified']=True
            candidate_env={k:v for k,v in env.items() if k.startswith(('Mirror__','ASPNETCORE_','DOTNET_')) and not k.startswith('ASPNETCORE_HOSTINGSTARTUP')}
            candidate_env=compatible_env(candidate_env)
            for key in ['ConnectionStrings__Default','Mirror__SyncConnection']:candidate_env[key]=re.sub(r'(?i)(\bPort=)[^;]+',r'\g<1>33486',env[key])
            candidate_env.update(ASPNETCORE_ENVIRONMENT='Production',ASPNETCORE_URLS='http://127.0.0.1:33487',Mirror__Enabled='true',Sync__Url='',Storage__Path=str(work/'storage'),DataProtection__KeysPath=str(work/'keys'))
            envfile=base/'api-private.env';envfile.write_text('\n'.join(k+'='+json.dumps(v) for k,v in candidate_env.items())+'\n')
            run(['systemd-run','--unit='+unit,'--collect','--property=Type=exec','--property=User=alshifa','--property=WorkingDirectory='+str(release),'--property=EnvironmentFile='+str(envfile),'--property=ProtectSystem=strict','--property=ProtectHome=true','--property=PrivateTmp=true','--property=NoNewPrivileges=true','--property=ReadWritePaths='+str(work),str(release/'AlShifa.Api')])
            for _ in range(60):
                try:
                    with urllib.request.urlopen('http://127.0.0.1:33487/health',timeout=2) as r:
                        if r.read()==b'Healthy':break
                except Exception:pass
                time.sleep(1)
            else:raise RuntimeError('Candidate service-user health failed')
            cloned_state=state('http://127.0.0.1:33487',sync_key)
            if not check_sync(cloned_state):raise RuntimeError('Candidate Sync preservation failed')
            with urllib.request.urlopen('http://127.0.0.1:33487/login',timeout=10) as r:
                if r.status!=200 or r.read()!=(release/'wwwroot/index.html').read_bytes():raise RuntimeError('Candidate frontend mismatch')
            if not same_business(before,proof(columns,clone)):raise RuntimeError('Candidate startup changed restored data')
            report.update(passed=True,candidateServiceUserVerified=True,candidateFrontendVerified=True,candidateSyncVerified=True,existingRowsPreserved=True,tablesVerified=len(columns),schema=SCHEMA,previousRelease=str(old),previousApiSha256=digest(old/'AlShifa.Api.dll'),release=str(release),candidateApiSha256=digest(release/'AlShifa.Api.dll'),archiveSha256=a.sha256,databaseBackupSha256=digest(backup),installationBackupSha256=digest(installation),activeDatabase=database)
        else:
            ready=json.loads((base/'preparation.json').read_text())
            for flag in ['passed','installationRestoreVerified','databaseRestoreVerified','candidateServiceUserVerified','candidateFrontendVerified','candidateSyncVerified','existingRowsPreserved']:
                if not ready.get(flag):raise RuntimeError('Preparation proof missing')
            if a.performance_index_upgrade and not all(ready.get(flag) for flag in ['indexMigrationVerified','migrationRollbackRestoreVerified']):raise RuntimeError('Index upgrade/rollback rehearsal missing')
            if time.time()-(base/'preparation.json').stat().st_mtime>3600:raise RuntimeError('Preparation expired')
            if str(old)!=ready['previousRelease'] or digest(old/'AlShifa.Api.dll')!=ready['previousApiSha256'] or digest(release/'AlShifa.Api.dll')!=ready['candidateApiSha256'] or ready['archiveSha256']!=a.sha256:raise RuntimeError('Release changed after rehearsal')
            if digest(base/'database.sql.gz')!=ready['databaseBackupSha256'] or digest(base/'installation.tar.gz')!=ready['installationBackupSha256']:raise RuntimeError('Verified backups changed')
            if stable_env(env)!=json.loads((base/'environment-private.json').read_text()):raise RuntimeError('Production environment changed after rehearsal')
            columns=json.loads((base/'columns-private.json').read_text());expected=json.loads((base/'proof-private.json').read_text())
            if nginx_before.count(b'server {')!=2:raise RuntimeError('Unexpected public vhost topology')
            (base/'nginx-before.conf').write_bytes(nginx_before)
            nginx.write_bytes(nginx_before.replace(b'server {',b'server {\n    if ($remote_addr != 127.0.0.1) { return 503; }'))
            maintenance=True;run(['nginx','-t']);run(['systemctl','reload','nginx'])
            run(['systemctl','stop',SERVICE]);stopped=True
            if proof(columns)!=expected:raise RuntimeError('Source changed after verified backup; code cutover refused')
            if a.performance_index_upgrade:
                migration_started=True
                migrate('alshifa-mysql',3316);verify_index(columns,expected,'alshifa-mysql')
                if compatibility.exists() or compatibility_env.exists():raise RuntimeError('Unexpected existing compatibility override')
                compatibility.parent.mkdir(parents=True,exist_ok=True)
                compatibility_env.write_text(''.join('Mirror__AcceptedSourceSchemas__'+str(i)+'='+s+'\n' for i,s in enumerate(accepted)))
                compatibility.write_text('[Service]\nEnvironmentFile='+str(compatibility_env)+'\n')
                run(['systemctl','daemon-reload']);report['indexMigrationApplied']=True
                report['previousSourceSchemasRetained']=True
            switched=True;switch(release);report['productionChanged']=True
            run(['systemctl','start',SERVICE]);stopped=False;health()
            newpid,newenv=effective()
            if Path('/proc',newpid,'exe').resolve()!=release/'AlShifa.Api' or stable_env(newenv)!=stable_env(compatible_env(env)):raise RuntimeError('Running release/environment mismatch')
            if not same_business(expected,proof(columns)):raise RuntimeError('Production data changed during startup')
            after=state('http://127.0.0.1:5080',sync_key)
            if not check_sync(after):raise RuntimeError('Sync state changed during cutover')
            if tls('/login')!=(200,(release/'wwwroot/index.html').read_bytes()):raise RuntimeError('Public TLS frontend mismatch')
            if tls('/api/auth/me')[0]!=401 or tls('/api/sync/mirror/state')[0] not in [401,403]:raise RuntimeError('Public authorization guard failed')
            if not check_sync(state('',sync_key,public=True)):raise RuntimeError('Public TLS signed Sync mismatch')
            report.update(existingRowsPreserved=True,syncSettingsPreserved=True,syncIdentityAndCheckpointsPreserved=True,healthVerified=True,publicTLSVerified=True,authorizationVerified=True,version=VERSION,schema=SCHEMA,release=str(release),previousRelease=str(old),activeDatabase=database,tablesVerified=len(columns),backupRestoreVerified=True)
            reopen();maintenance=False;report.update(passed=True,publicAccessRestored=True)
    except Exception as error:
        report['failure']=str(error)
        if a.activate and (switched or stopped):
            run(['systemctl','stop',SERVICE])
            stopped=True
            if migration_started:
                restore_backup('alshifa-mysql')
                if proof(columns)!=expected:raise RuntimeError('Rollback content proof failed; maintenance retained')
                report['databaseRollbackVerified']=True
            if compatibility.exists():compatibility.unlink();run(['systemctl','daemon-reload'])
            if compatibility_env.exists():compatibility_env.unlink()
            if switched:switch(old)
            run(['systemctl','start',SERVICE]);health();report['rollbackSucceeded']=True
        if maintenance:reopen();maintenance=False
        raise
    finally:
        if not a.activate:
            subprocess.run(['systemctl','stop',unit],capture_output=True)
            if container_created:
                info=json.loads(run(['docker','inspect',clone]))[0]
                if info['Config']['Labels'].get('alshifa.current-update')==a.id:run(['docker','stop',clone])
        report['completedAt']=datetime.datetime.now(datetime.timezone.utc).isoformat();report['maintenanceRetained']=maintenance
        status.write_text(json.dumps(report,indent=2))
        print(json.dumps(report))
if __name__=='__main__':main()
