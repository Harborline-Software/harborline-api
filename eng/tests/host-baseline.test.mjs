import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync, mkdtempSync, mkdirSync, copyFileSync, rmSync} from 'node:fs'
import {spawnSync} from 'node:child_process'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {compareHostBaseline, resultNamesIn, hostBaselineFor, WINDOWS_BASELINE, MACOS_BASELINE} from '../host-baseline.mjs'

const root = path.resolve(import.meta.dirname, '../..')
const baseline = {comparison: 'named', permittedFailures: [{test: 'Listed test'}]}
function compare(output, counts = {total: 10, failed: 1}) {
  const newFailures = resultNamesIn(output, 'Failed').filter(name => name !== 'Listed test')
  return compareHostBaseline({baseline, counts, adjustedFailed: counts?.failed, newFailures, output})
}

test('named: expected failure green regardless of historical total', () => {
  assert.equal(compare('  Failed Listed test [< 1 ms]\r\n').passed, true)
})
test('named: listed pass is red burn-down requiring row removal', () => {
  const result = compare('  Passed Listed test [1 ms]\n', {total: 10, failed: 0})
  assert.equal(result.passed, false)
  assert.deepEqual(result.burnDown, ['Listed test'])
  assert.match(result.note, /remove every burn-down row/)
})
test('named: unlisted failure is red even at the same failure count', () => {
  const result = compare('  Failed Regression [1 ms]\n  Passed Listed test [2 ms]\n')
  assert.equal(result.passed, false)
  assert.equal(compare('  Failed Listed test [1 ms]\n  Failed Regression [1 ms]\n', {total: 10, failed: 2}).passed, false)
})
test('named: missing, skipped, duplicate or unparsed results cannot pass', () => {
  for (const output of ['', '  Skipped Listed test [1 ms]\n', '  Failed Listed test [1 ms]\n  Failed Listed test [2 ms]\n']) {
    assert.equal(compare(output).passed, false)
  }
  assert.equal(compare('  Failed Listed test [1 ms]\n', null).passed, false)
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
  const input = {baseline: mac, counts: {total: 3510, failed: 15}, adjustedFailed: 15, newFailures: [], output}
  assert.equal(compareHostBaseline(input).passed, true)
  for (const row of rows) {
    const result = compareHostBaseline({...input, counts: {total: 3510, failed: 14},
      output: output.replace(`Failed ${row.test}`, `Passed ${row.test}`)})
    assert.equal(result.passed, false)
    assert.deepEqual(result.burnDown, [row.test])
    const renamed = compareHostBaseline({...input, newFailures: [row.test + ' renamed'],
      output: output.replace(row.test, row.test + ' renamed')})
    assert.equal(renamed.passed, false)
    assert.deepEqual(renamed.missing, [row.test])
  }
})
test('OS selection and the actual gate and landing routes carry the baseline', () => {
  assert.equal(hostBaselineFor('darwin'), MACOS_BASELINE)
  for (const platform of ['win32', 'linux', 'freebsd']) assert.equal(hostBaselineFor(platform), WINDOWS_BASELINE)
  const verify = readFileSync(path.join(root, 'eng/verify.sh'), 'utf8')
  assert.match(verify, /Darwin\) host_baseline=eng\/baselines\/host-test-baseline\.macos\.json/)
  assert.match(verify, /run-exact-clone\.mjs --host-baseline "\$host_baseline"/)
  assert.match(verify, /--record "\$\{passed\[@\]\}" --host-baseline "\$host_baseline"/)
  const runner = readFileSync(path.join(root, 'eng/run-exact-clone.mjs'), 'utf8')
  assert.match(runner, /host: baselineArgument\(process\.argv\.slice\(2\)\)/)
  assert.match(runner, /compareHostBaseline\(/)
  assert.match(runner, /console;verbosity=normal/)
  const land = readFileSync(path.join(root, 'eng/land.sh'), 'utf8')
  assert.equal((land.match(/bash eng\/verify\.sh && node eng\/verify-receipt\.mjs --landing/g) ?? []).length, 2)
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
    for (const file of [MACOS_BASELINE, WINDOWS_BASELINE]) {
      const recorded = cli(['--record', ...steps, '--host-baseline', file])
      assert.equal(recorded.status, 0, recorded.stdout + recorded.stderr)
      const receipt = JSON.parse(readFileSync(path.join(dir, '.git/harborline-api-verify-receipt.json')))
      assert.equal(receipt.hostBaseline, file)
      assert.deepEqual(receipt.steps, steps)
      assert.equal(cli(['--slice']).status, 0)
      for (const args of [[], ['--landing'], ['--landing', '--slice']]) {
        const checked = cli(args)
        assert.equal(checked.status, file === MACOS_BASELINE ? 1 : 0, checked.stdout + checked.stderr)
        if (file === MACOS_BASELINE) assert.match(checked.stderr, /macOS host baseline receipts are slice-only; landings require the Windows host baseline \(ticket 324\)/)
      }
    }
  } finally { rmSync(dir, {recursive: true, force: true}) }
})
