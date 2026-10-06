import assert from 'node:assert/strict'
import {createHash} from 'node:crypto'
import {execFileSync} from 'node:child_process'
import {mkdtempSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync} from 'node:fs'
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
  coverageDigest: mode === 'coverage-on' ? `sha256:${'d'.repeat(64)}` : null,
  coverage: mode === 'coverage-on' ? {validLines: 1, coveredLines: 0, paths: ['source.cs']} : null}))

for (const [file, kind] of [
  ['apps/local-node-host/Validation/Check.cs', 'validation'],
  ['packages/foundation/Reflection/Inspector.cs', 'reflection'],
  ['packages/foundation/Normal.cs', 'binary'], ['lib/component.dll', 'binary'],
  ['Directory.Build.targets', 'build-artifact'], ['eng/run-exact-clone.mjs', 'build-artifact'],
  ['.github/workflows/verify.yml', 'build-artifact'], ['artifacts/build.json', 'build-artifact'],
  ['global.json', 'toolchain'], ['NuGet.config', 'toolchain'],
  ['apps/capability-host/pnpm-lock.yaml', 'toolchain'],
  ['apps/capability-host/pnpm-workspace.yaml', 'toolchain'],
  ['packages/contracts/pnpm-lock.yaml', 'toolchain'],
  ['packages/contracts/pnpm-workspace.yaml', 'toolchain'],
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
test('both selector checks accept every zero-padded rename/copy score from 000 through 100', () => {
  for (const prefix of ['R', 'C']) for (let score = 0; score <= 100; score++) {
    const status = `${prefix}${String(score).padStart(3, '0')}`
    const plan = classifyFocusedModes(parseChangedFiles(`${status}\0packages/Old.cs\0docs/new.md\0`))
    assert.equal(plan.selection, 'focused', status)
    assert.deepEqual(plan.changes.map(row => row.path).sort(), ['docs/new.md', 'packages/Old.cs'])
    assert.deepEqual(plan.requiredModes, ['coverage-off', 'coverage-on'])
    assert.ok(requireRunnableSelection(plan), status)
  }
})
test('real Git edited-rename output preserves both paths and executes the supported selector', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'focused-mode-git-rename-'))
  const git = (...args) => execFileSync('git', ['-C', root, ...args], {encoding: 'utf8'})
  try {
    git('init', '--quiet')
    mkdirSync(path.join(root, 'packages')); mkdirSync(path.join(root, 'docs'))
    const lines = Array.from({length: 100}, (_, i) => `row ${String(i).padStart(3, '0')} unchanged source content\n`)
    writeFileSync(path.join(root, 'packages/Old.cs'), lines.join(''))
    git('add', '.')
    git('-c', 'user.name=ModePolicyFixture', '-c', 'user.email=modepolicy@example.invalid', 'commit', '--quiet', '-m', 'baseline')
    renameSync(path.join(root, 'packages/Old.cs'), path.join(root, 'docs/new.md'))
    lines[50] = 'edited row with distinct contents\n'
    writeFileSync(path.join(root, 'docs/new.md'), lines.join(''))
    git('add', '--all')
    const raw = git('diff', '--cached', '--name-status', '-z', '--find-renames')
    const changes = parseChangedFiles(raw)
    assert.equal(changes.length, 1)
    assert.match(changes[0].status, /^R0[0-9]{2}$/)
    assert.equal(changes[0].oldPath, 'packages/Old.cs'); assert.equal(changes[0].path, 'docs/new.md')
    const plan = classifyFocusedModes(changes)
    assert.equal(plan.selection, 'focused')
    assert.deepEqual(plan.requiredModes, ['coverage-off', 'coverage-on'])
    assert.ok(requireRunnableSelection(plan))
  } finally { rmSync(root, {recursive: true, force: true}) }
})
test('malformed and out-of-range rename/copy scores fail both selection and plan validation', () => {
  for (const status of ['R101', 'C101', 'R999', 'C999', 'R0100', 'C1000', 'R-01', 'C+01', 'R1e2', 'C01x']) {
    const plan = classifyFocusedModes([{status, oldPath: 'packages/Old.cs', path: 'docs/new.md'}])
    assert.equal(plan.selection, 'unsupported', status)
    assert.throws(() => requireRunnableSelection(plan), /unsupported-selection/)
    const forged = selected(); forged.changes[0].status = status
    const body = {...forged}; delete body.planDigest; forged.planDigest = hash(body)
    assert.throws(() => requireRunnableSelection(forged), /invalid-plan/, status)
  }
})
for (const change of [
  {status: 'M', path: 'apps/unknown/pnpm-lock.yaml'},
  {status: 'M', path: 'packages/contracts/unknown.yaml'},
  {status: 'M', path: 'apps/capability-host/nested/pnpm-lock.yaml'},
  {status: 'M', path: 'packages/contracts/pnpm-lock.yaml.bak'},
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
test('an unsupported change cannot become a runnable selector by recomputing its consistency digest', () => {
  const plan = classifyFocusedModes([{status: 'M', path: 'unknown.blob'}]); plan.selection = 'focused'
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
  ['collector produced no executable lines', rows => { rows[1].coverage.validLines = 0 }, 'nonpopulated-coverage'],
  ['collector produced no source paths', rows => { rows[1].coverage.paths = [] }, 'nonpopulated-coverage'],
  ['collector counts are inconsistent', rows => { rows[1].coverage.coveredLines = 2 }, 'nonpopulated-coverage'],
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

const populatedCoverage = '<coverage line-rate="0"><sources><source>src</source></sources><packages><package name="production"><classes><class name="Example" filename="source.cs"><lines><line number="7" hits="0" /></lines></class></classes></package></packages></coverage>'
function executorFixture({delta = 'M\0packages/foundation/Compiler.cs\0', childFailure = false, changeInputs = false,
  coverageXml = populatedCoverage, collectorName = 'coverage.cobertura.xml', offCollectorName = null} = {}) {
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
    // The process is simulated, but result parsing and report processing are real.
    writeFileSync(path.join(output, 'focused.trx'), `<TestRun><Results>${requiredNames.map((name, i) =>
      `<UnitTestResult testName="${name}" testId="test-${i}" outcome="Passed" />`).join('')}</Results><ResultSummary><Counters total="5" passed="5" failed="0" notExecuted="0" /></ResultSummary></TestRun>`)
    if (options.env.HARBORLINE_GATE_COVERAGE === '1') writeFileSync(path.join(output, collectorName), coverageXml)
    else if (offCollectorName) writeFileSync(path.join(output, offCollectorName), populatedCoverage)
    return {status: childFailure && calls.length === 1 ? 1 : 0}
  }
  return {root, calls, options: {apiRoot: root, git, command, sdk: () => context.sdk,
    dependencies: () => context.dependencies}, cleanup: () => rmSync(root, {recursive: true, force: true})}
}

for (const collectorName of ['coverage.cobertura.xml', 'cobertura-coverage.xml', 'COBERTURA-COVERAGE.XML']) {
  test(`populated ON collector report is accepted: ${collectorName}`, () => {
    const fixture = executorFixture({collectorName})
    try { assert.equal(executeFocusedModes(fixture.options).status, 'passed') }
    finally { fixture.cleanup() }
  })
  test(`OFF collector output fails after both modes execute: ${collectorName}`, () => {
    const fixture = executorFixture({offCollectorName: collectorName})
    try {
      assert.throws(() => executeFocusedModes(fixture.options), /unexpected-coverage/)
      assert.equal(fixture.calls.length, 2)
    } finally { fixture.cleanup() }
  })
}
for (const [label, coverageXml, expectedError] of [
  ['zero classes', '<coverage line-rate="1"><sources><source>src</source></sources><packages></packages></coverage>', 'nonpopulated-coverage'],
  ['zero lines', populatedCoverage.replace('<line number="7" hits="0" />', ''), 'nonpopulated-coverage'],
  ['invalid hits', populatedCoverage.replace('hits="0"', 'hits="invalid"'), 'missing-coverage'],
]) test(`ON ${label} cannot certify collection with successful real TRX parsing`, () => {
  const fixture = executorFixture({coverageXml})
  try {
    assert.throws(() => executeFocusedModes(fixture.options), new RegExp(expectedError))
    assert.equal(fixture.calls.length, 2)
  } finally { fixture.cleanup() }
})
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
test('processed ON evidence has a portable source root and retains population/hash binding across checkout paths', () => {
  const fixtures = [executorFixture(), executorFixture()]
  try {
    const processed = fixtures.map(fixture => {
      assert.equal(executeFocusedModes(fixture.options).status, 'passed')
      const output = fixture.calls[1].args[fixture.calls[1].args.indexOf('--results-directory') + 1]
      const bytes = readFileSync(path.join(output, 'focused.cobertura.xml'))
      assert.match(bytes.toString(), /<source>\.<\/source>/)
      assert.equal(bytes.toString().includes(fixture.root.replaceAll('\\', '/')), false)
      const receipt = JSON.parse(readFileSync(path.join(path.dirname(output), 'receipts.json'), 'utf8')).receipts[1]
      assert.deepEqual(receipt.coverage, {coveredLines: 0, validLines: 1, paths: ['source.cs']})
      assert.equal(receipt.coverageDigest, `sha256:${createHash('sha256').update(bytes).digest('hex')}`)
      return bytes
    })
    assert.notEqual(fixtures[0].root, fixtures[1].root)
    assert.deepEqual(processed[0], processed[1])
  } finally { for (const fixture of fixtures) fixture.cleanup() }
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

test('the owned durable-write architecture inventory retains mandatory OFF and ON', () => {
  const plan = classifyFocusedModes([{status: 'M', path: 'apps/local-node-host/tests/ArchTests/durable-write-classification.tsv'}])
  assert.equal(plan.selection, 'focused')
  assert.equal(plan.changes[0].kind, 'build-artifact')
  assert.deepEqual(plan.requiredModes, ['coverage-off', 'coverage-on'])
  assert.equal(requireRunnableSelection(plan).id, 'compiled-write-fences-v1')
})

for (const file of [
  'apps/local-node-host/tests/ArchTests/another-inventory.tsv',
  'apps/local-node-host/tests/Identity/durable-write-classification.tsv',
  'docs/durable-write-classification.tsv',
]) test(`unowned inventory stays unsupported: ${file}`, () => {
  const plan = classifyFocusedModes([{status: 'M', path: file}])
  assert.deepEqual(plan.requiredModes, ['coverage-off', 'coverage-on'])
  assert.throws(() => requireRunnableSelection(plan), /unsupported-selection/)
})

for (const file of [
  'apps/capability-host/pnpm-lock.yaml', 'apps/capability-host/pnpm-workspace.yaml',
  'packages/contracts/pnpm-lock.yaml', 'packages/contracts/pnpm-workspace.yaml',
]) test(`owned pnpm input cannot waive either completed mode: ${file}`, () => {
  const plan = classifyFocusedModes([{status: 'M', path: file}])
  assert.equal(validateFocusedModeEvidence(plan, context, evidence(plan)).status, 'passed')
  for (const missing of ['coverage-off', 'coverage-on']) {
    assert.throws(() => validateFocusedModeEvidence(plan, context,
      evidence(plan).filter(row => row.mode !== missing)), /missing-mode/)
  }
})
