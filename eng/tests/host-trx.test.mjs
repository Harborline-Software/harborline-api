import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync, writeFileSync, copyFileSync, existsSync, mkdtempSync, mkdirSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import * as host from '../host-baseline.mjs'
import {persistStepEvidence} from '../exact-clone-evidence.mjs'

const root = path.resolve(import.meta.dirname, '../..')
const fixture = path.join(import.meta.dirname, 'host-results.trx')
const duplicate = 'TRX fixture: duplicate & <quoted> "name"'
// T-724 ruling 119 CodeRabbit follow-up (2026-09-27): the two physical duplicate-named rows now get
// distinct rosterIds (the fixture carries no testId, so the backstop ordinal suffix applies), and
// knownTests must name BOTH to track them independently -- that per-row tracking is the whole point
// of the fix. permittedFailures stays testName-keyed (burnDown is about failure identity, unchanged).
const baseline = {permittedFailures: [{test: duplicate}], knownTests: [duplicate, `${duplicate} #2`]}
const compare = trx => host.compareHostBaseline({baseline, trx, counts: trx.counts,
  adjustedFailed: trx.counts?.failed, newFailures: trx.results.filter(r => r.outcome === 'Failed' && r.testName !== duplicate).map(r => r.testName)})
const countsFor = results => ({total: results.length, failed: results.filter(r => r.outcome === 'Failed').length,
  passed: results.filter(r => r.outcome === 'Passed').length, notExecuted: results.filter(r => r.outcome === 'NotExecuted').length})

test('TRX invariant: every outcome, PER-ROW duplicate multiplicity (rosterId), order and all three named directions', () => {
  const original = host.readHostTrx(fixture)
  assert.equal(compare(original).passed, true)
  const twinsOriginal = original.results.filter(r => r.testName === duplicate)
  const [firstId, secondId] = [twinsOriginal[0].rosterId, twinsOriginal[1].rosterId]
  // The fix's whole point: two rows sharing a DisplayName no longer share a roster identity.
  assert.notEqual(firstId, secondId)
  for (const first of ['Failed', 'Passed', 'NotExecuted']) for (const second of ['Failed', 'Passed', 'NotExecuted']) {
    for (const reverse of [false, true]) {
      const results = original.results.map(r => ({...r}))
      const twins = results.filter(r => r.testName === duplicate)
      twins[0].outcome = first; twins[1].outcome = second
      if (reverse) results.reverse()
      const result = compare({...original, results, counts: countsFor(results)})
      const ran = outcome => outcome === 'Passed' || outcome === 'Failed'
      const burnDown = first === 'Passed' || second === 'Passed' ? [duplicate] : []
      const disappeared = [...(ran(first) ? [] : [firstId]), ...(ran(second) ? [] : [secondId])].sort()
      const passed = burnDown.length === 0 && disappeared.length === 0
      assert.equal(result.passed, passed, `${first}/${second}/${reverse}`)
      assert.deepEqual(result.burnDown, burnDown, `${first}/${second}/${reverse}`)
      assert.deepEqual([...result.disappeared].sort(), disappeared, `${first}/${second}/${reverse}`)
      if (!passed) assert.ok(result.problems.length > 0)
    }
  }
  const results = original.results.map(r => ({...r, testName: r.outcome === 'Failed' ? 'Unlisted regression' : r.testName}))
  const regression = compare({...original, results})
  assert.equal(regression.passed, false)
  assert.ok(regression.problems.some(p => p.includes('unlisted failure')))
})

