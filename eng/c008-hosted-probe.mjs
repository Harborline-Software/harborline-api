import {readFileSync, writeFileSync, mkdirSync} from 'node:fs'
import {execFileSync, spawnSync} from 'node:child_process'
import {createHash} from 'node:crypto'
import {Worker} from 'node:worker_threads'
import path from 'node:path'
import {fileURLToPath} from 'node:url'

const file='apps/local-node-host/tests/Health/HostBootSmokeTests.cs'
const project='apps/local-node-host/tests/tests.csproj'
const hash=text=>createHash('sha256').update(text).digest('hex')
function once(text, before, after) {
  if(text.split(before).length!==2) throw Error(`source shape changed: ${before}`)
  return text.replace(before,after)
}
export function instrument(source) {
  if(source.includes('C008ShutdownTokenObserver')) throw Error('source shape changed: already instrumented')
  const start=source.indexOf('    public async Task Real_Host_Starts_Without_DiCycle()')
  const end=source.indexOf('    [Fact(DisplayName = "host-boot BLOCKER-1 (bite)',start)
  if(start<0||end<0) throw Error('positive/negative test boundaries missing')
  let body=source.slice(start,end)
  body=once(body,'        var (builder, dataDir) = NewHostBuilder();',`        var (builder, dataDir) = NewHostBuilder();
        var probeClock = System.Diagnostics.Stopwatch.StartNew();
        void ProbeTrace(string phase, CancellationToken token) =>
            File.AppendAllText(Environment.GetEnvironmentVariable("HARBORLINE_C008_TRACE")!,
                System.Text.Json.JsonSerializer.Serialize(new { phase, elapsedMs = probeClock.Elapsed.TotalMilliseconds,
                    canceled = token.IsCancellationRequested }) + "\\n");
        if (Environment.GetEnvironmentVariable("HARBORLINE_C008_FORCE_EXPIRED") == "1")
            builder.Services.AddHostedService<C008ShutdownTokenObserver>();`)
  body=once(body,'            await host.StartAsync(cts.Token);',`            ProbeTrace("startup-begin", cts.Token);
            try { await host.StartAsync(cts.Token); ProbeTrace("startup-end", cts.Token); }
            catch { ProbeTrace("startup-error", cts.Token); throw; }`)
  body=once(body,'            await host.StopAsync(cts.Token);',`            if (Environment.GetEnvironmentVariable("HARBORLINE_C008_FORCE_EXPIRED") == "1") cts.Cancel();
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stopToken = Environment.GetEnvironmentVariable("HARBORLINE_C008_SEPARATE_STOP") == "1"
                ? stopCts.Token : cts.Token;
            ProbeTrace("shutdown-begin", stopToken);
            try { await host.StopAsync(stopToken); ProbeTrace("shutdown-end", stopToken); }
            catch { ProbeTrace("shutdown-error", stopToken); throw; }`)
  const observer=`    private sealed class C008ShutdownTokenObserver : IHostedService
    {
        public C008ShutdownTokenObserver() { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested)
                throw new OperationCanceledException("C008 probe observed expired shutdown token");
            return Task.CompletedTask;
        }
    }

`
  return once(source.slice(0,start)+body+source.slice(end),'    private static void TryDelete(string dir)',observer+'    private static void TryDelete(string dir)')
}
export function counters(xml) {
  const tag=xml.match(/<Counters\s+([^>]+)>/)
  if(!tag) throw Error('TRX counters missing')
  const data=Object.fromEntries([...tag[1].matchAll(/(\w+)="(\d+)"/g)].map(m=>[m[1],Number(m[2])]))
  if(data.total!==1||data.executed!==1||data.notExecuted!==0||data.passed+data.failed!==1)
    throw Error('TRX did not execute exactly one test with no skips')
  return data
}
async function main() {
  if(process.platform!=='win32') throw Error('causal execution is Windows-only')
  const git=(...args)=>execFileSync('git',args,{encoding:'utf8'}).trim()
  const head=git('rev-parse','HEAD')
  if(head!==process.env.C008_EXPECTED_HEAD||git('status','--porcelain')) throw Error('head mismatch or dirty source')
  const original=readFileSync(file,'utf8'), changed=instrument(original)
  const directory=path.resolve('artifacts/c008-probe',`${process.env.GITHUB_RUN_ID}-${process.env.GITHUB_RUN_ATTEMPT}`)
  mkdirSync(directory,{recursive:true})
  const manifest={schemaVersion:1,head,tree:git('rev-parse','HEAD^{tree}'),sourceSha256:hash(original),
    instrumentedSha256:hash(changed),sdk:execFileSync('dotnet',['--version'],{encoding:'utf8'}).trim(),
    runtime:process.version,runId:process.env.GITHUB_RUN_ID,attempt:process.env.GITHUB_RUN_ATTEMPT,
    hostValidationPerformed:false,coveragePerformed:false,baselineChanged:false,
    limitation:'20 measured repetitions under one two-worker CPU load profile; not historical root-cause proof, full gate, mutation coverage or automatic retirement authority',results:[]}
  const save=()=>writeFileSync(path.join(directory,'result.json'),JSON.stringify(manifest,null,2))
  save()
  const run=(args,output,env=process.env,timeout=120000)=> {
    const p=spawnSync('dotnet',args,{encoding:'utf8',env,timeout,maxBuffer:64*1024*1024})
    writeFileSync(output,`${p.stdout??''}\n${p.stderr??''}`)
    if(p.error) throw p.error
    return p.status
  }
  try {
    writeFileSync(file,changed)
    if(run(['build',project,'-c','Release','--nologo','-nodeReuse:false','-maxcpucount:1'],path.join(directory,'build.log'),process.env,1200000)!==0)
      throw Error('build failed; no causal result')
    const samples=[]
    for(const separate of [false,true]) for(const loaded of [false,true]) for(let i=0;i<5;i++)
      samples.push({name:`${separate?'separate':'shared'}-${loaded?'load':'quiet'}-${i}`,separate,loaded,expired:false,negative:false})
    samples.push({name:'expired-shared',separate:false,loaded:false,expired:true,negative:false},
      {name:'expired-separate',separate:true,loaded:false,expired:true,negative:false},
      {name:'negative-di-quiet',negative:true,loaded:false},{name:'negative-di-load',negative:true,loaded:true})
    for(const sample of samples) {
      const dir=path.join(directory,sample.name);mkdirSync(dir)
      const trace=path.join(dir,'phases.jsonl')
      const env={...process.env,HARBORLINE_C008_TRACE:trace,HARBORLINE_C008_SEPARATE_STOP:sample.separate?'1':'0',HARBORLINE_C008_FORCE_EXPIRED:sample.expired?'1':'0'}
      const stop=new SharedArrayBuffer(4), flag=new Int32Array(stop)
      const workers=sample.loaded?[0,1].map(()=>new Worker(`const {workerData}=require('node:worker_threads'); const f=new Int32Array(workerData); let n=1; while(Atomics.load(f,0)===0){ for(let i=0;i<100000;i++)n=(n*1664525+1013904223)|0; }`,{eval:true,workerData:stop})):[]
      try {
        // Allow both workers to start before the synchronous dotnet invocation.
        await Promise.all(workers.map(w=>new Promise((resolve,reject)=>{w.once('online',resolve);w.once('error',reject)})))
        const method=sample.negative?'Buggy_Registration_Cycles_At_StartAsync_Bite':'Real_Host_Starts_Without_DiCycle'
        const status=run(['test',project,'-c','Release','--no-build','--no-restore','--nologo',
          '--filter',`FullyQualifiedName=Harborline.Api.LocalNodeHost.Tests.Health.HostBootSmokeTests.${method}`,
          '--logger','trx;LogFileName=sample.trx','--results-directory',dir],path.join(dir,'test.log'),env)
        const xml=readFileSync(path.join(dir,'sample.trx'),'utf8'), count=counters(xml)
        const phases=sample.negative?[]:readFileSync(trace,'utf8').trim().split('\n').map(line=>JSON.parse(line))
        const control=sample.expired&&!sample.separate
        const accepted=control?status!==0&&count.failed===1&&xml.includes('C008 probe observed expired shutdown token')
          &&phases.some(p=>p.phase==='shutdown-begin'&&p.canceled):status===0&&count.passed===1
          &&(sample.negative||phases.some(p=>p.phase==='shutdown-end'&&!p.canceled))
        manifest.results.push({...sample,status,count,phases,accepted});save()
      } finally {
        Atomics.store(flag,0,1)
        await Promise.all(workers.map(w=>w.terminate()))
      }
    }
    manifest.completed=true
    manifest.accepted=manifest.results.length===24&&manifest.results.every(r=>r.accepted)
    if(!manifest.accepted) throw Error('causal qualification contains unexpected failures; inspect raw evidence')
  } catch(error) {
    manifest.error=String(error);process.exitCode=1
  } finally {
    writeFileSync(file,original);save()
    console.log(JSON.stringify({evidence:directory,completed:manifest.completed??false,accepted:manifest.accepted??false,error:manifest.error}))
  }
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) await main()
