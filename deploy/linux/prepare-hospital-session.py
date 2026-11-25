"""Install a temporary, unprivileged, loopback-only encrypted mailbox under one HTTPS path."""
import datetime, fcntl, grp, hashlib, json, os, pathlib, pwd, secrets, socket, subprocess, time, urllib.request, urllib.error
import traceback
os.umask(0o077)
lock=open('/run/lock/alshifa-deploy.lock','a');fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
def run(args):
    p=subprocess.run(args,capture_output=True)
    if p.returncode:
        if 'directory' in globals():(directory/'native-failure-private.log').write_bytes(p.stderr)
        raise RuntimeError('Session preparation failed; protected logs retained')
    return p.stdout
stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
sid='connect-'+stamp+'-'+secrets.token_hex(4)
account=pwd.getpwnam('alshifa')
parent=pathlib.Path('/var/lib/alshifa-operator-sessions')
parent.mkdir(exist_ok=True,mode=0o750);os.chown(parent,0,account.pw_gid);parent.chmod(0o750)
directory=parent/sid
directory.mkdir(mode=0o700)
os.chown(directory,account.pw_uid,account.pw_gid)
script=directory/'relay.py';script.write_bytes(pathlib.Path('/var/tmp/alshifa-hospital-session-relay.py').read_bytes());script.chmod(0o500);os.chown(script,account.pw_uid,account.pw_gid)
with socket.socket() as probe:
    probe.bind(('127.0.0.1',0));port=probe.getsockname()[1]
expires=time.time()+48*3600
prefix='/__alshifa_session/'+sid+'/'
config={'directory':str(directory),'port':port,'token':secrets.token_hex(32),'sessionId':sid,'installationId':'0ab50744-3dc0-40d4-9662-50c1332ca4c2','expiresEpoch':expires,
        'expiresAt':datetime.datetime.fromtimestamp(expires,datetime.timezone.utc).isoformat(),'url':'https://shafa.novatechsoft.com'+prefix}
configfile=directory/'config-private.json';configfile.write_text(json.dumps(config));configfile.chmod(0o400);os.chown(configfile,account.pw_uid,account.pw_gid)
nginx=pathlib.Path('/etc/nginx/sites-available/shafa.novatechsoft.com')
original=nginx.read_bytes();(directory/'nginx-before-private.conf').write_bytes(original)
assert original.count(b'    # Signed batches from the hospital server.')==1
marker='# AlShifa temporary operator session '+sid
block=('    '+marker+' BEGIN\n    location ^~ '+prefix+' {\n        client_max_body_size 128m;\n        proxy_pass http://127.0.0.1:'+str(port)+'/;\n        proxy_set_header Authorization $http_authorization;\n        proxy_set_header X-AlShifa-Agent $http_x_alshifa_agent;\n        proxy_read_timeout 60s;\n        proxy_send_timeout 60s;\n        access_log off;\n    }\n    '+marker+' END\n\n').encode()
unit='alshifa-operator-'+sid
started=False;changed=False;stage='start_unprivileged_service'
try:
    run(['systemd-run','--unit='+unit,'--property=User=alshifa','--property=Group='+grp.getgrgid(account.pw_gid).gr_name,'--property=NoNewPrivileges=yes','--property=PrivateTmp=yes',
         '--property=ProtectSystem=strict','--property=ProtectHome=yes','--property=ReadWritePaths='+str(directory),'--property=MemoryMax=512M','--property=RuntimeMaxSec=48h',
         '/usr/bin/python3',str(script),'--config',str(configfile)])
    started=True
    stage='private_health'
    for _ in range(30):
        try:
            req=urllib.request.Request('http://127.0.0.1:'+str(port)+'/health',headers={'Authorization':'Bearer '+config['token']})
            with urllib.request.urlopen(req,timeout=2) as response:assert json.load(response)['sessionId']==sid
            break
        except OSError:time.sleep(.2)
    else:raise RuntimeError('Private mailbox health failed')
    assert nginx.read_bytes()==original
    stage='nginx_additive_route'
    nginx.write_bytes(original.replace(b'    # Signed batches from the hospital server.',block+b'    # Signed batches from the hospital server.',1));changed=True
    run(['nginx','-t']);run(['systemctl','reload','nginx'])
    stage='public_tls_health'
    request=urllib.request.Request(config['url']+'health',headers={'Authorization':'Bearer '+config['token']})
    for _ in range(30):
        try:
            with urllib.request.urlopen(request,timeout=15) as response:assert json.load(response)['sessionId']==sid
            break
        except (OSError,ValueError,AssertionError):time.sleep(.3)
    else:raise RuntimeError('New HTTPS route did not become ready')
    stage='public_unauthorized_refusal'
    try:urllib.request.urlopen(config['url']+'health',timeout=15)
    except urllib.error.HTTPError as response:assert response.code==401
    else:raise RuntimeError('Unauthenticated mailbox was not refused')
    stage='application_health'
    with urllib.request.urlopen('https://shafa.novatechsoft.com/login',timeout=15) as response:assert response.status==200
    assert run(['systemctl','is-active','alshifa-api.service']).decode().strip()=='active'
    cleanup_code='import pathlib,subprocess\np=pathlib.Path('+repr(str(nginx))+')\nblock='+repr(block)+'\ncurrent=p.read_bytes()\nif current.count(block)==1:\n p.write_bytes(current.replace(block,b"",1))\n r=subprocess.run(["nginx","-t"],capture_output=True)\n if r.returncode: p.write_bytes(current)\n else: subprocess.run(["systemctl","reload","nginx"],check=True)\nsubprocess.run(["systemctl","stop",'+repr(unit)+'])\n'
    rootcleanup=pathlib.Path('/var/backups/alshifa/operator-session-cleanup')
    rootcleanup.mkdir(parents=True,exist_ok=True,mode=0o700)
    rootfile=rootcleanup/(sid+'.py');rootfile.write_text(cleanup_code);rootfile.chmod(0o700)
    run(['systemd-run','--unit='+unit+'-expiry','--on-active=48h','/usr/bin/python3',str(rootfile)])
    result={'sessionId':sid,'directory':str(directory),'unit':unit,'expiresAt':config['expiresAt'],'prefix':prefix,'publicTlsVerified':True,'unauthorizedRefused':True,'applicationUnchanged':True}
    (directory/'prepared.json').write_text(json.dumps(result,indent=2))
    print(json.dumps(result))
except Exception as error:
    (directory/'failure-private.txt').write_text(traceback.format_exc())
    if changed:
        nginx.write_bytes(original);subprocess.run(['nginx','-t'],capture_output=True);subprocess.run(['systemctl','reload','nginx'],capture_output=True)
    if started:subprocess.run(['systemctl','stop',unit],capture_output=True)
    raise RuntimeError('Temporary mailbox preparation failed at '+stage+' ('+type(error).__name__+'); previous application configuration restored') from None
