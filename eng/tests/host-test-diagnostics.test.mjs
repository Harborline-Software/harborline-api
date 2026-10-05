import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, writeFileSync, appendFileSync, readFileSync, rmSync} from 'node:fs'
import {spawn} from 'node:child_process'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {diagnosticReader, sampleCpu} from '../host-test-diagnostics.mjs'

test('actual VSTest TraceSource headers supply PIDs to the Windows CPU probe', t => {
  const root = mkdtempSync(path.join(tmpdir(), 'host-vstest-prefix-')); t.after(() => rmSync(root, {recursive: true, force: true}))
  // Format source: microsoft/vstest issue3127 diagnostic output; event id 0 precedes PID.
  writeFileSync(path.join(root, 'vstest.log'), [
    'TpTrace Information: 0 : 1152, 7, time, BlameCollector.EventsTestCaseStart: Test Case Start',
    'TpTrace Verbose: 0 : 2233, 7, time, tracing',
    'TpTrace Warning: 0 : 3344, 7, time, tracing',
    'TpTrace Error: 0 : 4455, 7, time, tracing',
    'TpTrace Information: 0 : 1152, 7, time, BlameCollector.EventsTestCaseEnd: Test Case End',
    'TpTrace Information: 0 : 0, 7, time, invalid zero PID',
  ].join('\n') + '\n')
  const result = diagnosticReader(root)(100)
  assert.deepEqual(result.traceWriterPids, [1152, 2233, 3344, 4455])
  assert.equal(result.testStarts, 1); assert.equal(result.testEnds, 1)
  const cpu = sampleCpu(result.traceWriterPids, {platform: 'win32', execute: (command, args) => {
    assert.match(args.at(-1), /Get-Process -Id 1152,2233,3344,4455 /)
    return {status: 0, stdout: '{"pid":1152,"cpuMs":321}'}
  }})
  assert.deepEqual(cpu, {available: true, processes: [{pid: 1152, cpuMs: 321}]})
})

test('split multibyte whitespace preserves an event across the bounded read boundary', t => {
  const root = mkdtempSync(path.join(tmpdir(), 'host-utf8-boundary-')); t.after(() => rmSync(root, {recursive: true, force: true}))
  const marker = Buffer.from('BlameCollector.EventsTestCaseStart: Test Case Start')
  const bytes = Buffer.alloc(1024 * 1024 + 2, 0x20)
  marker.copy(bytes, 1024 * 1024 - 1 - marker.length)
  bytes[1024 * 1024 - 1] = 0xc2; bytes[1024 * 1024] = 0xa0; bytes[1024 * 1024 + 1] = 0x0a
  writeFileSync(path.join(root, 'vstest.log'), bytes)
  const observe = diagnosticReader(root)
  assert.equal(observe(100).caughtUp, false)
  const complete = observe(200)
  assert.equal(complete.testStarts, 1, 'literal event plus valid UTF-8 NBSP is one start')
  assert.equal(complete.lastTestActivityAt, 200)
  assert.equal(complete.caughtUp, true)
})

test('truncation resets partial UTF-8 and partial line before reading a new trace', t => {
  const root = mkdtempSync(path.join(tmpdir(), 'host-utf8-truncate-')); t.after(() => rmSync(root, {recursive: true, force: true}))
  const file = path.join(root, 'vstest.log'), observe = diagnosticReader(root)
  writeFileSync(file, Buffer.concat([Buffer.from('old partial line '.repeat(20)), Buffer.from([0xc2])]))
  assert.equal(observe(100).testStarts, 0)
  writeFileSync(file, 'TpTrace Information: 0 : 999, 1, time, BlameCollector.EventsTestCaseEnd: Test Case End\n')
  const fresh = observe(200)
  assert.equal(fresh.testStarts, 0)
  assert.equal(fresh.testEnds, 1)
  assert.deepEqual(fresh.traceWriterPids, [999], 'a pending old byte cannot corrupt the new PID prefix')
  assert.equal(fresh.lastTestActivityAt, 200)
})