test('TRX reader preserves real logger counters, entities, duplicate names and outcomes', () => {
  const trx = host.readHostTrx(fixture)
  assert.deepEqual(trx.problems, [])
  // VSTest emits notExecuted=0 here despite its NotExecuted result; preserve the actual counter.
  assert.deepEqual(trx.counts, {total: 4, passed: 1, failed: 2, notExecuted: 0})
  // This fixture predates testId (no qualifier available), so the two duplicate-named rows fall
  // through to the ordinal backstop: the first keeps the bare testName, the second gets " #2".
  assert.deepEqual(trx.results, [
    {testName: 'TRX fixture: passed', outcome: 'Passed', rosterId: 'TRX fixture: passed'},
    {testName: duplicate, outcome: 'Failed', rosterId: duplicate}, {testName: duplicate, outcome: 'Failed', rosterId: `${duplicate} #2`},
    {testName: 'TRX fixture: skipped', outcome: 'NotExecuted', rosterId: 'TRX fixture: skipped'},
  ])
  const dir = mkdtempSync(path.join(tmpdir(), 'host-trx-reader-'))
  try {
    const file = path.join(dir, 'entities.trx')
    writeFileSync(file, readFileSync(fixture, 'utf8').replaceAll('&quot;name&quot;', '&#34;name&#x22; &apos; &amp;lt;'))
    assert.equal(host.readHostTrx(file).results[1].testName, 'TRX fixture: duplicate & <quoted> "name" \' &lt;')
  } finally { rmSync(dir, {recursive: true, force: true}) }
})

test('CodeRabbit 4113873155: testId disambiguates two theory cases sharing a DisplayName, so one can disappear without the other masking it', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'host-trx-testid-'))
  try {
    const file = path.join(dir, 'duplicate-names.trx')
    writeFileSync(file, [
      '<?xml version="1.0" encoding="utf-8"?>',
      '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">',
      '  <Results>',
      '    <UnitTestResult executionId="10ddacc1-a56d-4189-a782-5f92424e7bda" testId="aaaaaaaa-0000-0000-0000-000000000001" testName="Theory(n: 1)" outcome="Passed" />',
      '    <UnitTestResult executionId="ec2f4169-fa6e-403a-8bbc-d6db4991d941" testId="bbbbbbbb-0000-0000-0000-000000000002" testName="Theory(n: 1)" outcome="Passed" />',
      '  </Results>',
      '  <ResultSummary outcome="Passed">',
      '    <Counters total="2" executed="2" passed="2" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />',
      '  </ResultSummary>',
      '</TestRun>',
    ].join('\n'))
    const trx = host.readHostTrx(file)
    assert.deepEqual(trx.problems, [])
    // Same DisplayName, different rosterId: the testId qualifier keeps them apart.
    assert.equal(trx.results[0].testName, trx.results[1].testName)
    assert.notEqual(trx.results[0].rosterId, trx.results[1].rosterId)

    // Now prove the gate actually uses that: one case (by testId) stops running while its
    // same-named twin keeps passing. A testName-only Set would see "Theory(n: 1)" still in `ran`
    // and never notice; rosterId must catch it.
    const knownTests = trx.results.map(r => r.rosterId)
    const staleBaseline = {permittedFailures: [], knownTests, policyRemovals: []}
    const oneVanished = {...trx, results: [trx.results[0]], counts: {total: 1, passed: 1, failed: 0, notExecuted: 0}}
    const result = host.compareHostBaseline({
      baseline: staleBaseline, counts: oneVanished.counts,
      adjustedFailed: 0, newFailures: [], trx: oneVanished,
    })
    assert.equal(result.passed, false)
    assert.deepEqual(result.disappeared, [trx.results[1].rosterId])
  } finally { rmSync(dir, {recursive: true, force: true}) }
})

