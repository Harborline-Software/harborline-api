import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import * as fsExtra from 'node:fs'
import {repositoryRelativePath} from '../normalize-roslyn-sarif.mjs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {spawnSync} from 'node:child_process'

const root = path.resolve(import.meta.dirname, '../..')
const normalizer = path.join(root, 'eng/normalize-roslyn-sarif.mjs')
const repin = path.join(root, 'eng/repin-quality-baseline.mjs')
const quality = 'C:/Users/Chris/AppData/Local/Temp/claude/C--Projects-Harborline/90e98f9a-7b65-4ac7-a0b9-c8603b865f60/scratchpad/quality-pin'
const control = 'C:/Projects/Harborline/harborline-control'

const run = (command, args, options = {}) => {
  const result = spawnSync(command, args, {encoding: 'utf8', ...options})
  assert.equal(result.status, 0, result.stderr || result.stdout)
  return result
}

const writeFixtureRepository = directory => {
  mkdirSync(path.join(directory, 'src'))
  writeFileSync(path.join(directory, 'src/Test.csproj'), '<Project />\n')
  writeFileSync(path.join(directory, 'src/One.cs'), 'class One {}\n')
  writeFileSync(path.join(directory, 'src/Two.cs'), 'class Two {}\n')
  run('git', ['init', '-q'], {cwd: directory})
  run('git', ['config', 'user.name', 'Normalizer Test'], {cwd: directory})
  run('git', ['config', 'user.email', 'normalizer@test.invalid'], {cwd: directory})
  run('git', ['add', 'src'], {cwd: directory})
  run('git', ['commit', '--no-verify', '-qm', 'fixture'], {cwd: directory})
  const head = run('git', ['rev-parse', 'HEAD'], {cwd: directory}).stdout.trim()
  run('git', ['update-ref', 'refs/remotes/origin/main', head], {cwd: directory})
  return head
}

const cqg = (directory, sarif, baseline, output, writeBaseline) => run(process.execPath, [
  path.join(quality, 'bin/cqg.mjs'), 'analyze', '--sarif', sarif,
  '--diff', path.join(directory, 'empty.diff'), '--baseline', baseline,
  '--policy-defaults', path.join(control, 'policy/quality-defaults.yaml'),
  '--policy', path.join(root, 'eng/quality-policy.yaml'), '--repo-root', directory,
  '--out', output, ...(writeBaseline ? ['--write-baseline', writeBaseline] : []),
], {cwd: quality})

const sarifWith = uri => ({
  version: '2.1.0',
  runs: [{
    tool: {driver: {name: 'fixture'}},
    results: [{
      ruleId: 'CA1031',
      level: 'warning',
      message: {text: 'fixture'},
      locations: [{physicalLocation: {
        artifactLocation: {uri},
        region: {startLine: 7, startColumn: 3},
      }}],
    }],
  }],
})

test('normalizer makes Windows and POSIX locations repository-relative and clone-stable', t => {
  const directory = mkdtempSync(path.join(tmpdir(), 'roslyn-normalizer-test-'))
  t.after(() => rmSync(directory, {recursive: true, force: true}))
  const fixtures = [
    {name: 'windows', repoRoot: 'C:/clones/api', uri: 'file:///C:/clones/api/packages/foo/Source.cs'},
    {name: 'posix', repoRoot: '/opt/build/api', uri: '/opt/build/api/packages/foo/Source.cs'},
  ]

  const normalized = fixtures.map(fixture => {
    const projectDirectory = path.join(directory, fixture.name)
    mkdirSync(projectDirectory)
    const file = path.join(projectDirectory, 'Fixture.Project.sarif')
    writeFileSync(file, JSON.stringify(sarifWith(fixture.uri)))
    const result = spawnSync(process.execPath,
      [normalizer, '--repo-root', fixture.repoRoot, file], {encoding: 'utf8'})
    assert.equal(result.status, 0, result.stderr)
    return JSON.parse(readFileSync(file, 'utf8')).runs[0].results[0]
  })

  for (const result of normalized) {
    assert.equal(result.locations[0].physicalLocation.artifactLocation.uri,
      'packages/foo/Source.cs')
    assert.doesNotMatch(result.locations[0].physicalLocation.artifactLocation.uri,
      /^(?:file:|[A-Za-z]:|\/)/)
  }
  assert.equal(normalized[0].partialFingerprints['harborline/primary-location/v2'],
    normalized[1].partialFingerprints['harborline/primary-location/v2'])
  assert.match(normalized[0].partialFingerprints['harborline/primary-location/v2'], /^sha256:[a-f0-9]{64}$/)
})

test('normalizer fingerprints findings from distinct project outputs separately and maps the compiler driver to roslyn', t => {
  const directory = mkdtempSync(path.join(tmpdir(), 'roslyn-normalizer-project-test-'))
  t.after(() => rmSync(directory, {recursive: true, force: true}))
  const fixture = sarifWith('C:/clones/api/packages/foo/Source.cs')
  fixture.runs[0].tool.driver.name = 'Microsoft (R) Visual C# Compiler'
  const normalized = ['Project.One', 'Project.Two'].map(project => {
    const file = path.join(directory, `${project}.sarif`)
    writeFileSync(file, JSON.stringify(fixture))
    const result = spawnSync(process.execPath,
      [normalizer, '--repo-root', 'C:/clones/api', file], {encoding: 'utf8'})
    assert.equal(result.status, 0, result.stderr)
    return JSON.parse(readFileSync(file, 'utf8')).runs[0]
  })
  assert.equal(normalized[0].tool.driver.name, 'roslyn')
  assert.equal(normalized[1].tool.driver.name, 'roslyn')
  assert.notEqual(normalized[0].results[0].partialFingerprints['harborline/primary-location/v2'],
    normalized[1].results[0].partialFingerprints['harborline/primary-location/v2'])
})

