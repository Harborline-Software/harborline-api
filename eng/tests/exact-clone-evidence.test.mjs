// node --test eng/tests/exact-clone-evidence.test.mjs
// Enumerates evidenceTarget over {record} x {PASS, FAIL}: a FAIL without --record must never resolve into the
// tracked tree (round-1 blocker on ticket 247), --record must keep the committed path, and a PASS without
// --record writes nothing.
import test from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import {execFileSync, spawnSync} from 'node:child_process'
import {existsSync, mkdtempSync, readFileSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import {evidenceTarget, FAIL_EVIDENCE_RELATIVE, persistStepEvidence} from '../exact-clone-evidence.mjs'

const apiRoot = path.resolve(import.meta.dirname, '..', '..')
const evidencePath = path.join(apiRoot, 'docs/evidence/exact-clone.json')

test('--record keeps the committed evidence path for PASS and FAIL', () => {
  for (const status of ['PASS', 'FAIL']) {
    assert.equal(evidenceTarget({record: true, status, apiRoot, evidencePath}), evidencePath)
  }
})

test('FAIL without --record goes under .claude/gate-evidence, outside the tracked tree', () => {
  const target = evidenceTarget({record: false, status: 'FAIL', apiRoot, evidencePath})
  assert.equal(target, path.join(apiRoot, FAIL_EVIDENCE_RELATIVE))
  assert.notEqual(target, evidencePath)
  // git must ignore it: a red gate can never dirty the checkout it ran in
  const rc = (() => { try { execFileSync('git', ['check-ignore', '-q', FAIL_EVIDENCE_RELATIVE], {cwd: apiRoot, stdio: 'ignore'}); return 0 } catch (e) { return e.status } })()
  assert.equal(rc, 0, `${FAIL_EVIDENCE_RELATIVE} is not gitignored`)
})

test('PASS without --record writes nothing', () => {
  assert.equal(evidenceTarget({record: false, status: 'PASS', apiRoot, evidencePath}), null)
})

test('run-exact-clone.mjs uses evidenceTarget (no second writer path)', async () => {
  const {readFileSync} = await import('node:fs')
  const src = readFileSync(path.join(apiRoot, 'eng/run-exact-clone.mjs'), 'utf8')
  assert.match(src, /evidenceTarget\(\{record, status: report\.status, apiRoot, evidencePath\}\)/)
  assert.doesNotMatch(src, /exact-clone-fail\.json/)
})

const diagnosticHead = '0123456789abcdef0123456789abcdef01234567'

test('a planted failing process retains the first failure and every stdout/stderr line beyond the tail', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'exact-clone-diagnostic-'))
  try {
    const stdout = ['FIRST FAILED CASE', ...Array.from({length: 30}, (_, i) => `later line ${i}`)].join('\n') + '\n'
    const stderr = 'trap: unexpected EOF while looking for a matching parenthesis\n'
    const result = spawnSync(process.execPath, ['-e',
      `process.stdout.write(${JSON.stringify(stdout)}); process.stderr.write(${JSON.stringify(stderr)}); process.exit(7)`], {encoding: 'utf8'})
    assert.equal(result.status, 7)
    const output = result.stdout + result.stderr
    const original = {status: 'FAIL', apiCommit: diagnosticHead, steps: [{id: 'boundary-check',
      passed: false, exitCode: result.status, fullOutput: output, rawOutput: output,
      tail: output.trimEnd().split('\n').slice(-14).join('\n')}]}
    const persisted = persistStepEvidence({report: original, apiRoot: root, redactEvidence: text => text})
    const [step] = persisted.steps
    assert.equal(step.outputFile, path.join('.claude', 'gate-evidence',
      'exact-clone-0123456789abcdef0123456789abcdef01234567-boundary-check.log'))
    assert.equal(readFileSync(path.join(root, step.outputFile), 'utf8'), stdout + stderr)
    assert.doesNotMatch(step.tail, /FIRST FAILED CASE/)
    assert.equal(step.passed, false)
    assert.equal(step.exitCode, 7)
    assert.equal(persisted.status, 'FAIL')
    assert.equal('fullOutput' in step, false)
    assert.equal('rawOutput' in step, false)
    assert.equal(original.steps[0].fullOutput, output)
  } finally { rmSync(root, {recursive: true, force: true}) }
})

test('failure output is redacted before writing and the report carries only a relative artifact path', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'exact-clone-diagnostic-'))
  try {
    const report = {status: 'FAIL', apiCommit: diagnosticHead, steps: [{id: 'boundary-check', passed: false,
      fullOutput: 'private-scratch/clone/eng/test.sh: first failed case\n'}]}
    const persisted = persistStepEvidence({report, apiRoot: root,
      redactEvidence: text => text.replaceAll('private-scratch/clone', '<exact-clone>')})
    const [step] = persisted.steps
    assert.equal(typeof step.outputFile, 'string')
    assert.equal(path.isAbsolute(step.outputFile), false)
    assert.equal(readFileSync(path.join(root, step.outputFile), 'utf8'), '<exact-clone>/eng/test.sh: first failed case\n')
    assert.doesNotMatch(JSON.stringify(persisted), /private-scratch/)
  } finally { rmSync(root, {recursive: true, force: true}) }
})