test('VSTest Info event counts persist without raw data and distinguish trace activity from test progress', t => {
  const root = mkdtempSync(path.join(tmpdir(), 'host-observation-')); t.after(() => rmSync(root, {recursive: true, force: true}))
  const file = path.join(root, 'vstest.datacollector.log'), observe = diagnosticReader(root)
  writeFileSync(file, 'TpTrace Info: 123, 7, time, BlameCollector.EventsTestCaseStart: Test Case Start\nprivate-token and arbitrary test output\n')
  assert.deepEqual(observe(100), {available: true, caughtUp: true, testStarts: 1, testEnds: 0,
    lastTraceActivityAt: 100, lastTestActivityAt: 100, traceWriterPids: [123]})
  appendFileSync(file, 'TpTrace Info: 123, 7, time, private-token still busy\n')
  assert.equal(observe(200).lastTestActivityAt, 100)
  assert.equal(observe(300).lastTraceActivityAt, 200)
  appendFileSync(file, 'TpTrace Info: 123, 7, time, BlameCollector.EventsTestCaseEnd: Test Case End')
  assert.equal(observe(400).testEnds, 0, 'partial lines do not invent completed events')
  appendFileSync(file, '\n')
  assert.equal(observe(500).testEnds, 1)
  assert.doesNotMatch(JSON.stringify(observe(600)), /private-token|arbitrary/)
  for (let index = 0; index < 17; index++) writeFileSync(path.join(root, `vstest.extra-${index}.log`), '')
  assert.equal(observe(700).caughtUp, false, 'omitted logs cannot claim complete observation')
})

test('CPU metadata is numeric and unavailable probes do not become zero CPU claims', () => {
  assert.deepEqual(sampleCpu([123], {platform: 'linux'}), {available: false, processes: []})
  const execute = (command, args, options) => {
    assert.equal(command, 'powershell.exe'); assert.equal(options.shell, false); assert.equal(options.timeout, 3000)
    assert.doesNotMatch(args.join(' '), /CommandLine|Environment|private/)
    return {status: 0, stdout: '[{"pid":123,"cpuMs":456,"secret":"private"},{"pid":987,"cpuMs":5}]'}
  }
  assert.deepEqual(sampleCpu([123, 'private'], {platform: 'win32', execute}), {available: true, processes: [{pid: 123, cpuMs: 456}]})
  assert.deepEqual(sampleCpu([123], {platform: 'win32', execute: () => {throw Error('private')}}), {available: false, processes: []})
})

test('negative hang control retains advancing heartbeat with stationary test events before parent termination', async t => {
  const root = mkdtempSync(path.join(tmpdir(), 'host-hang-')); t.after(() => rmSync(root, {recursive: true, force: true}))
  const journal = path.join(root, 'journal.jsonl'), trace = path.join(root, 'vstest.log'), fixture = path.join(root, 'fixture.mjs')
  writeFileSync(fixture, `import {observedSpawnSync} from ${JSON.stringify(new URL('../exact-clone-progress.mjs', import.meta.url).href)};
    observedSpawnSync('dotnet-host-tests', process.execPath, ['-e', ${JSON.stringify(`require('fs').writeFileSync(${JSON.stringify(trace)}, 'TpTrace Information: 0 : '+process.pid+', 1, time, BlameCollector.EventsTestCaseStart: Test Case Start\\n');setInterval(()=>{},1000)`)}],
      {encoding:'utf8'}, {file:${JSON.stringify(journal)},diagnosticDirectory:${JSON.stringify(root)},intervalMs:50});`)
  const parent = spawn(process.execPath, [fixture], {stdio: 'ignore'})
  let lines = [], deadline = Date.now() + 10000
  while (Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 40))
    try {lines = readFileSync(journal, 'utf8').trim().split('\n').map(line => JSON.parse(line))} catch {}
    if (lines.filter(line => line.diagnostics?.testStarts === 1).length >= 3) break
  }
  // Kill only this owned fixture tree. The production observer never terminates its child.
  const childPid = lines.find(line => line.diagnostics?.traceWriterPids.length)?.diagnostics.traceWriterPids[0]
  if (childPid) try {process.kill(childPid)} catch {}
  parent.kill()
  await new Promise(resolve => parent.once('close', resolve))
  const running = lines.filter(line => line.diagnostics?.testStarts === 1)
  assert.ok(running.length >= 3, JSON.stringify(lines))
  assert.ok(running.at(-1).elapsedMs > running[0].elapsedMs)
  assert.ok(running.every(line => line.diagnostics.testEnds === 0))
  assert.equal(running.at(-1).diagnostics.lastTestActivityAt, running[0].diagnostics.lastTestActivityAt)
  if (process.platform === 'win32') assert.ok(running.some(line => line.cpu.available
    && line.cpu.processes.some(item => item.pid === childPid && Number.isFinite(item.cpuMs))), 'real owned-child CPU was observable')
})
