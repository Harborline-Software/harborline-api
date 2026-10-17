import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import path from 'node:path'
import test from 'node:test'

const source = readFileSync(new URL('../run-exact-clone.mjs', import.meta.url), 'utf8').replaceAll('\r\n', '\n')
function slice(from, to) {
  const start = source.indexOf(from)
  const end = source.indexOf(to, start)
  assert.ok(start >= 0 && end > start, `production source boundaries: ${from} / ${to}`)
  return source.slice(start, end)
}
const commands = slice('const run =', '\nlet report')
const install = slice("  run('capability-install',", '\n  const hostResultsDirectory')
const registration = slice("  run('validation-reuse-contracts',", '\n  const feedStep')
const fixtureClone = path.resolve('exact-clone-frozen-install-fixture')

function runInstall({status = 0, failureAt, sentinel} = {}) {
  const resolvedCalls = []
  const spawnCalls = []
  const steps = []
  const resolveCommand = (command, args) => {
    resolvedCalls.push({command, args: [...args]})
    if (failureAt === 'resolve') throw sentinel
    return {executable: 'fixture-pnpm', args: ['fixture-prefix', ...args]}
  }
  const observedSpawnSync = (id, command, args, options) => {
    spawnCalls.push({id, command, args: [...args], cwd: options.cwd})
    if (failureAt === 'spawn') throw sentinel
    return {status, stdout: '', stderr: status === 0 ? '' : 'fixture lock mismatch'}
  }
  new Function('resolveCommand', 'observedSpawnSync', 'progressFile', 'stripAnsi', 'redactEvidence', 'steps', 'path', 'clone',
    commands + '\n' + install)(resolveCommand, observedSpawnSync, 'fixture-progress.jsonl', text => text, text => text,
    steps, path, fixtureClone)
  return {resolvedCalls, spawnCalls, steps}
}

test('capability install uses pnpm with only frozen-lockfile arguments at the exact package cwd', () => {
  const result = runInstall()

  assert.deepEqual(result.resolvedCalls, [{command: 'pnpm', args: ['install', '--frozen-lockfile']}])
  assert.deepEqual(result.spawnCalls, [{
    id: 'capability-install', command: 'fixture-pnpm', args: ['fixture-prefix', 'install', '--frozen-lockfile'],
    cwd: path.join(fixtureClone, 'apps', 'capability-host'),
  }])
  assert.equal(result.steps.length, 1)
  assert.equal(result.steps[0].id, 'capability-install')
  assert.equal(result.steps[0].passed, true)
  assert.equal(result.steps[0].exitCode, 0)
  assert.equal(result.steps[0].verdictFrom, 'exit code')
})

test('an install exit of 17 remains an ordinary failed step with the original exit-code verdict', () => {
  const result = runInstall({status: 17})

  assert.equal(result.steps.length, 1)
  assert.equal(result.steps[0].id, 'capability-install')
  assert.equal(result.steps[0].passed, false)
  assert.equal(result.steps[0].exitCode, 17)
  assert.equal(result.steps[0].verdictFrom, 'exit code')
  assert.equal(result.steps[0].fullOutput, 'fixture lock mismatch')
})

for (const failureAt of ['resolve', 'spawn']) {
  test(`${failureAt} exception escapes the actual install span and run helper unchanged`, () => {
    const sentinel = new Error('fixture dependency failure')
    assert.throws(() => runInstall({failureAt, sentinel}), error => error === sentinel)
  })
}

test('the existing validation stage retains all six guards and runs the frozen install guard', () => {
  const calls = []
  new Function('run', 'process', 'clone', registration)(
    (id, command, args, cwd) => calls.push({id, command, args, cwd}), {execPath: 'fixture-node'}, fixtureClone)

  assert.deepEqual(calls, [{
    id: 'validation-reuse-contracts', command: 'fixture-node',
    args: ['--test',
      'eng/tests/validation-reuse.test.mjs', 'eng/tests/validation-inputs.test.mjs',
      'eng/tests/validation-github-shadow.test.mjs', 'eng/tests/validation-producer-policy.test.mjs',
      'eng/tests/validation-compiler-inputs.test.mjs', 'eng/tests/validation-consumer.test.mjs',
      'eng/tests/exact-clone-frozen-install.test.mjs'],
    cwd: fixtureClone,
  }])
})
