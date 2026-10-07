// Raw reports are read by reviewed controller bytes, independently of summary claims.
import assert from 'node:assert/strict'
import {execFileSync} from 'node:child_process'
import {existsSync,readFileSync,writeFileSync} from 'node:fs'
import path from 'node:path'
import {pathToFileURL} from 'node:url'
const [kind,out,api,mode='write']=process.argv.slice(2)
const reader=existsSync(path.join(import.meta.dirname,'host-baseline.mjs')) ? path.join(import.meta.dirname,'host-baseline.mjs') : path.join(import.meta.dirname,'../host-baseline.mjs')
const {readHostTrx,readVitestJsonAsTrx,normalizeIdentity,compareHostBaseline}=await import(pathToFileURL(reader))
let result
if (kind.startsWith('portable')||kind==='native-full') {
  const raw=readHostTrx(path.join(out,'gate-evidence/host-tests.trx'))
  const named=JSON.parse(readFileSync(path.join(out,'gate-evidence/named-test-outcomes.json')))
  assert.equal(raw.problems.length,0)
  assert.deepEqual(raw,named.host)
  assert.ok(raw.counts.total>0)
  assert.equal(raw.results.length,raw.counts.total)
  assert.equal(raw.counts.failed,raw.results.filter(x=>x.outcome==='Failed').length)
  assert.equal(raw.counts.passed,raw.results.filter(x=>x.outcome==='Passed').length)
  const skipped=raw.results.filter(x=>x.outcome==='NotExecuted').length
  assert.equal(raw.counts.total,raw.counts.passed+raw.counts.failed+skipped)
  if(kind.startsWith('portable'))assert.deepEqual(named.runtime,{platform:'linux',architecture:'arm64'})
  if(kind==='native-full') {
    const context=JSON.parse(readFileSync(path.join(out,'gate-evidence/native-context.json')))
    const platform={windows:'win32',macos:'darwin'}[context.platform]
    assert.ok(platform)
    assert.deepEqual(named.runtime,{platform,architecture:context.architecture})
    assert.equal(named.apiCommit,context.sha)
    assert.equal(named.runId,context.runId);assert.equal(named.attempt,context.attempt)
    const cap=readVitestJsonAsTrx(path.join(out,'gate-evidence/capability-tests.json'))
    assert.equal(cap.problems.length,0)
    for(const key of ['clone','scratch'])assert.ok(typeof named.evidenceRoots?.[key]==='string'&&named.evidenceRoots[key].length>1)
    const redact=value=>{
      for(const [key,replacement] of [['clone','<exact-clone>'],['scratch','<exact-clone-root>']]) {
        const root=named.evidenceRoots[key];value=value.replaceAll(root,replacement).replaceAll(root.replaceAll('\\','/'),replacement)
      }
      return normalizeIdentity(value)
    }
    cap.results=cap.results.map(row=>({...row,testName:redact(row.testName),rosterId:redact(row.rosterId)}))
    assert.deepEqual(cap,named.capability)
    const report=JSON.parse(readFileSync(path.join(out,'gate-evidence/exact-clone-report.json')))
    assert.equal(report.status,'PASS');assert.equal(report.apiCommit,context.sha)
    assert.equal(report.repository,'harborline-api');assert.equal(report.gate,'destination-exact-clone')
    assert.ok(report.steps.length>0&&report.steps.every(step=>step.passed===true))
    for(const [key,trx,baselinePath] of [['host',raw,context.platform==='windows'?'eng/baselines/host-test-baseline.json':'eng/baselines/host-test-baseline.macos.json'],
                                       ['capability',cap,context.platform==='windows'?'eng/baselines/hull-test-baseline.json':'eng/baselines/hull-test-baseline.macos.json']]) {
      const git=(...args)=>execFileSync('git',['-C',api,...args],{encoding:'utf8'}).trim()
      const baseline=JSON.parse(git('show',context.sha+':'+baselinePath))
      assert.equal(report.baselineProvenance[key].committedBlob,git('rev-parse',context.sha+':'+baselinePath))
      assert.equal(report.baselineProvenance[key].workingBlob,report.baselineProvenance[key].committedBlob)
      assert.equal(report.baselineProvenance[key].matchesCommitted,true)
      assert.equal(named[key==='host'?'hostBaseline':'capabilityBaseline'],baselinePath)
      const step=report.steps.find(x=>x.id===key+'-baseline-match');assert.ok(step&&step.passed===true)
      const retry=key==='host'?report.steps.find(x=>x.id==='host-flake-retry'):null
      if(key==='host')assert.ok(retry&&Array.isArray(retry.retries)&&Number.isSafeInteger(step.rescuedByRetry))
      const rows=retry?.retries??[]
      const rescued=new Set(rows.filter(row=>row.green).map(row=>row.test))
      const flaky=new Set((baseline.knownFlaky??[]).map(x=>x.test))
      assert.ok([...rescued].every(x=>flaky.has(x)))
      assert.equal(rescued.size,rows.filter(row=>row.green).length,'Duplicate retry identity')
      for(const row of rows) {
        assert.ok(flaky.has(row.test)&&raw.results.some(x=>x.testName===row.test&&x.outcome==='Failed'))
        assert.equal(row.attempts,1);assert.equal(row.outcomes.length,2)
        assert.equal(row.outcomes[0].attempt,0);assert.equal(row.outcomes[0].green,false)
        const measured=row.outcomes[1]
        assert.equal(measured.attempt,1);assert.equal(measured.green,row.green)
        if(row.green) {
          assert.equal(row.targeted,true);assert.ok(measured.counts.total>0)
          assert.equal(measured.counts.failed,0)
          assert.equal(measured.counts.total,measured.counts.passed+measured.counts.skipped)
        }
      }
      if(key==='host') {
        assert.equal(step.rescuedByRetry,rescued.size);assert.equal(retry.rescued,rescued.size)
        const unexpected=raw.results.filter(x=>x.outcome==='Failed'&&!new Set((baseline.permittedFailures??[]).map(x=>x.test)).has(x.testName)).map(x=>x.testName)
        assert.deepEqual(retry.unexpectedFailures,unexpected)
        assert.deepEqual(retry.retriedUnderKnownFlaky,unexpected.every(x=>flaky.has(x))?unexpected:[])
        assert.deepEqual(rows.map(x=>x.test),retry.retriedUnderKnownFlaky)
      }
      const permitted=new Set((baseline.permittedFailures??[]).map(x=>x.test))
      const newFailures=trx.results.filter(x=>x.outcome==='Failed'&&!permitted.has(x.testName)&&!rescued.has(x.testName)).map(x=>x.testName)
      const comparison=compareHostBaseline({baseline,trx,newFailures,adjustedFailed:trx.counts.failed-rescued.size})
      assert.equal(comparison.passed,true,comparison.problems.join('; '))
    }
  }
  result={host:raw.counts,skippedResultRows:skipped,capability:named.capability.counts}
} else {
  assert.equal(kind,'mutation-benchmark')
  assert.equal(readFileSync(path.join(out,'mutation-exit.txt'),'utf8').trim(),'0')
  const report=JSON.parse(readFileSync(path.join(out,'mutation-reports/report.json')))
  const mutants=Object.values(report.files).flatMap(x=>x.mutants)
  assert.ok(mutants.length>0,'No generated mutants')
  const counts={}
  for(const mutant of mutants)counts[mutant.status]=(counts[mutant.status]??0)+1
  const tested=(counts.Killed??0)+(counts.Survived??0)+(counts.Timeout??0)
  assert.ok(tested>0,'No mutant tested')
  assert.ok(Object.keys(counts).every(x=>['Killed','Survived','Timeout','NoCoverage','CompileError','Ignored'].includes(x)),'Unknown status')
  result={project:'tests/Harborline.Api.Tests/Harborline.Api.Tests.csproj',tested,generated:mutants.length,counts,scope:'one small configured project; no whole-host or paired-mutation capacity claim'}
}
if(mode==='check')assert.deepEqual(JSON.parse(readFileSync(path.join(out,'raw-validation.json'))),result)
else {assert.equal(mode,'write');writeFileSync(path.join(out,'raw-validation.json'),JSON.stringify(result)+'\n')}