test('a successful process keeps the passing report unchanged and creates no diagnostic directory', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'exact-clone-diagnostic-'))
  try {
    const result = spawnSync(process.execPath, ['-e', 'process.stdout.write("passing fixture")'], {encoding: 'utf8'})
    assert.equal(result.status, 0)
    const report = {status: 'PASS', apiCommit: diagnosticHead, steps: [{id: 'boundary-check', passed: true,
      exitCode: 0, fullOutput: result.stdout, rawOutput: result.stdout, tail: 'passing fixture'}]}
    assert.deepEqual(persistStepEvidence({report, apiRoot: root,
      redactEvidence: () => { throw new Error('passing output must not be written') }}),
      {status: 'PASS', apiCommit: diagnosticHead, steps: [{id: 'boundary-check', passed: true,
        exitCode: 0, tail: 'passing fixture'}]})
    assert.equal(existsSync(path.join(root, '.claude')), false)
  } finally { rmSync(root, {recursive: true, force: true}) }
})

test('baseline-permitted nonzero exits and synthetic failures have no new output log', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'exact-clone-diagnostic-'))
  try {
    const report = {status: 'FAIL', apiCommit: diagnosticHead, steps: [
      {id: 'dotnet-host-tests', passed: true, exitCode: 1, fullOutput: 'known baseline failures'},
      {id: 'host-baseline-match', passed: true, tail: 'permitted baseline'},
      {id: 'unrelated-synthetic-failure', passed: false},
    ]}
    const persisted = persistStepEvidence({report, apiRoot: root,
      redactEvidence: () => { throw new Error('there is no failed command output to write') }})
    assert.deepEqual(persisted.steps, [
      {id: 'dotnet-host-tests', passed: true, exitCode: 1},
      {id: 'host-baseline-match', passed: true, tail: 'permitted baseline'},
      {id: 'unrelated-synthetic-failure', passed: false},
    ])
    assert.equal(existsSync(path.join(root, '.claude')), false)
  } finally { rmSync(root, {recursive: true, force: true}) }
})


test('an authoritative baseline failure retains the originating test output', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'exact-clone-diagnostic-'))
  try {
    const fixture = 'UNEXPECTED FAILED TEST\n' + 'later passing test\n'.repeat(30)
    for (const id of ['host-baseline-match', 'capability-baseline-match']) {
      const report = {status: 'FAIL', apiCommit: diagnosticHead, steps: [
        {id: 'informational-test-command', passed: true, exitCode: 1, fullOutput: fixture},
        {id, passed: false, fullOutput: fixture},
      ]}
      const persisted = persistStepEvidence({report, apiRoot: root, redactEvidence: text => text})
      assert.equal('outputFile' in persisted.steps[0], false)
      assert.equal(readFileSync(path.join(root, persisted.steps[1].outputFile), 'utf8'), fixture)
      assert.equal(persisted.steps[1].passed, false)
    }
    const runner = readFileSync(path.join(apiRoot, 'eng/run-exact-clone.mjs'), 'utf8')
    assert.match(runner, /fullOutput: hostComparison\.passed === false \? hostTests\.fullOutput : undefined/)
    assert.match(runner, /fullOutput: capabilityComparison\.passed === false \? capabilityTests\.fullOutput : undefined/)
  } finally { rmSync(root, {recursive: true, force: true}) }
})


test('runner reporting exceptions retain redacted failed command output before scratch cleanup', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'exact-clone-aborted-'))
  try {
    const source = readFileSync(path.join(apiRoot, 'eng/run-exact-clone.mjs'), 'utf8').replaceAll('\r\n', '\n')
    const handler = source.slice(source.indexOf('} catch (error) {\n  steps.push'),
      source.indexOf('\n// Record the report whatever its status.'))
    assert.ok(handler.length > 0, 'runner exception handler must exist')
    const output = 'FIRST FAILED COMMAND\n' + 'later diagnostic\n'.repeat(30) + 'private-scratch/clone/test\n'
    let cleaned = false
    const result = new Function('steps', 'head', 'baselineProvenance', 'apiRoot', 'scratch',
      'redactEvidence', 'persistStepEvidence', 'rmSync',
      'let report; let persisted; const retainScratch = false; try { throw new Error("private-scratch/clone/missing.trx")\n' +
      handler + '\nreturn {report, persisted}')(
      [{id: 'dotnet-build', passed: false, exitCode: 1, fullOutput: output, rawOutput: 'RAW PRIVATE OUTPUT'}],
      'fixture-head', {}, root, 'private-scratch', text => text.replaceAll('private-scratch/clone', '<exact-clone>'),
      persistStepEvidence, () => {
        assert.equal(readFileSync(path.join(root, '.claude', 'gate-evidence',
          'exact-clone-fixture-head-dotnet-build.log'), 'utf8'),
          'FIRST FAILED COMMAND\n' + 'later diagnostic\n'.repeat(30) + '<exact-clone>/test\n')
        cleaned = true
      })
    assert.equal(cleaned, true)
    assert.equal(result.report.status, 'FAIL')
    assert.equal(result.persisted.steps[0].exitCode, 1)
    assert.equal(result.persisted.steps[1].id, 'report-assembly')
    assert.match(result.persisted.steps[1].tail, /<exact-clone>\/missing\.trx/)
    assert.doesNotMatch(JSON.stringify(result.persisted), /private-scratch|RAW PRIVATE OUTPUT|FIRST FAILED COMMAND/)
  } finally { rmSync(root, {recursive: true, force: true}) }
})
