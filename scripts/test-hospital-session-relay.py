import json, pathlib, secrets, socket, subprocess, sys, time, urllib.request, urllib.error, uuid
repo=pathlib.Path(__file__).resolve().parents[1]
work=repo/'.tools'/('session-relay-lab-'+uuid.uuid4().hex);work.mkdir()
with socket.socket() as probe:
    probe.bind(('127.0.0.1',0));port=probe.getsockname()[1]
token=secrets.token_hex(32)
config={'directory':str(work),'port':port,'token':token,'sessionId':'synthetic-session','installationId':str(uuid.uuid4()),'expiresEpoch':time.time()+120}
(work/'config-private.json').write_text(json.dumps(config))
error=open(work/'relay-private.log','wb')
child=subprocess.Popen([sys.executable,str(repo/'deploy/linux/hospital-session-relay.py'),'--config',str(work/'config-private.json')],stdout=subprocess.DEVNULL,stderr=error)
checks=0
def call(path,method='GET',data=None,auth=True,agent=None):
    global checks
    headers={}
    if auth:headers['Authorization']='Bearer '+token
    if agent:headers['X-AlShifa-Agent']=agent
    if data is not None:headers['Content-Type']='application/json'
    request=urllib.request.Request('http://127.0.0.1:'+str(port)+path,method=method,headers=headers,data=None if data is None else json.dumps(data).encode())
    try:
        with urllib.request.urlopen(request,timeout=2) as response:return response.status,json.load(response)
    except urllib.error.HTTPError as response:return response.code,json.load(response)
def expect(actual,expected):
    global checks
    assert actual==expected,(actual,expected)
    checks+=1
try:
    for _ in range(50):
        try:
            if call('/health')[0]==200:break
        except OSError:time.sleep(.1)
    expect(call('/health',auth=False)[0],401)
    expect(call('/job')[0],403)
    expect(call('/claim','POST',{'sessionId':'wrong','installationId':config['installationId'],'agentId':'a'*64})[0],409)
    claim={'sessionId':config['sessionId'],'installationId':config['installationId'],'agentId':'a'*64}
    expect(call('/claim','POST',claim)[0],200)
    expect(call('/claim','POST',claim)[0],200)
    expect(call('/claim','POST',dict(claim,agentId='b'*64))[0],409)
    expect(call('/job',agent='b'*64)[0],403)
    expect(call('/job',agent='a'*64)[1]['job'],None)
    (work/'pending.job').write_text(json.dumps({'body':'synthetic','signature':'synthetic'}))
    expect(call('/job',agent='a'*64)[1]['job']['signature'],'synthetic')
    expect(call('/evidence','POST',{'envelope':'{}'},agent='a'*64)[0],400)
    envelope=json.dumps({'format':'AlShifa.SyncReview.1','packageId':config['sessionId'],'ciphertext':'synthetic-ciphertext'})
    expect(call('/evidence','POST',{'envelope':envelope},agent='a'*64)[0],200)
    expect(call('/evidence','POST',{'envelope':envelope},agent='a'*64)[0],200)
    expect(len(list((work/'evidence').glob('*.json'))),1)
    expect(call('/result','POST',{'envelope':envelope},agent='a'*64)[0],200)
    expect(call('/heartbeat','POST',{'alive':True},agent='a'*64)[0],200)
    expect(call('/unrelated',agent='a'*64)[0],404)
    result={'passed':True,'checks':checks,'synthetic':True,'productionChanged':False}
finally:
    child.terminate();child.wait(timeout=10);error.close()
(work/'result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result))
