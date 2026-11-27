import { readFile } from 'node:fs/promises'
import assert from 'node:assert/strict'
import test from 'node:test'
import ts from 'typescript'

const source=await readFile(new URL('../src/api/http.ts',import.meta.url),'utf8')
const compiled=ts.transpileModule(source,{compilerOptions:{target:ts.ScriptTarget.ES2022,module:ts.ModuleKind.ESNext}}).outputText
const {api,ApiError,uploadFile,downloadFile}=await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`)
const untilAbort=signal=>new Promise((_resolve,reject)=>{
  const abort=()=>reject(new DOMException('Synthetic cancellation','AbortError'))
  if(signal.aborted)abort();else signal.addEventListener('abort',abort,{once:true})
})
async function flush(){for(let i=0;i<20;i++)await Promise.resolve()}

for(const operation of ['write-body','read-body','upload-body','download-body','csrf']){
  test(`timeout covers stalled ${operation} and preserves an unknown write outcome`,async t=>{
    const originals={fetch:globalThis.fetch,document:globalThis.document}
    t.mock.timers.enable({apis:['setTimeout']})
    globalThis.document={cookie:operation==='csrf'?'':'XSRF-TOKEN=synthetic'}
    globalThis.fetch=async(_url,{signal})=>{
      if(operation==='csrf')return untilAbort(signal)
      return {status:operation==='write-body'||operation==='upload-body'?201:200,ok:true,headers:new Headers(),json:()=>untilAbort(signal),blob:()=>untilAbort(signal)}
    }
    try{
      const pending=operation==='upload-body'?uploadFile('/attachments',new FormData()):operation==='download-body'?downloadFile('/report','report.pdf'):operation==='read-body'?api('/patients'):api('/patients',{method:'POST',body:{synthetic:true},idempotencyKey:'stable-key'})
      const assertion=assert.rejects(pending,error=>error instanceof ApiError&&((operation==='read-body'||operation==='download-body')?error.code==='network':error.outcomeUnknown))
      await flush();t.mock.timers.tick(120001);await assertion
    }finally{Object.assign(globalThis,originals)}
  })
}

test('caller cancellation is still distinguishable from a timed out write',async()=>{
  const originals={fetch:globalThis.fetch,document:globalThis.document}
  globalThis.document={cookie:'XSRF-TOKEN=synthetic'}
  globalThis.fetch=async(_url,{signal})=>({status:201,ok:true,headers:new Headers(),json:()=>untilAbort(signal)})
  try{
    const controller=new AbortController()
    const pending=api('/patients',{method:'POST',body:{synthetic:true},idempotencyKey:'stable-key',signal:controller.signal})
    const assertion=assert.rejects(pending,error=>error instanceof DOMException&&error.name==='AbortError')
    await flush();controller.abort();await assertion
  }finally{Object.assign(globalThis,originals)}
})
