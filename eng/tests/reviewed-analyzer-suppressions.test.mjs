import test from 'node:test'
import assert from 'node:assert/strict'
import {createHash} from 'node:crypto'
import {copyFileSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {spawnSync} from 'node:child_process'
import {normalizeSarifFile} from '../normalize-roslyn-sarif.mjs'
import {annotateReviewedBaseline, readReviewedSuppressions} from '../reviewed-analyzer-suppressions.mjs'
import {compareFindings} from '../quality-baseline-compare.mjs'

const source = 'class Example {\n#pragma warning disable CA1309 // reviewed-suppression: cursor-comparison\nvar after = string.Compare(left, right) > 0;\n#pragma warning restore CA1309\n}\n'
const reference = {path: 'evidence.md', anchor: 'Provider trial and reviewed contract'}
const entry = {
  id: 'cursor-comparison', rule: 'CA1309', path: 'Example.cs', scopeKind: 'statement',
  scopeSha256: createHash('sha256').update(source.split('\n').slice(1, 4).join('\n')).digest('hex'),
  justification: {
    protectedBehavior: 'The cursor and SQL ordering use the same provider comparison.',
    whyComplianceChangesBehavior: 'The provider cannot translate the overload.',
    protectedRisk: 'Locale-dependent managed ordering can lose cursor rows.',
    applicability: 'This expression executes in SQL, not as a managed comparison.',
    remainingControls: 'Provider ordering and cursor regression.',
    repeatedExceptionReview: 'A repeated exception requires separate rule applicability review.',
  },
  alternatives: [{kind: 'analysis', option: 'Filter in memory', reason: 'Changes bounded database paging.', evidence: [reference]}],
  tests: [{path: 'Regression.cs', anchor: 'Cursor_preserves_order'}], review: {...reference, status: 'reviewed', reviewedBy: 'fixture reviewer'},
}
function fixture(t, change = () => {}) {
  const root = mkdtempSync(path.join(tmpdir(), 'reviewed-suppression-'))
  t.after(() => rmSync(root, {recursive: true, force: true}))
  mkdirSync(path.join(root, 'eng'))
  writeFileSync(path.join(root, 'Example.cs'), source)
  writeFileSync(path.join(root, 'Regression.cs'), 'void Cursor_preserves_order() {}')
  writeFileSync(path.join(root, 'evidence.md'), reference.anchor)
  const manifest = {schemaVersion: 1, exceptions: [structuredClone(entry)]}
  change(manifest, root)
  writeFileSync(path.join(root, 'eng/reviewed-analyzer-suppressions.json'), JSON.stringify(manifest))
  const sarif = {version: '2.1.0', runs: [{tool: {driver: {name: 'Microsoft (R) Visual C# Compiler'}}, results: [
    {ruleId: 'CA1309', level: 'warning', message: {text: 'comparison'}, suppressions: [{kind: 'inSource'}], locations: [{physicalLocation: {artifactLocation: {uri: 'Example.cs'}, region: {startLine: 3}}}]},
    {ruleId: 'CA1031', level: 'warning', message: {text: 'new warning'}, locations: [{physicalLocation: {artifactLocation: {uri: 'Example.cs'}, region: {startLine: 5}}}]},
    {ruleId: 'CA1309', level: 'warning', message: {text: 'outside scope'}, suppressions: [{kind: 'inSource'}], locations: [{physicalLocation: {artifactLocation: {uri: 'Example.cs'}, region: {startLine: 5}}}]},
  ]}]}
  const file = path.join(root, 'Project.sarif')
  const raw = Buffer.from(JSON.stringify(sarif, null, 2) + '\n')
  writeFileSync(file, raw)
  return {root, file, raw, sarif}
}

test('only the exact reviewed source finding is accepted; every raw finding is retained', t => {
  const {root, file, raw} = fixture(t)
  normalizeSarifFile(file, root)
  const results = JSON.parse(readFileSync(file)).runs[0].results
  assert.equal(results.length, 3)
  assert.equal(results[0].suppressions[0].status, 'accepted')
  assert.equal(results[0].properties['harborline/reviewed-suppression'], 'cursor-comparison')
  assert.equal(results[1].suppressions, undefined)
  assert.equal(results[2].suppressions[0].status, undefined)
  assert.deepEqual(readFileSync(`${file}.raw`), raw)
})

for (const [name, change] of [
  ['missing justification', manifest => delete manifest.exceptions[0].justification.protectedRisk],
  ['unsupported evidence', manifest => manifest.exceptions[0].alternatives[0].kind = 'assumed-executed'],
  ['unsubstantiated execution', manifest => manifest.exceptions[0].alternatives[0].kind = 'executed'],
  ['missing test', manifest => manifest.exceptions[0].tests[0].anchor = 'Nonexistent_test'],
  ['missing review', manifest => delete manifest.exceptions[0].review],
  ['pending review', manifest => manifest.exceptions[0].review.status = 'pending'],
  ['wrong digest', manifest => manifest.exceptions[0].scopeSha256 = '0'.repeat(64)],
  ['broad rule', manifest => manifest.exceptions[0].rule = 'CA*'],
  ['duplicate entry', manifest => manifest.exceptions.push(structuredClone(manifest.exceptions[0]))],
  ['unsupported schema', manifest => manifest.schemaVersion = 2],
  ['multi-rule directive', (_, root) => writeFileSync(path.join(root, 'Example.cs'), source.replace('disable CA1309', 'disable CA1309, CA1031'))],
  ['file-wide scope', (_, root) => writeFileSync(path.join(root, 'Example.cs'), source.replace('var after', 'class EntireFile {}\nvar after'))],
  ['multiple statements', (manifest, root) => {
    const changed = source.replace('> 0;', '> 0; AnotherCall();')
    writeFileSync(path.join(root, 'Example.cs'), changed)
    manifest.exceptions[0].scopeSha256 = createHash('sha256').update(changed.split('\n').slice(1, 4).join('\n')).digest('hex')
  }],
]) test(`${name} fails closed without altering compiler input`, t => {
  const {root, file, raw} = fixture(t, change)
  assert.throws(() => normalizeSarifFile(file, root))
  assert.deepEqual(readFileSync(file), raw)
  assert.deepEqual(readFileSync(`${file}.raw`), raw)
})

test('an accepted SARIF status cannot authorize a finding without reviewed source evidence', t => {
  const {root, file, sarif} = fixture(t)
  sarif.runs[0].results[1].suppressions = [{kind: 'inSource', status: 'accepted'}]
  writeFileSync(file, JSON.stringify(sarif))
  assert.throws(() => normalizeSarifFile(file, root), /no reviewed source evidence/)
})

test('a manifest alone cannot suppress a compiler finding', t => {
  const {root, file, sarif} = fixture(t)
  delete sarif.runs[0].results[0].suppressions
  writeFileSync(file, JSON.stringify(sarif))
  assert.throws(() => normalizeSarifFile(file, root), /compiler source suppression/)
})

test('an accepted finding cannot end before its physical start', t => {
  const {root, file, sarif} = fixture(t)
  sarif.runs[0].results[0].suppressions[0].status = 'accepted'
  sarif.runs[0].results[0].locations[0].physicalLocation.region.endLine = 0
  writeFileSync(file, JSON.stringify(sarif))
  assert.throws(() => normalizeSarifFile(file, root), /no reviewed source evidence/)
})

test('baseline comparison accepts only the verified local exception and retains candidate evidence', t => {
  const {root} = fixture(t)
  const entries = readReviewedSuppressions(root)
  const candidate = {fingerprint: 'reviewed', ruleId: 'CA1309', path: 'Example.cs', line: 3}
  const reviewed = annotateReviewedBaseline(candidate, {engine: 'roslyn', suppressed: true}, entries)
  const active = {fingerprint: 'new-warning', ruleId: 'CA1031', path: 'Example.cs', line: 5}
  const head = [reviewed, active]
  assert.deepEqual(compareFindings(head, [], '', entries).newFindings, [active])
  assert.equal(head.length, 2)
  assert.equal(head[0].suppressed, true)
  assert.throws(() => compareFindings(head, []), /current reviewed source/)
})

test('reviewed exception cannot use identity fallback to displace an active baseline finding', t => {
  const {root} = fixture(t)
  const entries = readReviewedSuppressions(root)
  const reviewed = annotateReviewedBaseline({fingerprint: 'reviewed', ruleId: 'CA1309', path: 'Example.cs', line: 3}, {engine: 'roslyn', suppressed: true}, entries)
  const existing = {fingerprint: 'existing', ruleId: 'CA1309', path: 'Example.cs', line: 5}
  const added = {fingerprint: 'added', ruleId: 'CA1309', path: 'Example.cs', line: 6}
  assert.deepEqual(compareFindings([reviewed, existing, added], [existing], '', entries).newFindings, [added])
})

test('a retained baseline exception cannot cover a later active warning or reactivation', t => {
  const {root} = fixture(t)
  const entries = readReviewedSuppressions(root)
  const reviewed = annotateReviewedBaseline({fingerprint: 'reviewed', ruleId: 'CA1309', path: 'Example.cs', line: 3}, {engine: 'roslyn', suppressed: true}, entries)
  const retained = compareFindings([reviewed], [reviewed], '', entries)
  assert.deepEqual(retained.resolved, [])
  assert.deepEqual(retained.newFindings, [])
  const later = {fingerprint: 'new-active', ruleId: 'CA1309', path: 'Example.cs', line: 5}
  assert.deepEqual(compareFindings([later], [reviewed], '', entries).newFindings, [later])
  const reactivated = {fingerprint: 'reviewed', ruleId: 'CA1309', path: 'Example.cs', line: 3}
  assert.deepEqual(compareFindings([reactivated], [reviewed]).newFindings, [reactivated])
})

for (const [name, mutate] of [
  ['unverified accepted status', finding => delete finding.reviewedSuppression],
  ['wrong rule', finding => finding.ruleId = 'CA1031'],
  ['wrong path', finding => finding.path = 'Other.cs'],
  ['outside scope', finding => finding.line = 5],
  ['changed scope proof', finding => finding.reviewedSuppression.scopeSha256 = '0'.repeat(64)],
  ['not accepted by analyzer', finding => finding.suppressed = false],
]) test(`baseline candidate ${name} fails closed`, t => {
  const {root} = fixture(t)
  const entries = readReviewedSuppressions(root)
  const finding = annotateReviewedBaseline({fingerprint: 'reviewed', ruleId: 'CA1309', path: 'Example.cs', line: 3}, {engine: 'roslyn', suppressed: true}, entries)
  mutate(finding)
  assert.throws(() => compareFindings([finding], [], '', entries))
})

test('an additional bare catch is rejected even with its updated digest', t => {
  const {root, file} = fixture(t, (manifest, root) => {
    const block = '#pragma warning disable CA1031 // reviewed-suppression: cursor-comparison\ncatch (Exception error) { Record(error); } catch { Ignore(); }\n#pragma warning restore CA1031'
    writeFileSync(path.join(root, 'Example.cs'), block)
    Object.assign(manifest.exceptions[0], {rule: 'CA1031', scopeKind: 'catch', scopeSha256: createHash('sha256').update(block).digest('hex')})
  })
  assert.throws(() => normalizeSarifFile(file, root), /one handler/)
})

test('unanchored accepted findings fail closed and their raw bytes remain available', t => {
  const {root, file, sarif} = fixture(t)
  sarif.runs[0].results.push({ruleId: 'CA1309', suppressions: [{kind: 'inSource', status: 'accepted'}]})
  const raw = Buffer.from(JSON.stringify(sarif))
  writeFileSync(file, raw)
  assert.throws(() => normalizeSarifFile(file, root), /no physical source scope/)
  assert.deepEqual(readFileSync(`${file}.raw`), raw)
})

for (const [name, mutate] of [
  ['negative line', result => result.locations[0].physicalLocation.region.startLine = -1],
  ['string line', result => result.locations[0].physicalLocation.region.startLine = '3'],
  ['missing URI', result => delete result.locations[0].physicalLocation.artifactLocation.uri],
  ['missing rule', result => delete result.ruleId],
]) test(`accepted finding with ${name} fails closed before filtering`, t => {
  const {root, file, sarif} = fixture(t)
  const result = sarif.runs[0].results[0]
  result.suppressions[0].status = 'accepted'
  mutate(result)
  writeFileSync(file, JSON.stringify(sarif))
  assert.throws(() => normalizeSarifFile(file, root), /no physical source scope/)
})

const quality = process.env.HARBORLINE_QUALITY_REPO
const control = process.env.HARBORLINE_CONTROL_REPO
test('the pinned quality tool retains the reviewed finding and still fails new unsuppressed warnings', {
  skip: !quality || !control ? 'needs pinned quality and Control checkouts' : false,
}, t => {
  const {root, file, sarif} = fixture(t)
  const git = (...args) => {
    const result = spawnSync('git', args, {cwd: root, encoding: 'utf8'})
    assert.equal(result.status, 0, result.stderr)
    return result.stdout.trim()
  }
  git('init', '-q')
  git('config', 'user.name', 'Reviewed Suppression Test')
  git('config', 'user.email', 'reviewed-suppression@test.invalid')
  writeFileSync(path.join(root, 'base.txt'), 'base')
  git('add', 'base.txt')
  git('commit', '--no-verify', '-qm', 'base')
  const base = git('rev-parse', 'HEAD')
  git('update-ref', 'refs/remotes/origin/main', base)
  git('add', 'Example.cs')
  git('commit', '--no-verify', '-qm', 'new source')
  for (let index = 0; index < 4; index++) sarif.runs[0].results.push({
    ruleId: 'CA1031', level: 'warning', message: {text: `additional new warning ${index}`},
    locations: [{physicalLocation: {artifactLocation: {uri: 'Example.cs'}, region: {startLine: 5, startColumn: index + 2}}}],
  })
  sarif.runs.push({tool: {driver: {name: 'eslint'}}, results: []})
  writeFileSync(file, JSON.stringify(sarif))
  normalizeSarifFile(file, root)
  writeFileSync(path.join(root, 'change.diff'), git('diff', `${base}..HEAD`))
  writeFileSync(path.join(root, 'baseline.json'), JSON.stringify({schemaVersion: 1, commit: base, generatedAt: '2026-10-03T00:00:00Z', findings: []}))
  writeFileSync(path.join(root, 'policy.yaml'), 'mode: evaluate\n')
  const result = spawnSync(process.execPath, [path.join(quality, 'bin/cqg.mjs'), 'analyze',
    '--sarif', file, '--diff', path.join(root, 'change.diff'), '--baseline', path.join(root, 'baseline.json'),
    '--policy-defaults', path.join(control, 'policy/quality-defaults.yaml'), '--policy', path.join(root, 'policy.yaml'),
    '--repo-root', root, '--out', path.join(root, 'decision.json')], {cwd: quality, encoding: 'utf8'})
  assert.equal(result.status, 0, result.stderr)
  const decision = JSON.parse(readFileSync(path.join(root, 'decision.json')))
  const reviewed = decision.findings.filter(item => item.ruleId === 'CA1309' && item.suppressed)
  assert.equal(reviewed.length, 1)
  assert.equal(reviewed[0].introducedInPullRequest, true)
  assert.equal(decision.findings.filter(item => item.introducedInPullRequest && !item.suppressed).length, 6)
  assert.ok(decision.reasons.some(item => item.kind === 'finding-threshold' && item.severity === 'warning' && item.actual === 6 && item.allowed === 5), JSON.stringify(decision.reasons))
  // Exercise API's actual candidate writer and landing comparison, not only CQG.
  const artifactDirectory = path.join(root, 'artifacts/quality')
  mkdirSync(artifactDirectory, {recursive: true})
  mkdirSync(path.join(root, 'eng/baselines'))
  copyFileSync(file, path.join(artifactDirectory, 'Project.sarif'))
  copyFileSync(`${file}.raw`, path.join(artifactDirectory, 'Project.sarif.raw'))
  copyFileSync(path.join(root, 'baseline.json'), path.join(root, 'eng/baselines/quality-baseline.json'))
  copyFileSync(path.resolve(import.meta.dirname, '../quality-pin.json'), path.join(root, 'eng/quality-pin.json'))
  copyFileSync(path.join(root, 'policy.yaml'), path.join(root, 'eng/quality-policy.yaml'))
  const api = spawnSync(process.execPath, [path.resolve(import.meta.dirname, '../quality-step.mjs'), '--root', root], {
    cwd: root, encoding: 'utf8', env: {...process.env, HARBORLINE_QUALITY_REPO: quality, HARBORLINE_CONTROL_REPO: control, HARBORLINE_VERIFY_QUALITY_RUN: ''},
  })
  assert.equal(api.status, 0, api.stderr || api.stdout)
  const candidate = path.join(artifactDirectory, 'findings.json')
  const retained = JSON.parse(readFileSync(candidate)).findings
  assert.equal(retained.length, 7)
  assert.equal(retained.filter(item => item.suppressed === true && item.reviewedSuppression?.id === 'cursor-comparison').length, 1)
  const comparison = spawnSync(process.execPath, [path.resolve(import.meta.dirname, '../quality-baseline-compare.mjs'),
    candidate, path.join(root, 'eng/baselines/quality-baseline.json'), path.join(artifactDirectory, 'base-to-head.diff')], {cwd: root, encoding: 'utf8'})
  assert.equal(comparison.status, 0, comparison.stderr)
  const compared = JSON.parse(comparison.stdout)
  assert.equal(compared.new, 6)
  assert.equal(compared.findings.length, 6)
})

for (const [name, handler, valid] of [
  ['one bare handler', 'catch { PreservePrimary(); }', true],
  ['two bare handlers', 'catch { PreservePrimary(); } catch { Ignore(); }', false],
  ['bare handler followed by a statement', 'catch { PreservePrimary(); } Ignore();', false],
]) test(`reviewed catch parser: ${name}`, t => {
  const {root} = fixture(t, (manifest, root) => {
    const block = '#pragma warning disable CA1031 // reviewed-suppression: cursor-comparison\n' + handler + '\n#pragma warning restore CA1031'
    writeFileSync(path.join(root, 'Example.cs'), block)
    Object.assign(manifest.exceptions[0], {rule: 'CA1031', scopeKind: 'catch', scopeSha256: createHash('sha256').update(block).digest('hex')})
  })
  if (valid) assert.equal(readReviewedSuppressions(root).length, 1)
  else assert.throws(() => readReviewedSuppressions(root), /one handler/)
})
