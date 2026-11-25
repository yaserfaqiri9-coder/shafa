"""Expiring HTTPS-proxied mailbox. Never executes uploaded code or reads application data."""
import argparse, datetime, hashlib, hmac, json, os, re, secrets, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

MAX_BODY=128*1024*1024

def serve(config_path):
    os.umask(0o077)
    config=json.loads(Path(config_path).read_text())
    base=Path(config['directory'])
    lock=threading.Lock()
    expected=config['token'].encode()
    expires=float(config['expiresEpoch'])
    def atomic(path,data):
        temporary=path.with_name(path.name+'.'+secrets.token_hex(8)+'.partial')
        with open(temporary,'xb') as out:
            out.write(data);out.flush();os.fsync(out.fileno())
        os.replace(temporary,path)
    class Handler(BaseHTTPRequestHandler):
        server_version='AlShifaSession/1'
        def log_message(self,*args): pass
        def reply(self,code,obj):
            data=json.dumps(obj,separators=(',',':')).encode()
            self.send_response(code);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(data)));self.send_header('Cache-Control','no-store');self.end_headers();self.wfile.write(data)
        def authorized(self):
            if time.time()>expires:
                self.reply(410,{'code':'session_expired'});return False
            actual=self.headers.get('Authorization','').encode()
            if not hmac.compare_digest(actual,b'Bearer '+expected):
                self.reply(401,{'code':'unauthorized'});return False
            return True
        def bound(self):
            state=base/'claim.json'
            if not state.exists():return False
            claim=json.loads(state.read_text())
            return hmac.compare_digest(self.headers.get('X-AlShifa-Agent',''),claim['agentId'])
        def read_body(self):
            length=int(self.headers.get('Content-Length','-1'))
            if length<0 or length>MAX_BODY:raise ValueError('body_size')
            return self.rfile.read(length)
        def do_GET(self):
            if not self.authorized():return
            if self.path=='/health':return self.reply(200,{'sessionId':config['sessionId'],'expiresEpoch':expires})
            if self.path!='/job':return self.reply(404,{'code':'not_found'})
            with lock:
                if not self.bound():return self.reply(403,{'code':'agent_not_bound'})
                job=base/'pending.job'
                return self.reply(200,{'job':json.loads(job.read_text()) if job.exists() else None})
        def do_POST(self):
            if not self.authorized():return
            try:
                data=self.read_body()
                with lock:
                    if self.path=='/claim':
                        value=json.loads(data)
                        if value.get('sessionId')!=config['sessionId'] or value.get('installationId')!=config['installationId'] or not re.fullmatch('[a-f0-9]{64}',value.get('agentId','')):
                            return self.reply(409,{'code':'identity_mismatch'})
                        claim=base/'claim.json'
                        if claim.exists() and json.loads(claim.read_text())!=value:return self.reply(409,{'code':'session_already_claimed'})
                        if not claim.exists():atomic(claim,json.dumps(value).encode())
                        return self.reply(200,{'claimed':True})
                    if not self.bound():return self.reply(403,{'code':'agent_not_bound'})
                    if self.path not in ['/evidence','/result','/heartbeat']:return self.reply(404,{'code':'not_found'})
                    if self.path=='/heartbeat':
                        atomic(base/'heartbeat.json',json.dumps({'seenAt':datetime.datetime.now(datetime.timezone.utc).isoformat()}).encode())
                        return self.reply(200,{'received':True})
                    value=json.loads(data)
                    # Payloads remain encrypted to the office. This service is only a mailbox.
                    envelope=value.get('envelope')
                    if not isinstance(envelope,str) or len(envelope)>MAX_BODY:return self.reply(400,{'code':'envelope_required'})
                    header=json.loads(envelope)
                    if header.get('format')!='AlShifa.SyncReview.1' or header.get('packageId')!=config['sessionId']:return self.reply(400,{'code':'wrong_envelope'})
                    digest=hashlib.sha256(data).hexdigest()
                    bucket=base/('evidence' if self.path=='/evidence' else 'results');bucket.mkdir(exist_ok=True,mode=0o700)
                    target=bucket/(digest+'.json')
                    if not target.exists():atomic(target,data)
                    atomic(base/'heartbeat.json',json.dumps({'seenAt':datetime.datetime.now(datetime.timezone.utc).isoformat()}).encode())
                    return self.reply(200,{'received':True,'sha256':digest})
            except (ValueError,KeyError,TypeError,json.JSONDecodeError):self.reply(400,{'code':'invalid_request'})
    server=ThreadingHTTPServer(('127.0.0.1',int(config['port'])),Handler)
    server.daemon_threads=True
    server.serve_forever()

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--config',required=True)
    serve(parser.parse_args().config)