test('CodeRabbit 4113873155 backstop: two rows sharing BOTH testName and testId (a DisplayName-truncation collision) still get distinct rosterIds', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'host-trx-testid-collision-'))
  try {
    const file = path.join(dir, 'truncated.trx')
    writeFileSync(file, [
      '<?xml version="1.0" encoding="utf-8"?>',
      '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">',
      '  <Results>',
      '    <UnitTestResult executionId="10ddacc1-a56d-4189-a782-5f92424e7bda" testId="cccccccc-0000-0000-0000-000000000003" testName="Truncated case (method: GET, path: same-prefix)" outcome="Passed" />',
      '    <UnitTestResult executionId="ec2f4169-fa6e-403a-8bbc-d6db4991d941" testId="cccccccc-0000-0000-0000-000000000003" testName="Truncated case (method: GET, path: same-prefix)" outcome="Passed" />',
      '  </Results>',
      '  <ResultSummary outcome="Passed">',
      '    <Counters total="2" executed="2" passed="2" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />',
      '  </ResultSummary>',
      '</TestRun>',
    ].join('\n'))
    const trx = host.readHostTrx(file)
    assert.deepEqual(trx.problems, [])
    assert.notEqual(trx.results[0].rosterId, trx.results[1].rosterId)
    assert.deepEqual(host.rosterIdCollisions(trx.results.map(r => r.rosterId)), [], 'the reader itself must never hand out a colliding rosterId pair')
  } finally { rmSync(dir, {recursive: true, force: true}) }
})

test('TRX reader failures and incomplete results always produce a red named reason', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'host-trx-invalid-'))
  try {
    const file = path.join(dir, 'bad.trx')
    const raw = readFileSync(fixture, 'utf8')
    const variants = [null, '', raw.replace('</TestRun>', ''), raw.replace(/<Counters[^>]+\/>/, ''),
      raw.replace('failed="2"', 'failed="3"'), raw.replace('total="4"', 'total="5"'),
      raw.replace('passed="1"', 'passed="2"'), raw.replace('total="4"', 'total="NaN"'),
      raw.replace('outcome="Passed"', 'outcome="Aborted"'), raw.replace('testName="TRX fixture: passed"', 'testName=""'),
      raw.replace(/<UnitTestResult[^>]+\/>/, ''), raw.replace('total="4"', 'total="0"')]
    for (const xml of variants) {
      if (xml !== null) writeFileSync(file, xml)
      const trx = host.readHostTrx(file)
      const result = compare(trx)
      assert.equal(result.passed, false, String(xml))
      assert.ok(result.problems.length > 0)
      assert.equal(result.tail, result.problems.join('\n'))
    }
  } finally { rmSync(dir, {recursive: true, force: true}) }
})

