import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync, writeFileSync, mkdtempSync, mkdirSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import * as host from '../host-baseline.mjs'

const root = path.resolve(import.meta.dirname, '../..')
const fixture = path.join(import.meta.dirname, 'host-results.trx')
const duplicate = 'TRX fixture: duplicate & <quoted> "name"'
const baseline = {comparison: 'named', permittedFailures: [{test: duplicate}]}
const compare = trx => host.compareHostBaseline({baseline, trx, counts: trx.counts,
  adjustedFailed: trx.counts?.failed, newFailures: trx.results.filter(r => r.outcome === 'Failed' && r.testName !== duplicate).map(r => r.testName)})
const countsFor = results => ({total: results.length, failed: results.filter(r => r.outcome === 'Failed').length,
  passed: results.filter(r => r.outcome === 'Passed').length, notExecuted: results.filter(r => r.outcome === 'NotExecuted').length})

test('TRX invariant: every outcome, duplicate multiplicity, order and all three named directions', () => {
  const original = host.readHostTrx(fixture)
  assert.equal(compare(original).passed, true)
  for (const first of ['Failed', 'Passed', 'NotExecuted']) for (const second of ['Failed', 'Passed', 'NotExecuted']) {
    for (const reverse of [false, true]) {
      const results = original.results.map(r => ({...r}))
      const twins = results.filter(r => r.testName === duplicate)
      twins[0].outcome = first; twins[1].outcome = second
      if (reverse) results.reverse()
      const result = compare({...original, results, counts: countsFor(results)})
      const passed = ![first, second].includes('Passed') && [first, second].includes('Failed')
      assert.equal(result.passed, passed, `${first}/${second}/${reverse}`)
      assert.deepEqual(result.burnDown, [first, second].includes('Passed') ? [duplicate] : [])
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
  assert.deepEqual(trx.results, [
    {testName: 'TRX fixture: passed', outcome: 'Passed'},
    {testName: duplicate, outcome: 'Failed'}, {testName: duplicate, outcome: 'Failed'},
    {testName: 'TRX fixture: skipped', outcome: 'NotExecuted'},
  ])
  const dir = mkdtempSync(path.join(tmpdir(), 'host-trx-reader-'))
  try {
    const file = path.join(dir, 'entities.trx')
    writeFileSync(file, readFileSync(fixture, 'utf8').replaceAll('&quot;name&quot;', '&#34;name&#x22; &apos; &amp;lt;'))
    assert.equal(host.readHostTrx(file).results[1].testName, 'TRX fixture: duplicate & <quoted> "name" \' &lt;')
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
  const source = readFileSync(path.join(root, 'eng/run-exact-clone.mjs'), 'utf8')
  const hostBlock = source.slice(source.indexOf('  const hostResultsDirectory ='), source.indexOf("  run('analyzer-canary'"))
  assert.ok(hostBlock.length > 0, 'host results setup must exist')
  const dir = mkdtempSync(path.join(tmpdir(), 'host-trx-runner-'))
  try {
    let invocation
    new Function('path', 'clone', 'run', hostBlock)(path, dir, (...args) => { invocation = args; return {} })
    assert.equal(invocation[0], 'dotnet-host-tests')
    assert.deepEqual(invocation[2], ['test', 'apps/local-node-host/tests/tests.csproj', '-c', 'Release', '--nologo', '--no-build', '-nodeReuse:false', '-maxcpucount:6',
      '--logger', 'trx;LogFileName=host-tests.trx', '--results-directory', path.join(dir, 'TestResults', 'host')])
    assert.doesNotMatch(hostBlock, /console;verbosity|hostBaseline/)
    assert.ok(/hostBaseline\.comparison === 'named' \? hostTrx\.counts : countsOf\(hostTests\.fullOutput\)/.test(source), 'named counts must come from TRX; Windows counts from console')
    assert.match(source, /hostTrx\.results\.filter\(row => row\.outcome === 'Failed'\)\.map\(row => row\.testName\)/)
    const compareBlock = source.slice(source.indexOf('  const hostComparison ='), source.indexOf("  steps.push({\n    id: 'host-baseline-match'"))
    assert.ok(compareBlock.includes('rawOutput'), 'comparison must persist raw output')
    const rawOutput = `${String.fromCharCode(27)}[31mraw host output\r\nTest Run Failed.\r\n`
    for (const reason of ['TRX missing', 'TRX incomplete', 'unlisted failure', 'burn-down', 'missing result', 'duplicate row']) {
      const hostComparison = {passed: false, problems: [reason], tail: reason}
      const printed = []
      const actual = new Function('compareHostBaseline', 'hostBaseline', 'hostCounts', 'adjustedFailed', 'newFailures',
        'hostTrx', 'hostTests', 'hostResultsDirectory', 'path', 'mkdirSync', 'writeFileSync', 'console',
        'let retainScratch = false;\n' + compareBlock + '\nreturn {hostComparison, retainScratch}')(
        () => hostComparison, baseline, null, null, [], {}, {rawOutput, fullOutput: 'stripped'},
        path.join(dir, 'TestResults', 'host'), path,
        mkdirSync, writeFileSync, {log: line => printed.push(line)})
      assert.equal(actual.retainScratch, true)
      const outputFile = path.join(dir, 'TestResults', 'host', 'host-tests-output.txt')
      assert.equal(readFileSync(outputFile, 'utf8'), rawOutput)
      assert.ok(printed.some(line => line.includes(reason) && line.includes(outputFile)))
      assert.ok(actual.hostComparison.tail.includes(outputFile))
    }
    assert.match(source, /if \(!retainScratch\) rmSync\(scratch, \{recursive: true, force: true\}\)/)
    assert.match(source, /\{fullOutput, rawOutput, \.\.\.rest\}/)
  } finally { rmSync(dir, {recursive: true, force: true}) }
})
