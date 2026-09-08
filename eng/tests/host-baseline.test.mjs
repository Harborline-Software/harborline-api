import {test} from 'node:test'
import './host-trx.test.mjs'
import assert from 'node:assert/strict'
import {readFileSync, mkdtempSync, mkdirSync, copyFileSync, rmSync} from 'node:fs'
import {spawnSync} from 'node:child_process'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {baselineArgument, compareHostBaseline, resultNamesIn, hostBaselineFor, WINDOWS_BASELINE, MACOS_BASELINE, UBUNTU_BASELINE} from '../host-baseline.mjs'

const root = path.resolve(import.meta.dirname, '../..')
const baseline = {comparison: 'named', permittedFailures: [{test: 'Listed test'}]}
function trxOf(output, counts) {
  const results = ['Failed', 'Passed', 'Skipped'].flatMap(outcome => resultNamesIn(output, outcome).map(testName =>
    ({testName, outcome: outcome === 'Skipped' ? 'NotExecuted' : outcome})))
  return {results, counts: counts && {passed: results.filter(r => r.outcome === 'Passed').length, notExecuted: 0, ...counts}, problems: []}
}
function compare(output, counts = {total: 1, failed: 1}) {
  const newFailures = resultNamesIn(output, 'Failed').filter(name => name !== 'Listed test')
  return compareHostBaseline({baseline, counts, adjustedFailed: counts?.failed, newFailures, trx: trxOf(output, counts)})
}

test('named: expected failure green regardless of historical total', () => {
  assert.equal(compare('  Failed Listed test [< 1 ms]\r\n').passed, true)
})
test('named: listed pass is red burn-down requiring row removal', () => {
  const result = compare('  Passed Listed test [1 ms]\n', {total: 1, failed: 0})
  assert.equal(result.passed, false)
  assert.deepEqual(result.burnDown, ['Listed test'])
  assert.match(result.note, /remove every burn-down row/)
})
test('named: unlisted failure is red even at the same failure count', () => {
  const result = compare('  Failed Regression [1 ms]\n  Passed Listed test [2 ms]\n')
  assert.equal(result.passed, false)
  assert.equal(compare('  Failed Listed test [1 ms]\n  Failed Regression [1 ms]\n', {total: 2, failed: 2}).passed, false)
})
test('named: missing, skipped, miscounted or unparsed results cannot pass', () => {
  for (const output of ['', '  Skipped Listed test [1 ms]\n', '  Failed Listed test [1 ms]\n  Failed Listed test [2 ms]\n']) {
    assert.equal(compare(output).passed, false)
  }
  assert.equal(compare('  Failed Listed test [1 ms]\n', null).passed, false)
})

const mac = JSON.parse(readFileSync(path.join(root, MACOS_BASELINE)))
const macNames = mac.permittedFailures.map(row => row.test)
const macOutput = macNames.map(name => `  Failed ${name} [12 ms]\n`).join('')
const macInput = {baseline: mac, counts: {total: 15, failed: 15}, adjustedFailed: 15, newFailures: [], trx: trxOf(macOutput, {total: 15, failed: 15})}

test('named: macOS duplicate permitted cases count individually', () => {
  const output = macOutput.replace('[12 ms]', '[1 s]') + `  Failed ${macNames[6]} [< 1 ms]\n`
  const result = compareHostBaseline({...macInput, trx: trxOf(output, {total: 16, failed: 16}), adjustedFailed: 16})
  assert.deepEqual(result.burnDown, [])
  assert.deepEqual(result.missing, [])
  assert.equal(result.passed, true)
})

test('result parser preserves real display names across outcomes and duration formats', () => {
  for (const ending of ['\n', '\r\n']) for (const outcome of ['Passed', 'Failed', 'Skipped']) {
    const lines = ['12 ms', '1 s', '< 1 ms'].map((duration, index) => `  ${outcome} ${macNames[index + 6]} [${duration}]${ending}`)
    const output = `[xUnit.net 00:00:01.00] runner diagnostic${ending}` + lines.join('')
    assert.deepEqual(resultNamesIn(output, outcome), macNames.slice(6, 9))
    for (const other of ['Passed', 'Failed', 'Skipped'].filter(value => value !== outcome)) {
      assert.deepEqual(resultNamesIn(output, other), [])
    }
  }
})