test('a location under the root’s resolved real path is inside the repository (macOS /var -> /private/var)', () => {
  const {realpathSync, symlinkSync, mkdirSync} = fsExtra
  const base = mkdtempSync(path.join(tmpdir(), 'sarif-real-'))
  try {
    const real = path.join(base, 'real'); mkdirSync(real)
    const link = path.join(base, 'link'); symlinkSync(real, link, 'junction')
    const resolved = realpathSync.native(link)
    const uri = 'file:///' + path.posix.join(resolved.split(path.sep).join('/'), 'src/A.cs').replace(/^\//, '')
    assert.equal(repositoryRelativePath(uri, link), 'src/A.cs')
    assert.equal(repositoryRelativePath(uri, resolved), 'src/A.cs')
  } finally {
    rmSync(base, {recursive: true, force: true})
  }
})

test('re-pin retains every gate-time engine partial and the pinned analyzer classifies them as existing', t => {
  const directory = mkdtempSync(path.join(tmpdir(), 'quality-repin-parity-'))
  t.after(() => rmSync(directory, {recursive: true, force: true}))
  const head = writeFixtureRepository(directory)
  const sarif = path.join(directory, 'Test.sarif')
  const baseline = path.join(directory, 'baseline.json')
  const firstDecision = path.join(directory, 'first-decision.json')
  const secondDecision = path.join(directory, 'second-decision.json')
  writeFileSync(path.join(directory, 'empty.diff'), '')
  writeFileSync(baseline, JSON.stringify({schemaVersion: 1, commit: head, generatedAt: '2026-09-11T00:00:00.000Z', findings: []}) + '\n')
  const fixture = sarifWith(path.join(directory, 'src/One.cs'))
  fixture.runs[0].tool.driver.name = 'Microsoft (R) Visual C# Compiler'
  fixture.runs[0].results.push({...sarifWith(path.join(directory, 'src/Two.cs')).runs[0].results[0], ruleId: 'CA1032', locations: [{physicalLocation: {artifactLocation: {uri: path.join(directory, 'src/Two.cs')}, region: {startLine: 11, startColumn: 1}}}]})
  writeFileSync(sarif, JSON.stringify(fixture))
  run(process.execPath, [normalizer, '--repo-root', directory, sarif])
  const normalized = JSON.parse(readFileSync(sarif, 'utf8')).runs[0].results
  cqg(directory, sarif, baseline, firstDecision, baseline)
  const legacy = JSON.parse(readFileSync(baseline, 'utf8'))
  for (const finding of legacy.findings) {
    const partial = JSON.parse(finding.enginePartial)
    delete partial['harborline/primary-location/v2']
    finding.enginePartial = JSON.stringify(partial)
  }
  writeFileSync(baseline, JSON.stringify(legacy) + '\n')
  run(process.execPath, [repin, baseline], {cwd: directory})
  const pinned = JSON.parse(readFileSync(baseline, 'utf8')).findings
  assert.equal(pinned.length, 2)
  assert.deepEqual(pinned.map(row => JSON.parse(row.enginePartial)).sort((a, b) => a['harborline/primary-location/v2'].localeCompare(b['harborline/primary-location/v2'])),
    normalized.map(row => row.partialFingerprints).sort((a, b) => a['harborline/primary-location/v2'].localeCompare(b['harborline/primary-location/v2'])))
  cqg(directory, sarif, baseline, secondDecision)
  const decision = JSON.parse(readFileSync(secondDecision, 'utf8'))
  assert.equal(decision.findings.filter(finding => finding.baselineState === 'new').length, 0)
  assert.equal(decision.resolved.length, 0)
})

test('a failed Roslyn execution with no results reports analyzer-error', t => {
  const directory = mkdtempSync(path.join(tmpdir(), 'quality-empty-engine-'))
  t.after(() => rmSync(directory, {recursive: true, force: true}))
  const head = writeFixtureRepository(directory)
  const sarif = path.join(directory, 'Test.sarif')
  const baseline = path.join(directory, 'baseline.json')
  const decisionFile = path.join(directory, 'decision.json')
  writeFileSync(path.join(directory, 'empty.diff'), '')
  writeFileSync(baseline, JSON.stringify({schemaVersion: 1, commit: head, generatedAt: '2026-09-11T00:00:00.000Z', findings: []}) + '\n')
  writeFileSync(sarif, JSON.stringify({version: '2.1.0', runs: [{tool: {driver: {name: 'roslyn'}}, invocations: [{executionSuccessful: false}], results: []}]}))
  cqg(directory, sarif, baseline, decisionFile)
  const decision = JSON.parse(readFileSync(decisionFile, 'utf8'))
  assert.ok(decision.reasons.some(reason => reason.kind === 'analyzer-error' && reason.engine === 'roslyn'))
})