test('exact-clone uses TRX for named results and retains the raw diagnostic file on every red verdict', () => {
  const source = readFileSync(path.join(root, 'eng/run-exact-clone.mjs'), 'utf8').replaceAll('\r\n', '\n')
  const hostBlock = source.slice(source.indexOf('  const hostResultsDirectory ='), source.indexOf("  run('analyzer-canary'"))
  assert.ok(hostBlock.length > 0, 'host results setup must exist')
  const dir = mkdtempSync(path.join(tmpdir(), 'host-trx-runner-'))
  try {
    let invocation
    new Function('path', 'clone', 'run', 'collectCoverage', hostBlock)(path, dir, (...args) => { invocation = args; return {} }, false)
    assert.equal(invocation[0], 'dotnet-host-tests')
    // Q38: the host suite excludes the perf lane; verify-perf runs it on mac16.
    assert.deepEqual(invocation[2], ['test', 'apps/local-node-host/tests/tests.csproj', '-c', 'Release', '--nologo', '--no-build', '-nodeReuse:false', '-maxcpucount:6',
      '--filter', 'Lane!=perf', '--logger', 'trx;LogFileName=host-tests.trx', '--results-directory', path.join(dir, 'TestResults', 'host')])
    assert.doesNotMatch(hostBlock, /console;verbosity|hostBaseline/)
    // T-724 ruling 119: every baseline is identity-based now, so host counts always come from TRX,
    // never from the console summary regex (that route remains only for capability's text tail).
    assert.match(source, /const hostTrx = readHostTrx\(path\.join\(hostResultsDirectory, 'host-tests\.trx'\)\)/)
    assert.match(source, /const hostCounts = hostTrx\.counts/)
    assert.match(source, /const observedFailures = hostTrx\.results\.filter\(row => row\.outcome === 'Failed'\)\.map\(row => row\.testName\)/)
    // A capability test title can interpolate the clone's own absolute path (e.g. the
    // operational-environment Python-worker table), and that path is fresh (mkdtempSync) every
    // run, so an identity built from the raw title is never stable across two runs -- caught by
    // running the real gate twice and seeing capability-baseline-match go red on its own committed
    // knownTests. redactEvidence must run over every capability test name before it becomes an identity.
    assert.match(source, /testName: normalizeIdentity\(redactEvidence\(row\.testName\)\), rosterId: normalizeIdentity\(redactEvidence\(row\.rosterId\)\)/)
    const compareBlock = source.slice(source.indexOf('  const hostComparison ='), source.indexOf("  steps.push({\n    id: 'host-baseline-match'"))
    assert.ok(compareBlock.includes('rawOutput'), 'comparison must persist raw output')
    const rawOutput = `${String.fromCharCode(27)}[31mraw host output\r\nTest Run Failed.\r\n`
    for (const reason of ['TRX missing', 'TRX incomplete', 'unlisted failure', 'burn-down', 'missing result', 'duplicate row']) {
      const hostComparison = {passed: false, problems: [reason], tail: reason}
      const printed = []
      const actual = new Function('compareHostBaseline', 'hostBaseline', 'hostCounts', 'adjustedFailed', 'newFailures',
        'hostTrx', 'hostTests', 'hostResultsDirectory', 'path', 'mkdirSync', 'writeFileSync', 'copyFileSync', 'existsSync', 'apiRoot', 'console',
        'let retainScratch = false;\n' + compareBlock + '\nreturn {hostComparison, retainScratch}')(
        () => hostComparison, baseline, null, null, [], {}, {rawOutput, fullOutput: 'stripped'},
        path.join(dir, 'TestResults', 'host'), path,
        mkdirSync, writeFileSync, copyFileSync, existsSync, dir, {log: line => printed.push(line)})
      assert.equal(actual.retainScratch, true)
      const outputFile = path.join(dir, 'TestResults', 'host', 'host-tests-output.txt')
      assert.equal(readFileSync(outputFile, 'utf8'), rawOutput)
      assert.ok(printed.some(line => line.includes(reason) && line.includes(outputFile)))
      assert.ok(actual.hostComparison.tail.includes(outputFile))
    }
    assert.match(source, /if \(!retainScratch\) rmSync\(scratch, \{recursive: true, force: true\}\)/)
    // Exercise the runner's actual redactor and persistence call rather than pinning
    // the location or variable names of its serialization destructuring.
    const redactorBlock = source.slice(source.indexOf('const ANSI ='), source.indexOf('\n// `expectNonZero`', source.indexOf('const ANSI =')))
    const redactEvidence = new Function('clone', 'scratch', redactorBlock + '\nreturn redactEvidence')(path.join(dir, 'clone'), dir)
    const persistenceCall = source.split('\n').find(line => line.startsWith('const persisted ='))
    assert.ok(persistenceCall, 'runner must persist step evidence')
    const report = {status: 'FAIL', apiCommit: 'fixture-head', steps: [{id: 'host-baseline-match', passed: false,
      fullOutput: `${String.fromCharCode(27)}[31m${path.join(dir, 'clone', 'tests')}: unexpected failure\n`,
      rawOutput: 'private raw diagnostic'}]}
    const persisted = new Function('persistStepEvidence', 'report', 'apiRoot', 'redactEvidence',
      persistenceCall + '\nreturn persisted')(persistStepEvidence, report, dir, redactEvidence)
    assert.equal(persisted.status, 'FAIL')
    assert.equal(persisted.steps[0].passed, false)
    assert.equal('fullOutput' in persisted.steps[0], false)
    assert.equal('rawOutput' in persisted.steps[0], false)
    assert.equal(path.isAbsolute(persisted.steps[0].outputFile), false)
    assert.equal(readFileSync(path.join(dir, persisted.steps[0].outputFile), 'utf8'),
      `<exact-clone>${path.sep}tests: unexpected failure\n`)
    assert.doesNotMatch(JSON.stringify(persisted), /private raw diagnostic|unexpected failure/)

  } finally { rmSync(dir, {recursive: true, force: true}) }
})