test('named: every false verdict supplies actionable console and evidence details', () => {
  const noLines = 'host baseline incomplete: TRX has 0 failed results but its counter is 15'
  const cases = [
    [{counts: {total: 3510, failed: 16}}, 'host baseline incomplete: TRX has 15 failed results but its counter is 16'],
    [{counts: null}, 'host baseline incomplete: TRX counters unavailable; inspect the host test output'],
    [{counts: {total: 0, failed: 15}}, 'host baseline incomplete: TRX counted no tests; check test discovery'],
    [{output: 'Failed: 15, Passed: 3476, Skipped: 19, Total: 3510'}, noLines],
    [{baseline: {...mac, permittedFailures: [...mac.permittedFailures, mac.permittedFailures[6]]}},
      `host baseline duplicate permitted row: remove duplicate row: ${macNames[6]}`],
    [{output: macOutput + '  Failed Regression [1 s]\n', counts: {total: 16, failed: 16}, newFailures: ['Regression']},
      'host baseline unlisted failure: investigate: Regression'],
    [{output: macOutput.replace(`Failed ${macNames[6]}`, `Passed ${macNames[6]}`), counts: {total: 15, failed: 14}},
      `host baseline burn-down: remove row: ${macNames[6]}`],
    [{output: macOutput.replace(`Failed ${macNames[6]}`, `Skipped ${macNames[6]}`), counts: {total: 15, failed: 14}},
      `host baseline missing result: ${macNames[6]}`],
    [{baseline: {...mac, permittedFailures: []}, counts: {total: 1, failed: 0}, output: ''}, 'host baseline incomplete: TRX has 0 total results but its counter is 1'],
  ]
  const runner = readFileSync(path.join(root, 'eng/run-exact-clone.mjs'), 'utf8')
  const failureBlock = runner.slice(runner.indexOf("if (report.status === 'FAIL') {"), runner.lastIndexOf('process.exit('))
  for (const [overrides, reason] of cases) {
    const counts = Object.hasOwn(overrides, 'counts') ? overrides.counts : macInput.counts
    const result = compareHostBaseline({...macInput, ...overrides, trx: trxOf(overrides.output ?? macOutput, counts)})
    assert.equal(result.passed, false, reason)
    assert.ok(result.problems?.includes(reason), `missing diagnostic: ${reason}`)
    assert.equal(result.tail, result.problems.join('\n'))
    let printed = ''
    new Function('report', 'persisted', 'process', failureBlock)(
      {status: 'FAIL'}, {steps: [{id: 'host-baseline-match', ...result}]},
      {stdout: {write: text => { printed += text }}})
    assert.ok(printed.startsWith('host-baseline-match:\n') && printed.includes(`\n  ${reason}\n`), printed)
  }
  // This is the gate route: reasons print immediately and survive in the final FAIL block.
  assert.ok(/for \(const line of hostComparison\.problems \?\? \[\]\) console\.log\(line\)/.test(runner),
    'exact-clone must print comparison reasons')
  assert.match(runner, /id: 'host-baseline-match',\s*\.\.\.hostComparison/)
  assert.match(runner, /\(step\.tail \?\? ''\)\.split\('\\n'\)/)
})
test('Windows comparison preserves the original count and identity truth table', () => {
  const windows = JSON.parse(readFileSync(path.join(root, WINDOWS_BASELINE)))
  for (const total of [windows.totals.total, windows.totals.total + 1]) {
    for (const failed of [0, 1]) for (const newFailures of [[], ['Regression']]) {
      const counts = {total, failed}
      const expected = Boolean(counts) && counts.total === windows.totals.total && failed === windows.totals.failed && newFailures.length === 0
      assert.equal(compareHostBaseline({baseline: windows, counts, adjustedFailed: failed, newFailures}).passed, expected)
    }
  }
})
test('all fifteen macOS identities are owned, distinct, and compared exactly', () => {
  const mac = JSON.parse(readFileSync(path.join(root, MACOS_BASELINE)))
  const rows = mac.permittedFailures
  assert.equal(rows.length, 15)
  assert.equal(new Set(rows.map(row => row.test)).size, 15)
  for (const row of rows) {
    assert.equal(row.owner, '302')
    assert.ok(['behavioural', 'environmental'].includes(row.class))
  }
  const output = rows.map(row => `  Failed ${row.test} [1 ms]\n`).join('')
  const input = {baseline: mac, counts: {total: 15, failed: 15}, adjustedFailed: 15, newFailures: [], trx: trxOf(output, {total: 15, failed: 15})}
  assert.equal(compareHostBaseline(input).passed, true)
  for (const row of rows) {
    const result = compareHostBaseline({...input, counts: {total: 15, failed: 14},
      trx: trxOf(output.replace(`Failed ${row.test}`, `Passed ${row.test}`), {total: 15, failed: 14})})
    assert.equal(result.passed, false)
    assert.deepEqual(result.burnDown, [row.test])
    const renamed = compareHostBaseline({...input, newFailures: [row.test + ' renamed'],
      trx: trxOf(output.replace(row.test, row.test + ' renamed'), {total: 15, failed: 15})})
    assert.equal(renamed.passed, false)
    assert.deepEqual(renamed.missing, [row.test])
  }
})
test('all thirteen Ubuntu identities are owned, distinct, and compared exactly', () => {
  const ubuntu = JSON.parse(readFileSync(path.join(root, UBUNTU_BASELINE)))
  const rows = ubuntu.permittedFailures
  assert.equal(rows.length, 13)
  assert.equal(new Set(rows.map(row => row.test)).size, 13)
  for (const row of rows) {
    assert.equal(row.owner, 'the controller')
    assert.equal(row.class, 'environmental')
  }
  const output = rows.map(row => `  Failed ${row.test} [1 ms]\n`).join('')
  assert.equal(compareHostBaseline({baseline: ubuntu, counts: {total: 13, failed: 13}, adjustedFailed: 13,
    newFailures: [], trx: trxOf(output, {total: 13, failed: 13})}).passed, true)
})
test('OS selection and the actual gate and landing routes carry the baseline', () => {
  assert.equal(hostBaselineFor('darwin'), MACOS_BASELINE)
  assert.equal(hostBaselineFor('linux'), UBUNTU_BASELINE)
  for (const platform of ['win32', 'freebsd']) assert.equal(hostBaselineFor(platform), WINDOWS_BASELINE)
  assert.throws(() => baselineArgument(['--host-baseline', 'eng/baselines/not-a-baseline.json']), /unknown host baseline/)
  const verify = readFileSync(path.join(root, 'eng/verify.sh'), 'utf8')
  assert.match(verify, /Darwin\) host_baseline=eng\/baselines\/host-test-baseline\.macos\.json/)
  assert.match(verify, /Linux\)\s+host_baseline=eng\/baselines\/host-test-baseline\.ubuntu\.json/)
  assert.match(verify, /run-exact-clone\.mjs --host-baseline "\$host_baseline"/)
  assert.match(verify, /--record "\$\{passed\[@\]\}" --host-baseline "\$host_baseline"/)
  const runner = readFileSync(path.join(root, 'eng/run-exact-clone.mjs'), 'utf8')
  assert.match(runner, /host: baselineArgument\(process\.argv\.slice\(2\)\)/)
  assert.match(runner, /compareHostBaseline\(/)
  assert.ok(/trx;LogFileName=host-tests\.trx/.test(runner), 'host step must request TRX results')
  const land = readFileSync(path.join(root, 'eng/land.sh'), 'utf8')
  // Ticket 333: the nested verify must be the LAST command of its subshell (bash execs it in-process, so the
  // gate lock's parent-pid re-entry admits it); the landing receipt check follows in its own subshell.
  assert.equal((land.match(/bash eng\/verify\.sh \) && \( cd "\$(?:land_dir|verify_dir)" && node eng\/verify-receipt\.mjs --landing \)/g) ?? []).length, 2)
  assert.equal((land.match(/bash eng\/verify\.sh &&/g) ?? []).length, 0)
})
test('receipt CLI records baseline, accepts macOS slices and refuses macOS landing', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'host-baseline-receipt-'))
  try {
    mkdirSync(path.join(dir, 'eng'))
    for (const file of ['verify-receipt.mjs', 'host-baseline.mjs']) copyFileSync(path.join(root, 'eng', file), path.join(dir, 'eng', file))
    const run = (command, args) => spawnSync(command, args, {cwd: dir, encoding: 'utf8'})
    for (const args of [['init', '-q'], ['add', '.'], ['-c', 'user.name=Baseline Test', '-c', 'user.email=baseline@example.invalid', 'commit', '--no-verify', '-qm', 'fixture']]) {
      const result = run('git', args)
      assert.equal(result.status, 0, result.stdout + result.stderr)
    }
    const source = readFileSync(path.join(dir, 'eng/verify-receipt.mjs'), 'utf8')
    const steps = [...source.match(/export const requiredStepIds = \[([^\]]+)\]/)[1].matchAll(/'([^']+)'/g)].map(m => m[1])
    const cli = args => run(process.execPath, ['eng/verify-receipt.mjs', ...args])
    for (const file of [MACOS_BASELINE, UBUNTU_BASELINE, WINDOWS_BASELINE]) {
      const recorded = cli(['--record', ...steps, '--host-baseline', file])
      assert.equal(recorded.status, 0, recorded.stdout + recorded.stderr)
      const receipt = JSON.parse(readFileSync(path.join(dir, '.git/harborline-api-verify-receipt.json')))
      assert.equal(receipt.hostBaseline, file)
      assert.deepEqual(receipt.steps, steps)
      assert.equal(cli(['--slice']).status, 0)
      for (const args of [[], ['--landing'], ['--landing', '--slice']]) {
        const checked = cli(args)
        assert.equal(checked.status, [MACOS_BASELINE, UBUNTU_BASELINE].includes(file) ? 1 : 0, checked.stdout + checked.stderr)
        if ([MACOS_BASELINE, UBUNTU_BASELINE].includes(file)) assert.match(checked.stderr, /host baseline receipts are slice-only; landings require the Windows host baseline/)
      }
    }
  } finally { rmSync(dir, {recursive: true, force: true}) }
})
