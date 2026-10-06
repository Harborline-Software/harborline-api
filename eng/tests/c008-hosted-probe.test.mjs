import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import {instrument,counters,requireTestIdentity} from '../c008-hosted-probe.mjs'
test('instrumentation leaves negative fixture and positive assertions intact',()=>{
  const source=readFileSync(new URL('../../apps/local-node-host/tests/Health/HostBootSmokeTests.cs',import.meta.url),'utf8')
  const result=instrument(source)
  const negative=s=>s.slice(s.indexOf('    [Fact(DisplayName = "host-boot BLOCKER-1 (bite)'),s.indexOf('    private ',s.indexOf('    [Fact(DisplayName = "host-boot BLOCKER-1 (bite)')))
  assert.equal(negative(result),negative(source))
  for(const assertion of ['Assert.Same(worker, rebindStatus);','Assert.True(endpointRegistry.IsSealed);','Assert.True(rebindStatus.LastRebind.IsHealthy);']) assert.ok(result.includes(assertion))
  assert.throws(()=>instrument(result),/source shape changed/)
})
test('receipt parsing rejects empty or skipped execution',()=>{
  assert.equal(counters('<Counters total="1" executed="1" passed="1" failed="0" notExecuted="0"/>').passed,1)
  assert.throws(()=>counters('<Counters total="0" executed="0" passed="0" failed="0" notExecuted="0"/>'),/exactly one/)
  assert.throws(()=>counters('<Counters total="1" executed="0" passed="0" failed="0" notExecuted="1"/>'),/exactly one/)
  assert.throws(()=>counters('missing'),/missing/)
})
test('a passing unrelated case is not causal evidence',()=>{
  const xml='<TestMethod className="Harborline.Api.LocalNodeHost.Tests.Health.HostBootSmokeTests" name="Real_Host_Starts_Without_DiCycle"/>'
  requireTestIdentity(xml,'Real_Host_Starts_Without_DiCycle')
  assert.throws(()=>requireTestIdentity(xml,'Buggy_Registration_Cycles_At_StartAsync_Bite'),/different test/)
  assert.throws(()=>requireTestIdentity(xml+xml,'Real_Host_Starts_Without_DiCycle'),/ambiguous/)
})
