import assert from 'node:assert/strict'
import {createHash} from 'node:crypto'
import {mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import test from 'node:test'
import {classifyFocusedModes, requireRunnableSelection, validateFocusedModeEvidence, parseChangedFiles, executeFocusedModes} from '../focused-mode-policy.mjs'
import {runVerificationPreflight} from '../verify-preflight.mjs'

const requiredNames = [
  'ck-10 fence: no production code runs its own loop over the ADR 0038 stage order',
  'ck-10 fence: only the executor calls a KernelWrite stage',
  'ck-10 fence: every admitted-write commit is a KernelWrite commit stage or a reviewed not-yet-moved path',
  'ck-10 fence: the record writer commits only from its KernelWrite commit stages, EF saves included',
  'ck-10 fence: a planted bypass outside the writer is caught by every check',
]
const hash = value => `sha256:${createHash('sha256').update(JSON.stringify(value)).digest('hex')}`
const scope = hash({id: 'compiled-write-fences-v1', project: 'apps/local-node-host/tests/tests.csproj',
  filter: 'FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.ArchTests.WritePipelineExecutorFenceTests', requiredTests: requiredNames})
const context = {commit: 'a'.repeat(40), tree: 'b'.repeat(40), host: 'test-host/win32/x64',
  sdk: '11.0.100', dependencies: `sha256:${'c'.repeat(64)}`, run: 'run-case'}
const trx = () => ({counts: {total: 5, passed: 5, failed: 0, notExecuted: 0},
  results: requiredNames.map(name => ({testName: name, rosterId: name, outcome: 'Passed'}))})
const selected = () => classifyFocusedModes([{status: 'M', path: 'packages/foundation/Compiler.cs'}])
const evidence = plan => ['coverage-off', 'coverage-on'].map(mode => ({schemaVersion: 1, mode, ...context,
  planDigest: plan.planDigest, scopeDigest: scope, executed: true, exitCode: 0, ...trx(),
  coverageDigest: mode === 'coverage-on' ? `sha256:${'d'.repeat(64)}` : null}))

for (const [file, kind] of [
  ['apps/local-node-host/Validation/Check.cs', 'validation'],
  ['packages/foundation/Reflection/Inspector.cs', 'reflection'],
  ['packages/foundation/Normal.cs', 'binary'], ['lib/component.dll', 'binary'],
  ['Directory.Build.targets', 'build-artifact'], ['eng/run-exact-clone.mjs', 'build-artifact'],
  ['.github/workflows/verify.yml', 'build-artifact'], ['artifacts/build.json', 'build-artifact'],
  ['global.json', 'toolchain'], ['NuGet.config', 'toolchain'],
  ['eng/platform-pin.json', 'toolchain'], ['eng/coverage.runsettings', 'coverage'],
]) test(`${kind} changes require independently executed OFF and ON`, () => {
  const plan = classifyFocusedModes([{status: 'M', path: file}])
  assert.deepEqual(plan.requiredModes, ['coverage-off', 'coverage-on'])
  assert.equal(plan.changes[0].kind, kind)
  assert.equal(plan.selection, 'focused')
  assert.equal(requireRunnableSelection(plan).project, 'apps/local-node-host/tests/tests.csproj')
})

test('an explicit documentation-only delta omits only the additional early check', () => {
  const plan = classifyFocusedModes([{status: 'M', path: 'docs/guide.md'}])
  assert.deepEqual(plan.requiredModes, [])
  assert.equal(requireRunnableSelection(plan), null)
  assert.equal(validateFocusedModeEvidence(plan, null, []).status, 'not-required')
})
test('rename classification includes the old runtime path, not just the new documentation path', () => {
  const plan = classifyFocusedModes(parseChangedFiles('R100\0packages/Old.cs\0docs/old.md\0'))
  assert.deepEqual(plan.requiredModes, ['coverage-off', 'coverage-on'])
  assert.equal(plan.selection, 'focused')
})
for (const change of [
  {status: 'M', path: 'new-format.blob'}, {status: 'T', path: 'README.md'},
  {status: 'M', path: '../docs/guide.md'}, {status: 'M', path: '/docs/guide.md'},
  {status: 'M', path: 'C:\\docs\\guide.md'}, {status: 'R100', path: 'README.md'},
  {status: 'R999', path: 'README.md', oldPath: 'docs/old.md'},
]) test(`unsupported input cannot become a skip: ${JSON.stringify(change)}`, () => {
  const plan = classifyFocusedModes([change])
  assert.deepEqual(plan.requiredModes, ['coverage-off', 'coverage-on'])
  assert.equal(plan.selection, 'unsupported')
  assert.throws(() => requireRunnableSelection(plan), /unsupported-selection/)
})
test('missing diff remains conservative and explicitly unsupported', () => {
  const plan = classifyFocusedModes(null, {diffAvailable: false})
  assert.deepEqual(plan.requiredModes, ['coverage-off', 'coverage-on'])
  assert.throws(() => requireRunnableSelection(plan), /unsupported-selection/)
})
test('classification is deterministic under change ordering', () => {
  const changes = [{status: 'M', path: 'global.json'}, {status: 'M', path: 'docs/a.md'}]
  assert.deepEqual(classifyFocusedModes(changes), classifyFocusedModes(changes.toReversed()))
})
test('altering a plan cannot silently downgrade required modes', () => {
  const plan = selected(); plan.requiredModes = []
  assert.throws(() => requireRunnableSelection(plan), /invalid-plan/)
})
test('a recomputed digest cannot disguise a runtime path as documentation', () => {
  const plan = classifyFocusedModes([{status: 'M', path: 'docs/a.md'}]); plan.changes[0].path = 'packages/New.cs'
  const body = {...plan}; delete body.planDigest; plan.planDigest = hash(body)
  assert.throws(() => requireRunnableSelection(plan), /invalid-plan/)
})
test('complete matching ON/OFF results pass the bounded receipt contract', () => {
  const plan = selected()
  assert.equal(validateFocusedModeEvidence(plan, context, evidence(plan)).status, 'passed')
})
for (const [label, alter, error] of [
  ['one mode absent', rows => rows.pop(), 'missing-mode'],
  ['duplicated mode', rows => { rows[1].mode = rows[0].mode }, 'missing-mode'],
  ['wrong candidate', rows => { rows[0].commit = 'e'.repeat(40) }, 'identity-mismatch'],
  ['wrong dependency inputs', rows => { rows[1].dependencies = `sha256:${'f'.repeat(64)}` }, 'identity-mismatch'],
  ['different SDK', rows => { rows[1].sdk = 'another' }, 'identity-mismatch'],
  ['different host', rows => { rows[1].host = 'elsewhere' }, 'identity-mismatch'],
  ['different attempt', rows => { rows[1].run = 'other-run' }, 'identity-mismatch'],
  ['wrong suite', rows => { rows[0].scopeDigest = `sha256:${'0'.repeat(64)}` }, 'wrong-scope'],
  ['wrong plan', rows => { rows[0].planDigest = `sha256:${'0'.repeat(64)}` }, 'wrong-scope'],
  ['skipped invocation', rows => { rows[0].executed = false }, 'nonpassing-evidence'],
  ['child failure', rows => { rows[0].exitCode = 1 }, 'nonpassing-evidence'],
  ['empty selection', rows => { rows[0].counts.total = 0 }, 'nonpassing-evidence'],
  ['skipped test', rows => { rows[0].counts.notExecuted = 1 }, 'nonpassing-evidence'],
  ['failure disguised as green', rows => { rows[0].results[0].outcome = 'Failed' }, 'nonpassing-evidence'],
  ['duplicate result', rows => { rows[0].results[1] = rows[0].results[0] }, 'nonpassing-evidence'],
  ['required test missing', rows => { rows[0].results[0] = {testName: 'unrelated', rosterId: 'unrelated', outcome: 'Passed'} }, 'missing-test'],
  ['collector produced no proof', rows => { rows[1].coverageDigest = null }, 'missing-coverage'],
  ['OFF produced coverage', rows => { rows[0].coverageDigest = `sha256:${'1'.repeat(64)}` }, 'unexpected-coverage'],
]) test(`receipt refuses ${label}`, () => {
  const plan = selected(), rows = evidence(plan); alter(rows)
  assert.throws(() => validateFocusedModeEvidence(plan, context, rows), new RegExp(error))
})
test('a different extra test inventory fails even when both modes passed all required cases', () => {
  const plan = selected(), rows = evidence(plan)
  for (const [i, row] of rows.entries()) {
    row.counts.total++; row.counts.passed++
    row.results.push({testName: `extra-${i}`, rosterId: `extra-${i}`, outcome: 'Passed'})
  }
  assert.throws(() => validateFocusedModeEvidence(plan, context, rows), /inventory-mismatch/)
})

function executorFixture({delta = 'M\0packages/foundation/Compiler.cs\0', childFailure = false, changeInputs = false} = {}) {
  const root = mkdtempSync(path.join(tmpdir(), 'focused-mode-contract-')), calls = []; let sourceChecks = 0
  const git = (...args) => {
    if (args[0] === 'status') return changeInputs && sourceChecks++ > 0 ? ' M source.cs' : ''
    if (args[0] === 'merge-base') return 'e'.repeat(40)
    if (args[0] === 'diff') return delta
    return args[1] === 'HEAD' ? context.commit : context.tree
  }
  const command = (executable, args, options) => {
    calls.push({executable, args, options})
    const output = args[args.indexOf('--results-directory') + 1]
    if (options.env.HARBORLINE_GATE_COVERAGE === '1') writeFileSync(path.join(output, 'coverage.cobertura.xml'),
      '<coverage line-rate="1"><sources><source>src</source></sources><packages></packages></coverage>')
    return {status: childFailure && calls.length === 1 ? 1 : 0}
  }
  return {root, calls, options: {apiRoot: root, git, command, sdk: () => context.sdk,
    dependencies: () => context.dependencies, readTrx: trx}, cleanup: () => rmSync(root, {recursive: true, force: true})}
}
test('the real executor hook rebuilds isolated OFF/ON outputs before validating receipts', () => {
  const fixture = executorFixture()
  try {
    assert.equal(executeFocusedModes(fixture.options).status, 'passed')
    assert.equal(fixture.calls.length, 2)
    assert.deepEqual(fixture.calls.map(call => call.options.env.HARBORLINE_GATE_COVERAGE), ['0', '1'])
    assert.equal(fixture.calls[0].options.shell, false)
    assert.equal(fixture.calls.some(call => call.args.includes('--no-build')), false)
    const builds = fixture.calls.map(call => call.args[call.args.indexOf('--artifacts-path') + 1])
    assert.notEqual(builds[0], builds[1])
    assert.equal(fixture.calls[0].args.some(arg => arg.startsWith('--collect')), false)
    assert.equal(fixture.calls[1].args.includes('--collect:XPlat Code Coverage'), true)
    const output = fixture.calls[0].args[fixture.calls[0].args.indexOf('--results-directory') + 1]
    assert.match(readFileSync(path.join(output, 'coverage-off.runsettings'), 'utf8'), /enabled="false"/)
  } finally { fixture.cleanup() }
})
test('the executor collects both modes and fails after execution when OFF fails', () => {
  const fixture = executorFixture({childFailure: true})
  try {
    assert.throws(() => executeFocusedModes(fixture.options), /nonpassing-evidence/)
    assert.equal(fixture.calls.length, 2)
  } finally { fixture.cleanup() }
})
test('unsupported selection cannot dispatch an arbitrary focused suite', () => {
  const fixture = executorFixture({delta: 'M\0unknown.blob\0'})
  try { assert.throws(() => executeFocusedModes(fixture.options), /unsupported-selection/); assert.equal(fixture.calls.length, 0) }
  finally { fixture.cleanup() }
})
test('source changes during execution invalidate completed mode evidence', () => {
  const fixture = executorFixture({changeInputs: true})
  try { assert.throws(() => executeFocusedModes(fixture.options), /inputs-changed/) }
  finally { fixture.cleanup() }
})
for (const lane of ['host', 'all']) test(`existing ${lane} CLI path owns executable early parity`, () => {
  let calls = 0
  const result = runVerificationPreflight({prerequisites: () => ({lane}), focusedModes: () => { calls++; return {status: 'passed'} }})
  assert.equal(calls, 1); assert.equal(result.focused.status, 'passed')
})
test('shared preflight cannot claim host-mode execution', () => {
  const result = runVerificationPreflight({prerequisites: () => ({lane: 'shared'}), focusedModes: () => assert.fail('host work on shared')})
  assert.equal(result.focused.status, 'host-lane-owned')
})
test('a completion failure propagates through the existing preflight CLI wrapper', () => {
  assert.throws(() => runVerificationPreflight({prerequisites: () => ({lane: 'host'}), focusedModes: () => { throw new Error('mode failed') }}), /mode failed/)
})
