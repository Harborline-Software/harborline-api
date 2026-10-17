import assert from 'node:assert/strict'
import {execFileSync, spawnSync} from 'node:child_process'
import {existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import test from 'node:test'
import {persistStepEvidence} from '../exact-clone-evidence.mjs'
import {resolveCommand} from '../lib/resolve-command.mjs'
import {baselineArgument, capabilityBaselineFor} from '../host-baseline.mjs'

const source = readFileSync(new URL('../run-exact-clone.mjs', import.meta.url), 'utf8').replaceAll('\r\n', '\n')
function slice(from, to) {
  const start = source.indexOf(from)
  const end = source.indexOf(to, start)
  assert.ok(start >= 0 && end > start, `production source boundaries: ${from} / ${to}`)
  return source.slice(start, end)
}
const capture = slice('const head =', '// Baselines the clone must reproduce.')
const baselines = slice('// Baselines the clone must reproduce.', '\nconst scratch =')
const commands = slice('const run =', '\nlet report')
const binding = slice('  const cloneStep =', '  // Resolve once before restore/build.')
const assembly = slice('  const passed = steps.every', '\n// Record the report whatever its status.')

function fixture(t) {
  const root = mkdtempSync(path.join(tmpdir(), 'exact-clone-head-pin-'))
  t.after(() => rmSync(root, {recursive: true, force: true}))
  const apiRoot = path.join(root, 'source')
  mkdirSync(apiRoot)
  const git = (...args) => execFileSync('git', ['-C', apiRoot, ...args], {encoding: 'utf8'}).trim()
  git('init', '--quiet')
  writeFileSync(path.join(apiRoot, 'content.txt'), 'captured-A\n')
  writeFileSync(path.join(apiRoot, 'lane.mjs'),
    "import {readFileSync, writeFileSync} from 'node:fs'; writeFileSync(process.argv[2], readFileSync('content.txt', 'utf8'))\n")
  const hostPath = baselineArgument([])
  const capabilityPath = capabilityBaselineFor()
  mkdirSync(path.join(apiRoot, 'eng', 'baselines'), {recursive: true})
  writeFileSync(path.join(apiRoot, hostPath), '{"permittedFailures":[{"test":"only-A-host"}]}\n')
  writeFileSync(path.join(apiRoot, capabilityPath), '{"permittedFailures":[{"test":"only-A-capability"}]}\n')
  git('add', '.')
  const commit = message => git('-c', 'user.name=Head Pin Fixture', '-c', 'user.email=head-pin@example.invalid',
    'commit', '--quiet', '-m', message)
  commit('A')
  const head = new Function('execFileSync', 'apiRoot', capture + '\nreturn head')(execFileSync, apiRoot)
  return {root, apiRoot, git, head, move() {
    writeFileSync(path.join(apiRoot, 'content.txt'), 'moved-B\n')
    writeFileSync(path.join(apiRoot, hostPath), '{"permittedFailures":[{"test":"only-B-host"}]}\n')
    writeFileSync(path.join(apiRoot, capabilityPath), '{"permittedFailures":[{"test":"only-B-capability"}]}\n')
    git('add', '.')
    commit('B')
    return git('rev-parse', 'HEAD')
  }}
}

function runGate(f, refusal) {
  const scratch = path.join(f.root, 'scratch')
  mkdirSync(scratch)
  const clone = path.join(scratch, 'clone')
  const sentinel = path.join(f.root, 'lane-sentinel.txt')
  const steps = []
  const redact = text => text.replaceAll(scratch, '<exact-clone-root>')
  const observedSpawnSync = (id, command, args, options) => {
    if (refusal === 'checkout' && id === 'git-checkout-captured-head')
      return {status: 1, stdout: '', stderr: 'fixture checkout refused\n'}
    if (refusal === 'mismatch' && id === 'git-verify-captured-head')
      return {status: 0, stdout: '1111111111111111111111111111111111111111\n', stderr: ''}
    const result = spawnSync(command, args, options)
    return refusal === 'noisy-stderr' && id === 'git-verify-captured-head'
      ? {...result, stderr: (result.stderr ?? '') + 'fixture git trace\n'} : result
  }
  const policy = new Function('execFileSync', 'apiRoot', 'head', 'baselineArgument', 'capabilityBaselineFor', 'readFileSync', 'path',
    baselines + '\nreturn {BASELINES, baselineProvenance, hostBaseline, capabilityBaseline}')(
    execFileSync, f.apiRoot, f.head, baselineArgument, capabilityBaselineFor, readFileSync, path)
  const result = new Function('apiRoot', 'clone', 'scratch', 'head', 'steps', 'resolveCommand',
    'observedSpawnSync', 'progressFile', 'stripAnsi', 'redactEvidence', 'persistStepEvidence', 'rmSync',
    'persistInputShadow', 'baselineProvenance', 'BASELINES', 'sentinel', commands + `
      let report; let persisted; const retainScratch = false;
      const collectCoverage = false; const qualityEnabled = false;
      const packageRootResolution = {status: 'unavailable'};
      try {
        ${binding}
        run('fixture-lane', process.execPath, ['lane.mjs', sentinel], clone)
        ${assembly}
      return {report, persisted}
    `)(f.apiRoot, clone, scratch, f.head, steps, resolveCommand, observedSpawnSync,
    path.join(f.root, 'progress.jsonl'), text => text, redact, persistStepEvidence, rmSync, () => {},
    policy.baselineProvenance, policy.BASELINES, sentinel)
  assert.equal(existsSync(scratch), false, 'production finally removes scratch after evidence persistence')
  return {...result, sentinel, policy}
}

test('a source HEAD move still runs the lane at captured A when A remains available', t => {
  const f = fixture(t)
  const moved = f.move()
  assert.notEqual(moved, f.head)
  assert.equal(f.git('show', `${f.head}:content.txt`), 'captured-A')
  assert.equal(f.git('show', `${moved}:content.txt`), 'moved-B')

  const result = runGate(f)

  assert.equal(result.persisted.status, 'PASS')
  assert.equal(result.persisted.apiCommit, f.head)
  assert.equal(readFileSync(result.sentinel, 'utf8'), 'captured-A\n')
  const checkout = result.persisted.steps.find(step => step.id === 'git-checkout-captured-head')
  assert.equal(checkout.passed, true)
  const verified = result.persisted.steps.find(step => step.id === 'git-verify-captured-head')
  assert.equal(verified.passed, true)
  assert.equal(verified.expectedHead, f.head)
  assert.equal(verified.actualHead, f.head)
})

test('an unchanged source records passed checkout and clone HEAD equal to apiCommit', t => {
  const f = fixture(t)
  const result = runGate(f)

  assert.equal(result.persisted.status, 'PASS')
  assert.equal(result.persisted.apiCommit, f.head)
  assert.equal(readFileSync(result.sentinel, 'utf8'), 'captured-A\n')
  assert.equal(result.persisted.steps.find(step => step.id === 'git-checkout-captured-head').passed, true)
  const verified = result.persisted.steps.find(step => step.id === 'git-verify-captured-head')
  assert.equal(verified.passed, true)
  assert.equal(verified.expectedHead, result.persisted.apiCommit)
  assert.equal(verified.actualHead, result.persisted.apiCommit)
})

test('source movement changes both live baseline policies but the real loader retains captured A policy', t => {
  const f = fixture(t)
  f.move()
  assert.deepEqual(JSON.parse(readFileSync(path.join(f.apiRoot, baselineArgument([])), 'utf8')),
    {permittedFailures: [{test: 'only-B-host'}]})
  assert.deepEqual(JSON.parse(readFileSync(path.join(f.apiRoot, capabilityBaselineFor()), 'utf8')),
    {permittedFailures: [{test: 'only-B-capability'}]})

  const result = runGate(f)

  assert.equal(result.persisted.status, 'PASS')
  assert.equal(readFileSync(result.sentinel, 'utf8'), 'captured-A\n')
  assert.deepEqual(result.policy.hostBaseline, {permittedFailures: [{test: 'only-A-host'}]})
  assert.deepEqual(result.policy.capabilityBaseline, {permittedFailures: [{test: 'only-A-capability'}]})
  for (const [name, relative] of Object.entries(result.policy.BASELINES)) {
    const provenance = result.persisted.baselineProvenance[name]
    assert.equal(provenance.committedBlob, f.git('rev-parse', `${f.head}:${relative}`))
    assert.notEqual(provenance.committedBlob, f.git('rev-parse', `HEAD:${relative}`))
    assert.equal(provenance.workingBlob, provenance.committedBlob)
    assert.equal(provenance.matchesCommitted, true)
  }
})

test('correct rev-parse stdout passes with stderr noise retained in evidence', t => {
  const f = fixture(t)
  const result = runGate(f, 'noisy-stderr')

  assert.equal(result.persisted.status, 'PASS')
  assert.equal(readFileSync(result.sentinel, 'utf8'), 'captured-A\n')
  const verified = result.persisted.steps.find(step => step.id === 'git-verify-captured-head')
  assert.equal(verified.passed, true)
  assert.equal(verified.actualHead, f.head)
  assert.equal(verified.stdout.trim(), f.head)
  assert.ok(verified.tail.includes('fixture git trace'))
})

for (const refusal of ['checkout', 'mismatch']) {
  test(`${refusal} refuses before the lane and preserves captured identity and failed output`, t => {
    const f = fixture(t)
    const result = runGate(f, refusal)

    assert.equal(result.persisted.status, 'FAIL')
    assert.equal(result.persisted.apiCommit, f.head)
    assert.equal(existsSync(result.sentinel), false)
    assert.equal(result.persisted.steps.some(step => step.id === 'fixture-lane'), false)
    const failed = result.persisted.steps.find(step => step.id ===
      (refusal === 'checkout' ? 'git-checkout-captured-head' : 'git-verify-captured-head'))
    assert.equal(failed.passed, false)
    assert.equal(failed.expectedHead, f.head)
    assert.equal(readFileSync(path.join(f.apiRoot, failed.outputFile), 'utf8'),
      refusal === 'checkout' ? 'fixture checkout refused\n' : '1111111111111111111111111111111111111111\n')
    if (refusal === 'mismatch') {
      assert.equal(failed.actualHead, '1111111111111111111111111111111111111111')
      assert.ok(failed.tail.includes(`expected HEAD: ${f.head}`))
      assert.ok(failed.tail.includes('actual HEAD: 1111111111111111111111111111111111111111'))
    }
    assert.equal(result.persisted.steps.at(-1).id, 'report-assembly')
    assert.equal(result.persisted.steps.at(-1).passed, false)
  })
}
