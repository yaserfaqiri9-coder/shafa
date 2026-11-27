// Only invoked by run-isolated.ps1 after positive server/PID/datadir/grant verification.
import { readFileSync, writeFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { randomUUID } from 'node:crypto'
const [file, mode] = process.argv.slice(2)
const cfg = JSON.parse(readFileSync(file, 'utf8').replace(/^\uFEFF/, ''))
if (cfg.database !== 'alshifa_perf_load' || !/^http:\/\/127\.0\.0\.1:334\d\d$/.test(cfg.base)) throw Error('Unsafe lab target')
function sql(text) {
  return execFileSync(cfg.mysql, [`--defaults-extra-file=${cfg.cnf}`, '-N', '-B', '--raw', cfg.database], { input: text, encoding: 'utf8', maxBuffer: 20 * 1024 * 1024 })
}
const save = (name, value) => writeFileSync(`${cfg.output}/${name}.json`, JSON.stringify(value, null, 2))
if (mode === 'seed') {
  const n = cfg.patients
  if (Number(sql('SELECT COUNT(*) FROM Patients')) !== 0) throw Error('Fixture database must be empty')
  sql(`
CREATE TABLE PerfDigits(n INT PRIMARY KEY);
INSERT INTO PerfDigits VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9);
CREATE TEMPORARY TABLE PerfNumbers(n INT PRIMARY KEY);
INSERT INTO PerfNumbers SELECT 1+a.n+10*b.n+100*c.n+1000*d.n+10000*e.n+100000*f.n FROM PerfDigits a CROSS JOIN PerfDigits b CROSS JOIN PerfDigits c CROSS JOIN PerfDigits d CROSS JOIN PerfDigits e CROSS JOIN PerfDigits f WHERE 1+a.n+10*b.n+100*c.n+1000*d.n+10000*e.n+100000*f.n <= ${n*3};
DROP TABLE PerfDigits;
SET @u=(SELECT Id FROM AspNetUsers WHERE UserName='perf_operator');
SET @dep=(SELECT MIN(Id) FROM Departments);
SET @loc=(SELECT Id FROM StockLocations WHERE Code='pharmacy');
INSERT INTO Patients(Id,FileNumber,FirstName,LastName,Gender,EstimatedBirthYear,CreatedByUserId,CreatedAt,Version)
SELECT n,CONCAT('PF-',LPAD(n,8,'0')),CONCAT('Synthetic',LPAD(n,8,'0')),'Fixture',IF(n%2=0,'Male','Female'),1985,@u,DATE_SUB(UTC_TIMESTAMP(),INTERVAL (n%365) DAY),1 FROM PerfNumbers WHERE n<=${n};
INSERT INTO Visits(Id,VisitNumber,PatientId,DepartmentId,Status,VisitDate,QueueNumber,QueueShift,QueueRound,CreatedAt,CreatedByUserId,Version)
SELECT n,CONCAT('VF-',LPAD(n,8,'0')),1+(n-1)%${n},@dep,'Completed',DATE_SUB(UTC_DATE(),INTERVAL (n%365) DAY),NULL,'Morning',1,DATE_SUB(UTC_TIMESTAMP(),INTERVAL (n%365) DAY),@u,1 FROM PerfNumbers;
INSERT INTO CashRegisters(Name,IsActive) VALUES('Synthetic historical register',1);
SET @reg=LAST_INSERT_ID();
INSERT INTO CashSessions(RegisterId,OpenedByUserId,OpeningAmount,OpenedAt,ClosedByUserId,ClosedAt,ExpectedAmount,CountedAmount,Version) VALUES(@reg,@u,0,DATE_SUB(UTC_TIMESTAMP(),INTERVAL 366 DAY),@u,UTC_TIMESTAMP(),${n*100},${n*100},1);
SET @session=LAST_INSERT_ID();
INSERT INTO Invoices(Id,InvoiceNumber,PatientId,VisitId,Status,Total,Discount,Paid,Refunded,IssuedByUserId,CreatedAt,Version)
SELECT n,CONCAT('IF-',LPAD(n,8,'0')),n,n,'Paid',100,0,100,0,@u,DATE_SUB(UTC_TIMESTAMP(),INTERVAL (n%365) DAY),1 FROM PerfNumbers WHERE n<=${n};
INSERT INTO InvoiceItems(InvoiceId,Description,Quantity,UnitPrice,LineTotal,RevenueAccount,SourceType,SourceId,IsVoid)
SELECT n,'Synthetic historical service',1,100,100,'4000','Fixture',n,0 FROM PerfNumbers WHERE n<=${n};
INSERT INTO Payments(Id,ReceiptNumber,InvoiceId,Amount,Method,CashSessionId,ReceivedByUserId,CreatedAt,Refunded)
SELECT n,CONCAT('RF-',LPAD(n,8,'0')),n,100,'Cash',@session,@u,DATE_SUB(UTC_TIMESTAMP(),INTERVAL (n%365) DAY),0 FROM PerfNumbers WHERE n<=${n};
INSERT INTO LedgerTransactions(Id,Kind,SourceType,SourceId,Description,CreatedByUserId,CreatedAt)
SELECT n,'Invoice','Invoice',n,'Synthetic history',@u,DATE_SUB(UTC_TIMESTAMP(),INTERVAL (n%365) DAY) FROM PerfNumbers WHERE n<=${n};
INSERT INTO LedgerTransactions(Id,Kind,SourceType,SourceId,Description,CreatedByUserId,CreatedAt)
SELECT ${n}+n,'Payment','Payment',n,'Synthetic history',@u,DATE_SUB(UTC_TIMESTAMP(),INTERVAL (n%365) DAY) FROM PerfNumbers WHERE n<=${n};
INSERT INTO LedgerEntries(TransactionId,AccountCode,Debit,Credit) SELECT n,'1100',100,0 FROM PerfNumbers WHERE n<=${n};
INSERT INTO LedgerEntries(TransactionId,AccountCode,Debit,Credit) SELECT n,'4000',0,100 FROM PerfNumbers WHERE n<=${n};
INSERT INTO LedgerEntries(TransactionId,AccountCode,Debit,Credit) SELECT ${n}+n,'1000',100,0 FROM PerfNumbers WHERE n<=${n};
INSERT INTO LedgerEntries(TransactionId,AccountCode,Debit,Credit) SELECT ${n}+n,'1100',0,100 FROM PerfNumbers WHERE n<=${n};
INSERT INTO Products(Id,Code,Name,Kind,Unit,ReorderLevel,SalePrice,IsActive,Version) SELECT n,CONCAT('DF-',LPAD(n,6,'0')),CONCAT('Synthetic drug ',n),'Drug','unit',10,100,1,1 FROM PerfNumbers WHERE n<=5000;
INSERT INTO StockBatches(Id,ProductId,LocationId,LotNumber,ExpiryDate,Quantity,UnitCost) SELECT n,1+(n-1)%5000,@loc,CONCAT('LF-',n),DATE_ADD(UTC_DATE(),INTERVAL 730 DAY),10000,40 FROM PerfNumbers WHERE n<=15000;
INSERT INTO StockMovements(BatchId,Kind,Quantity,SourceType,SourceId,UserId,CreatedAt) SELECT n,'Purchase',10000,'Fixture',n,@u,DATE_SUB(UTC_TIMESTAMP(),INTERVAL 366 DAY) FROM PerfNumbers WHERE n<=15000;
INSERT INTO LedgerTransactions(Id,Kind,SourceType,SourceId,Description,CreatedByUserId,CreatedAt) VALUES(${n*2+1},'OpeningStock','Fixture',1,'Synthetic counted stock',@u,DATE_SUB(UTC_TIMESTAMP(),INTERVAL 366 DAY));
INSERT INTO LedgerEntries(TransactionId,AccountCode,Debit,Credit) VALUES(${n*2+1},'1200',6000000000,0),(${n*2+1},'3000',0,6000000000);
ANALYZE TABLE Patients,Visits,Invoices,InvoiceItems,Payments,LedgerTransactions,LedgerEntries,Products,StockBatches,StockMovements;
`)
  save('fixture', { patients:n,visits:n*3,invoices:n,payments:n,ledgerTransactions:n*2+1,ledgerEntries:n*4+2,products:5000,stockBatches:15000,synthetic:true,foreignKeysEnabled:true })
  console.log('Synthetic fixture created with foreign keys and uniqueness checks enabled.')
  process.exit(0)
}
const stats = new Map()
let phase = 'setup'
function record(name, ms, status, expected, code) {
  const key = `${phase}:${name}`
  if (!stats.has(key)) stats.set(key,{ count:0,sum:0,max:0,buckets:new Uint32Array(30001),statuses:{},codes:{},unexpected:0 })
  const s=stats.get(key);s.count++;s.sum+=ms;s.max=Math.max(s.max,ms);s.buckets[Math.min(30000,Math.ceil(ms))]++
  s.statuses[status]=(s.statuses[status]??0)+1
  if (!expected.includes(status)) s.unexpected++
  if(code && /^[a-zA-Z0-9_]+$/.test(code))s.codes[code]=(s.codes[code]??0)+1
}
class Client {
  cookies=new Map()
  async request(name,method,path,body,{key,expected=[200]}={}) {
    const headers={Cookie:[...this.cookies].map(([k,v])=>`${k}=${v}`).join('; ')}
    if(body!==undefined)headers['Content-Type']='application/json'
    if(method!=='GET')headers['X-XSRF-TOKEN']=decodeURIComponent(this.cookies.get('XSRF-TOKEN')??'')
    if(key)headers['Idempotency-Key']=key
    const started=performance.now()
    try{
      const response=await fetch(cfg.base+path,{method,headers,body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(30000),redirect:'manual'})
      for(const line of response.headers.getSetCookie()){const pair=line.split(';')[0];const i=pair.indexOf('=');this.cookies.set(pair.slice(0,i),pair.slice(i+1))}
      const text=await response.text();let data=null;try{data=JSON.parse(text)}catch{}
      record(name,performance.now()-started,response.status,expected,data?.code)
      return {status:response.status,data}
    }catch{record(name,performance.now()-started,0,expected);return {status:0,data:null}}
  }
  async login(){await this.request('csrf','GET','/api/auth/csrf',undefined,{expected:[204]});const r=await this.request('login','POST','/api/auth/login',{userName:cfg.user,password:cfg.password});if(r.status!==200)throw Error(`Synthetic login failed ${r.status}`)}
}
const ensure=(r,status=200)=>{if(r.status!==status)throw Error(`Fixture setup HTTP ${r.status}, expected ${status}; code ${r.data?.code??'unknown'}`);return r.data}
const admin=new Client();await admin.login()
const service=ensure(await admin.request('service','POST','/api/services',{name:'Synthetic load service',isActive:true,price:100},{expected:[201]}),201)
const registers=ensure(await admin.request('registers','GET','/api/cash/registers'))
ensure(await admin.request('cash_open','POST','/api/cash/sessions',{registerId:registers[0].id,openingAmount:0},{expected:[201]}),201)
const clients=[]
for(let i=0;i<cfg.users;i++){const c=new Client();await c.login();clients.push(c)}
const writes={patients:0,invoices:0,payments:0,sales:0,deliveries:0,expenses:0,replays:0}
const before=sql("SHOW GLOBAL STATUS WHERE Variable_name IN ('Threads_connected','Max_used_connections','Innodb_row_lock_waits','Innodb_row_lock_time','Slow_queries','Questions','Innodb_buffer_pool_reads','Innodb_buffer_pool_read_requests');")
save('mysql-before',before)
save('cash-opening-plan',JSON.parse(sql("EXPLAIN FORMAT=JSON SELECT COALESCE(SUM(l.Debit-l.Credit),0) FROM LedgerEntries l JOIN LedgerTransactions t ON l.TransactionId=t.Id WHERE l.AccountCode='1000' AND t.CreatedAt<DATE_SUB(DATE(DATE_ADD(UTC_TIMESTAMP(),INTERVAL 270 MINUTE)),INTERVAL 270 MINUTE)")))
const paths=[
 ['patients','/api/patients?pageSize=25&q=Synthetic0001'],['visits','/api/visits?pageSize=25'],['triage','/api/clinical/worklist?kind=triage'],['doctor','/api/clinical/worklist?kind=doctor'],
 ['invoices','/api/invoices?pageSize=25'],['payments','/api/payments?pageSize=25'],['stock','/api/inventory/products?pageSize=25'],['batches','/api/inventory/batches?productId=1'],
 ['pharmacy','/api/pharmacy/sales?pageSize=25'],['optical','/api/optical/orders?pageSize=25'],['diagnostic','/api/diagnostic-orders?pageSize=25'],['appointments','/api/appointments'],
 ['dashboard','/api/reports/dashboard'],['financial','/api/reports/financial'],['prescriptions','/api/prescriptions?pageSize=25'],['expenses','/api/expenses?pageSize=25']
]
async function cycle(client,index,iteration){
  const [name,path]=paths[(iteration+index)%paths.length];await client.request(name,'GET',path)
  if(iteration%4!==0)return
  const kind=(Math.floor(iteration/4)+index)%4
  const key=randomUUID().replaceAll('-','')
  if(kind===0){
    const body={firstName:`Load${index}-${iteration}`,lastName:'Synthetic',gender:'Male',estimatedAge:40}
    const r=await client.request('patient_create','POST','/api/patients',body,{key,expected:[201]})
    if(r.status===201){writes.patients++;if(iteration%20===0){const replay=await client.request('patient_replay','POST','/api/patients',body,{key,expected:[201]});if(replay.data?.id===r.data.id)writes.replays++;else throw Error('Patient idempotency mismatch')}}
  }else if(kind===1){
    const r=await client.request('invoice_create','POST','/api/invoices',{patientId:1+(index%cfg.patients),items:[{serviceId:service.id,quantity:1}]},{key,expected:[201]})
    if(r.status===201){writes.invoices++;const body={invoiceId:r.data.id,amount:100,method:'Cash'};const paymentKey=randomUUID().replaceAll('-','');const paid=await client.request('payment_create','POST','/api/payments',body,{key:paymentKey,expected:[201]});if(paid.status===201)writes.payments++}
  }else if(kind===2){
    const r=await client.request('sale_create','POST','/api/pharmacy/sales',{patientId:1+index,items:[{productId:1+index,quantity:1}]},{key,expected:[201]})
    if(r.status===201){
      writes.sales++
      const paid=await client.request('pharmacy_payment','POST','/api/payments',{invoiceId:r.data.invoiceId,amount:100,method:'Cash'},{key:randomUUID().replaceAll('-',''),expected:[201]})
      if(paid.status===201){
        writes.payments++
        const delivered=await client.request('sale_deliver','POST',`/api/pharmacy/sales/${r.data.id}/deliver`,{version:r.data.version},{expected:[204]})
        if(delivered.status===204)writes.deliveries++
      }
    }
  }else{
    const day=new Date(Date.now()+4.5*3600000).toISOString().slice(0,10)
    const r=await client.request('expense_create','POST','/api/expenses',{category:'Synthetic',description:'Load verification',amount:1,expenseDate:day},{key,expected:[201]});if(r.status===201)writes.expenses++
  }
}
phase='warmup'
for(const [name,path] of paths)await admin.request(name,'GET',path)
phase='load'
const startedUtc=new Date().toISOString(),started=performance.now(),until=started+cfg.seconds*1000
await Promise.all(clients.map(async(client,index)=>{let iteration=0;while(performance.now()<until){await cycle(client,index,iteration++);await new Promise(r=>setTimeout(r,25))}}))
const elapsed=(performance.now()-started)/1000
const finishedUtc=new Date().toISOString()
phase='drain';await new Promise(r=>setTimeout(r,3000))
// Validate every ledger transaction, invoice/payment total, invoice line sum and batch movement sum, not just sampled rows.
const integrity=Object.fromEntries(sql(`
SELECT 'unbalanced_ledger',COUNT(*) FROM (SELECT TransactionId FROM LedgerEntries GROUP BY TransactionId HAVING SUM(Debit)<>SUM(Credit)) x
UNION ALL SELECT 'payment_mismatch',COUNT(*) FROM Invoices i LEFT JOIN (SELECT InvoiceId,SUM(Amount) Paid FROM Payments GROUP BY InvoiceId) p ON p.InvoiceId=i.Id WHERE i.Paid<>COALESCE(p.Paid,0)
UNION ALL SELECT 'invoice_line_mismatch',COUNT(*) FROM Invoices i LEFT JOIN (SELECT InvoiceId,SUM(LineTotal) Total FROM InvoiceItems WHERE NOT IsVoid GROUP BY InvoiceId) l ON l.InvoiceId=i.Id WHERE i.Total<>COALESCE(l.Total,0)
UNION ALL SELECT 'overpaid_invoice',COUNT(*) FROM Invoices WHERE Paid+Discount>Total
UNION ALL SELECT 'negative_stock',COUNT(*) FROM StockBatches WHERE Quantity<0
UNION ALL SELECT 'stock_movement_mismatch',COUNT(*) FROM StockBatches b LEFT JOIN (SELECT BatchId,SUM(Quantity) Quantity FROM StockMovements GROUP BY BatchId) m ON m.BatchId=b.Id WHERE b.Quantity<>COALESCE(m.Quantity,0)
UNION ALL SELECT 'stock_ledger_mismatch',ABS((SELECT COALESCE(SUM(Debit-Credit),0) FROM LedgerEntries WHERE AccountCode='1200')-(SELECT COALESCE(SUM(Quantity*UnitCost),0) FROM StockBatches))
UNION ALL SELECT 'patient_count_mismatch',ABS(COUNT(*)-${cfg.patients+writes.patients}) FROM Patients
UNION ALL SELECT 'invoice_count_mismatch',ABS(COUNT(*)-${cfg.patients+writes.invoices+writes.sales}) FROM Invoices
UNION ALL SELECT 'payment_count_mismatch',ABS(COUNT(*)-${cfg.patients+writes.payments}) FROM Payments
UNION ALL SELECT 'committed_patient_missing_sync',COUNT(*) FROM Patients p WHERE p.Id>${cfg.patients} AND NOT EXISTS(SELECT 1 FROM SyncChanges s WHERE s.TableName='Patients' AND s.KeyJson=CONCAT('[',p.Id,']'))
UNION ALL SELECT 'unprocessed_outbox',COUNT(*) FROM OutboxEvents WHERE ProcessedAt IS NULL;
`).trim().split('\n').map(line=>{const [k,v]=line.split('\t');return[k,Number(v)]}))
save('mysql-after',sql("SHOW GLOBAL STATUS WHERE Variable_name IN ('Threads_connected','Max_used_connections','Innodb_row_lock_waits','Innodb_row_lock_time','Slow_queries','Questions','Innodb_buffer_pool_reads','Innodb_buffer_pool_read_requests');"))
save('innodb-diagnostics-private',sql('SHOW ENGINE INNODB STATUS'))
phase='cooldown';await new Promise(r=>setTimeout(r,15000));await admin.request('health','GET','/health')
const metrics=[...stats].map(([name,s])=>{const p=q=>{let t=0;for(let i=0;i<s.buckets.length;i++){t+=s.buckets[i];if(t>=Math.ceil(s.count*q))return i}return 30000};return{name,count:s.count,meanMs:Math.round(s.sum/s.count),p50Ms:p(.5),p95Ms:p(.95),p99Ms:p(.99),maxMs:Math.round(s.max),statuses:s.statuses,codes:s.codes,unexpected:s.unexpected}})
const requests=metrics.filter(m=>m.name.startsWith('load:')).reduce((n,m)=>n+m.count,0)
const unexpected=metrics.reduce((n,m)=>n+m.unexpected,0)
const stress=cfg.users>32
const latencyFailures=metrics.filter(m=>m.name.startsWith('load:')&&m.p95Ms>(stress?5000:m.name==='load:financial'?2000:1000)).map(m=>({name:m.name,p95Ms:m.p95Ms,limitMs:stress?5000:m.name==='load:financial'?2000:1000}))
const passed=unexpected===0&&Object.values(integrity).every(n=>n===0)&&latencyFailures.length===0
save('result',{passed,startedUtc,finishedUtc,seconds:elapsed,concurrentClients:cfg.users,requests,requestsPerSecond:requests/elapsed,writes,integrity,unexpected,latencyFailures,metrics})
console.log(JSON.stringify({passed,requests,requestsPerSecond:requests/elapsed,writes,integrity,unexpected}))
process.exitCode=passed?0:1
