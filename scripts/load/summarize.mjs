// Reads only redacted measurements, never credentials, SQL, logs or business payloads.
import { readFileSync, writeFileSync } from 'node:fs'
import { resolve, basename } from 'node:path'
const labs=process.argv.slice(2)
if(!labs.length)throw Error('Pass one or more completed lab directories')
const median=list=>{const s=[...list].sort((a,b)=>a-b);return s[Math.floor(s.length/2)]??0}
const reports=labs.map(arg=>{
  const dir=resolve(arg),result=JSON.parse(readFileSync(`${dir}/result.json`,'utf8'))
  const csv=readFileSync(`${dir}/resources.csv`,'utf8').replace(/^\uFEFF/,'').trim().split(/\r?\n/)
  const cells=line=>[...line.matchAll(/"((?:[^"]|"")*)"/g)].map(m=>m[1].replaceAll('""','"'))
  const fields=cells(csv.shift())
  const samples=csv.map(line=>Object.fromEntries(cells(line).map((s,i)=>[fields[i],fields[i]==='at'?Date.parse(s):Number(s.replace(',','.'))])))
  const start=Date.parse(result.startedUtc),end=Date.parse(result.finishedUtc)
  const active=samples.filter(s=>s.at>=start&&s.at<=end)
  const peak=key=>Math.max(...samples.map(s=>s[key]))
  const duration=active.length>1?(active.at(-1).at-active[0].at)/1000:0
  const cpu=key=>duration?(active.at(-1)[key]-active[0][key])/duration*100:0
  const reference=active.filter(s=>s.at>=start+5*60000&&s.at<start+10*60000)
  const tail=active.filter(s=>s.at>=end-5*60000)
  const growth=key=>reference.length&&tail.length?median(tail.map(s=>s[key]))-median(reference.map(s=>s[key])):null
  const resources={samples:samples.length,apiPeakPrivateMiB:peak('apiPrivateMiB'),apiPeakWorkingMiB:peak('apiWorkingMiB'),mysqlPeakPrivateMiB:peak('mysqlPrivateMiB'),mysqlPeakWorkingMiB:peak('mysqlWorkingMiB'),combinedPeakPrivateMiB:Math.max(...samples.map(s=>s.apiPrivateMiB+s.mysqlPrivateMiB)),apiPeakThreads:peak('apiThreads'),apiPeakHandles:peak('apiHandles'),apiMeanCpuPercentOfOneCore:cpu('apiCpuSeconds'),mysqlMeanCpuPercentOfOneCore:cpu('mysqlCpuSeconds'),apiLatePrivateGrowthMiB:growth('apiPrivateMiB'),mysqlLatePrivateGrowthMiB:growth('mysqlPrivateMiB'),apiLateHandleGrowth:growth('apiHandles'),apiLateThreadGrowth:growth('apiThreads')}
  const memoryPassed=resources.apiPeakPrivateMiB<768&&resources.mysqlPeakPrivateMiB<768
  const soak=result.seconds>=1800?{passed:memoryPassed&&growth('apiPrivateMiB')<=64&&growth('mysqlPrivateMiB')<=64&&growth('apiHandles')<=64&&growth('apiThreads')<=16,referenceMinutes:[5,10],tailMinutes:5}:null
  const report={lab:basename(dir),passed:result.passed&&memoryPassed&&(soak?.passed??true),...result,resources,memoryPassed,soak}
  // Place final passed after source fields so a resource failure cannot be hidden by the workload flag.
  report.passed=result.passed&&memoryPassed&&(soak?.passed??true)
  writeFileSync(`${dir}/summary-redacted.json`,JSON.stringify(report,null,2))
  return {lab:report.lab,passed:report.passed,seconds:report.seconds,clients:report.concurrentClients,requests:report.requests,requestsPerSecond:report.requestsPerSecond,writes:report.writes,unexpected:report.unexpected,integrity:report.integrity,latencyFailures:report.latencyFailures,resources,soak}
})
console.log(JSON.stringify(reports,null,2))
process.exitCode=reports.every(r=>r.passed)?0:1
