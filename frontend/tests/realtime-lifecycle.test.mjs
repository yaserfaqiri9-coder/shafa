import { readFile } from 'node:fs/promises'
import assert from 'node:assert/strict'
import test from 'node:test'
import ts from 'typescript'

async function lab(start = async () => {}) {
  const mounted=[],unmounted=[],connections=[],timers=new Map(),visibility=new Set()
  let sequence=0
  const originals={document:globalThis.document,setTimeout:globalThis.setTimeout,clearTimeout:globalThis.clearTimeout,setInterval:globalThis.setInterval,clearInterval:globalThis.clearInterval}
  globalThis.document={hidden:false,addEventListener:(_n,fn)=>visibility.add(fn),removeEventListener:(_n,fn)=>visibility.delete(fn)}
  globalThis.setTimeout=fn=>{timers.set(++sequence,fn);return sequence}
  globalThis.clearTimeout=id=>timers.delete(id)
  globalThis.setInterval=()=>++sequence
  globalThis.clearInterval=()=>{}
  class HubConnectionBuilder {
    withUrl(){return this} withAutomaticReconnect(){return this} configureLogging(){return this}
    build(){const c={state:'Disconnected',handlers:{},start,stop:async()=>{},on:(name,fn)=>c.handlers[name]=fn,onreconnecting:fn=>c.reconnecting=fn,onreconnected:fn=>c.reconnected=fn,onclose:fn=>c.close=fn};connections.push(c);return c}
  }
  const key=`__realtimeTest${Math.random().toString(36).slice(2)}`
  globalThis[key]={onMounted:fn=>mounted.push(fn),onBeforeUnmount:fn=>unmounted.push(fn),ref:value=>({value}),HubConnectionBuilder,HubConnectionState:{Disconnected:'Disconnected'},LogLevel:{None:0}}
  let source=await readFile(new URL('../src/api/realtime.ts',import.meta.url),'utf8')
  source=source.replace(/^import .* from 'vue'\r?\n/m,'').replace(/^import .* from '@microsoft\/signalr'\r?\n/m,'')
  const compiled=ts.transpileModule(source,{compilerOptions:{target:ts.ScriptTarget.ES2022,module:ts.ModuleKind.ESNext}}).outputText
  const prelude=`const {onMounted,onBeforeUnmount,ref,HubConnectionBuilder,HubConnectionState,LogLevel}=globalThis.${key};\n`
  const api=await import(`data:text/javascript;base64,${Buffer.from(prelude+compiled).toString('base64')}`)
  const flush=async()=>{const pending=[...timers.values()];timers.clear();for(const fn of pending)void fn();await Promise.resolve()}
  return {api,connections,timers,mount:()=>mounted.forEach(fn=>fn()),unmount:()=>unmounted.forEach(fn=>fn()),visible:()=>visibility.forEach(fn=>fn()),flush,restore:()=>{Object.assign(globalThis,originals);delete globalThis[key]}}
}

test('logout during a failed start cannot resurrect a connection or reconnect timer',async()=>{
  let rejectStart;let attempts=0
  const l=await lab(()=>++attempts===1?new Promise((_resolve,reject)=>rejectStart=reject):Promise.resolve())
  try{
    l.api.connectRealtime();await Promise.resolve()
    l.api.disconnectRealtime()
    rejectStart(new Error('Synthetic disconnected start'));await Promise.resolve()
    assert.equal(l.timers.size,0)
    l.api.connectRealtime();const current=l.connections[1]
    await Promise.resolve()
    l.connections[0].close();l.connections[0].reconnected()
    assert.equal(l.timers.size,0)
    current.close();assert.equal(l.timers.size,1)
    l.api.disconnectRealtime();assert.equal(l.timers.size,0)
  }finally{l.restore()}
})

test('pending live refresh and coalesced work stop when the page unmounts',async()=>{
  const l=await lab();let release;let loads=0
  try{
    l.api.useLiveRefresh(['changed'],()=>{loads++;return new Promise(resolve=>release=resolve)})
    l.mount();l.api.connectRealtime();await Promise.resolve()
    await l.flush()
    l.connections[0].handlers.event({type:'changed'});await l.flush()
  }finally{
    l.unmount();release?.();await Promise.resolve();assert.equal(loads,1);l.api.disconnectRealtime();l.restore()
  }
})

test('hidden tabs coalesce accepted events and refresh once on visibility even without a timer',async()=>{
  const l=await lab();let loads=0
  try{
    l.api.useLiveRefresh(['changed'],()=>loads++,0);l.mount()
    l.api.connectRealtime();await Promise.resolve();await l.flush();loads=0
    document.hidden=true
    for(let i=0;i<100;i++)l.connections[0].handlers.event({type:'changed'})
    assert.equal(l.timers.size,0);assert.equal(loads,0)
    document.hidden=false;l.visible();await Promise.resolve();assert.equal(loads,1)
    l.unmount();l.visible();assert.equal(loads,1)
  }finally{l.api.disconnectRealtime();l.restore()}
})
