import test from 'node:test'
import assert from 'node:assert/strict'
import {digest, fingerprint} from '../validation-reuse.mjs'
import {transportProblems, createGitHubClient, observeRun, compareRuns, readArchive} from '../validation-github-shadow.mjs'
import {execFileSync, spawnSync} from 'node:child_process'
import path from 'node:path'
import {mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import {compareDirectories, compareObservations} from '../validation-shadow-report.mjs'
import {inputs as fixtureInputs} from './validation-fixtures.mjs'

const archive = Buffer.from('archive fixture')
const run = {run_started_at: '2026-10-03T10:00:00Z', id: 123, run_attempt: 1, repository: {full_name: 'Harborline-Software/harborline-api'},
  head_repository: {full_name: 'Harborline-Software/harborline-api'}, head_sha: 'a'.repeat(40),
  path: '.github/workflows/verify.yml', event: 'pull_request', status: 'completed', conclusion: 'success'}
const job = {started_at: '2026-10-03T10:01:00Z', completed_at: '2026-10-03T10:10:00Z', id: 456, name: 'verify-macos', run_id: 123, run_attempt: 1, status: 'completed', conclusion: 'success',
  steps: [{name: "Host lane (exact-clone against this host's baseline)", status: 'completed', conclusion: 'success'}]}
const artifact = {created_at: '2026-10-03T10:09:00Z', id: 789, name: 'verify-macos-evidence-123', workflow_run: {id: 123, head_sha: 'a'.repeat(40)},
  expired: false, digest: `sha256:${digest(archive)}`}
const problems = (changes = {}) => transportProblems({run, job, artifact, archive, lane: 'verify-macos', ...changes})
test('GitHub digest and identity bind exact completed run attempt, job and artifact', () => {
  assert.deepEqual(problems(), [])
  for (const conclusion of ['failure', 'cancelled', 'skipped', null]) {
    assert.ok(problems({run: {...run, conclusion}}).length)
    assert.ok(problems({job: {...job, conclusion}}).length)
  }
  assert.ok(problems({job: {...job, run_attempt: 2}}).length)
  assert.ok(problems({job: {...job, steps: []}}).length)
  assert.ok(problems({job: {...job, steps: [{...job.steps[0], conclusion: 'skipped'}]}}).length)
  assert.ok(problems({artifact: {...artifact, expired: true}}).length)
  assert.ok(problems({artifact: {...artifact, digest: null}}).length)
  assert.ok(problems({archive: Buffer.from('tampered')}).length)
  assert.ok(problems({run: {...run, head_repository: {full_name: 'attacker/fork'}}}).length)
  assert.ok(problems({artifact: {...artifact, workflow_run: {...artifact.workflow_run, id: 124}}}).length)
  assert.ok(problems({run: {...run, run_attempt: 2, run_started_at: '2026-10-04T10:00:00Z'},
    job: {...job, run_attempt: 2, started_at: '2026-10-04T10:01:00Z', completed_at: '2026-10-04T10:10:00Z'}})
    .includes('artifact is not bound to this job attempt time window'), 'day-old same-run artifact is not attempt-two evidence')
  assert.ok(problems({artifact: {...artifact, created_at: undefined}}).length)
})
test('download redirect never receives API credentials and non-HTTPS redirects are refused', async () => {
  const calls = []
  const api = createGitHubClient('secret-test-token', async (url, options) => {
    calls.push({url, options})
    return calls.length === 1 ? new Response(null, {status: 302, headers: {location: 'https://blob.example/artifact'}})
      : new Response(archive)
  })
  assert.deepEqual(await api('/repos/Harborline-Software/harborline-api/actions/artifacts/789/zip', true), archive)
  assert.equal(calls[0].options.headers.Authorization, 'Bearer secret-test-token')
  assert.equal(calls[1].options.headers, undefined)
  await assert.rejects(createGitHubClient('token', async () => new Response(null,
    {status: 302, headers: {location: 'http://example/artifact'}}))(
    '/repos/Harborline-Software/harborline-api/actions/artifacts/789/zip', true), /unsafe artifact/)
  await assert.rejects(api('https://attacker.example'), /unsupported API endpoint/)
})
const inputs = {schemaVersion: 1, repository: run.repository.full_name, candidateTree: 'b'.repeat(40), lane: 'host',
  dependencies: {}, producer: {}, toolchain: {}, platform: {os: 'darwin', architecture: 'arm64'}, pins: {}, selection: {hostBaseline: 'eng/baselines/host-test-baseline.macos.json'}, coverage: {enabled: false},
  commitInputs: {}, unknownInputs: ['evaluated compiler inputs not observed']}
const unpack = () => ({observation: {candidateSha: 'c'.repeat(40), inputs, fingerprint: fingerprint(inputs)},
  receipt: {baseHead: 'c'.repeat(40), testedTree: 'b'.repeat(40), lane: 'host'}})
const mockApi = async endpoint => {
  if (endpoint.endsWith('/123')) return run
  if (endpoint.includes('/artifacts?')) return {total_count: 1, artifacts: [artifact]}
  if (endpoint.includes('/jobs?')) return {total_count: 1, jobs: [job]}
  if (endpoint.endsWith('/zip')) return archive
  if (endpoint.includes('/commits/')) return {commit: {tree: {sha: 'b'.repeat(40)}}, parents: [{sha: 'a'.repeat(40)}]}
  throw new Error('unexpected test request')
}
test('real orchestration verifies transport but never promotes unsigned branch data to reuse', async () => {
  const result = await compareRuns({currentRunId: 123, priorRunId: 123, api: mockApi, unpack})
  assert.equal(result.lanes[0].currentTransportVerified, true)
  assert.equal(result.lanes[0].sameInputs, true)
  assert.equal(result.lanes[0].completeInputs, false)
  assert.equal(result.lanes[0].trusted, false)
  assert.equal(result.reuseAuthorized, false)
  assert.equal(result.requiredWorkSkipped, false)
  assert.deepEqual(result.lanes[0].currentProblems, [])
  assert.deepEqual(result.lanes[0].priorProblems, [])
  assert.equal(result.lanes[0].currentEvent, 'pull_request')
  assert.equal(result.lanes[0].priorEvent, 'pull_request')
  assert.deepEqual(result.lanes[1].currentProblems, ['missing or duplicate job/artifact'])
  assert.deepEqual(result.lanes[1].priorProblems, ['missing or duplicate job/artifact'])
  assert.equal((await observeRun({runId: 123, api: mockApi, unpack}))[0].event, 'pull_request')
})

test('merge-group event comes from authenticated run metadata for coverage profile selection', async () => {
  const api = async endpoint => {
    if (endpoint.endsWith('/123')) return {...run, event: 'merge_group', head_sha: 'c'.repeat(40)}
    if (endpoint.includes('/artifacts?')) return {total_count: 1, artifacts: [{...artifact,
      workflow_run: {...artifact.workflow_run, head_sha: 'c'.repeat(40)}}]}
    return mockApi(endpoint)
  }
  const observed = await observeRun({runId: 123, unpack, api})
  assert.equal(observed[0].transportVerified, true)
  assert.equal(observed[0].event, 'merge_group')
  const report = await compareRuns({currentRunId: 123, priorRunId: 123, unpack, api})
  assert.equal(report.lanes[0].currentEvent, 'merge_group')
  assert.equal(report.lanes[0].priorEvent, 'merge_group')
})
test('orchestration rejects forged candidate binding, duplicate artifacts and pagination gaps', async () => {
  const forged = await observeRun({runId: 123, api: mockApi, unpack: () => ({...unpack(), receipt: {lane: 'all'}})})
  assert.equal(forged[0].observation, undefined)
  const duplicate = await observeRun({runId: 123, api: async endpoint => endpoint.includes('/artifacts?')
    ? {total_count: 2, artifacts: [artifact, artifact]} : mockApi(endpoint), unpack})
  assert.equal(duplicate[0].transportVerified, false)
  await assert.rejects(observeRun({runId: 123, api: async endpoint => endpoint.includes('/artifacts?')
    ? {total_count: 101, artifacts: [artifact]} : mockApi(endpoint), unpack}), /pagination/)
})

test('archive reader reads bounded data without extracting traversal entries and refuses duplicates', () => {
  const python = process.platform === 'win32' ? 'python' : 'python3'
  const zip = duplicate => execFileSync(python, ['-c',
    `import io,zipfile,json,sys\nb=io.BytesIO()\nwith zipfile.ZipFile(b,'w') as z:\n z.writestr('../never-extracted.py','raise Exception()')\n z.writestr('.claude/gate-evidence/validation-inputs-shadow.json',json.dumps({'marker':'observation'}))\n z.writestr('.git/harborline-api-verify-receipt.json',json.dumps({'marker':'receipt'}))\n if sys.argv[1]=='yes': z.writestr('.git/harborline-api-verify-receipt.json','{}')\nsys.stdout.buffer.write(b.getvalue())`,
    duplicate ? 'yes' : 'no'], {stdio: 'pipe'})
  assert.deepEqual(readArchive(zip(false)), {observation: {marker: 'observation'}, receipt: {marker: 'receipt'}})
  assert.throws(() => readArchive(zip(true)))
  assert.throws(() => readArchive(Buffer.from('not a zip')))
})

test('CLI never echoes credentials from malformed Authorization headers', () => {
  const synthetic = ['synthetic', 'credential', 'marker'].join('-')
  const result = spawnSync(process.execPath, [path.resolve(import.meta.dirname, '../validation-github-shadow.mjs'),
    '123', '124', 'unused-output.json'], {encoding: 'utf8', timeout: 10000,
      env: {...process.env, GH_TOKEN: `${synthetic}\ninvalid-header`}})
  assert.equal(result.status, 1)
  assert.equal(result.stderr.trim(), 'validation shadow unavailable: request, artifact or evidence validation failed')
  assert.equal(`${result.stdout}${result.stderr}`.includes(synthetic), false, 'diagnostics must not contain credential text')
})

test('normal gh download artifact folders compare by canonical host lane across different run IDs', t => {
  const root = mkdtempSync(path.join(tmpdir(), 'shadow-directory-comparison-'))
  t.after(() => rmSync(root, {recursive: true, force: true}))
  const current = path.join(root, 'current'), prior = path.join(root, 'prior')
  for (const lane of ['verify-macos', 'verify-linux', 'verify-windows-hosted']) {
    const manifest = fixtureInputs()
    manifest.platform = lane === 'verify-macos' ? {os: 'darwin', architecture: 'arm64', release: '24.0'}
      : lane === 'verify-linux' ? {os: 'linux', architecture: 'x64', release: '6.8'}
        : {os: 'win32', architecture: 'x64', release: '10.0.26100'}
    manifest.selection.hostBaseline = lane === 'verify-macos' ? 'eng/baselines/host-test-baseline.macos.json'
      : lane === 'verify-linux' ? 'eng/baselines/host-test-baseline.ubuntu.json' : 'eng/baselines/host-test-baseline.json'
    const observation = {candidateSha: 'a'.repeat(40), inputs: manifest, fingerprint: fingerprint(manifest)}
    for (const [directory, runId] of [[current, '123'], [prior, '122']]) {
      const folder = path.join(directory, `${lane}-evidence-${runId}`, '.claude/gate-evidence')
      mkdirSync(folder, {recursive: true})
      writeFileSync(path.join(folder, 'validation-inputs-shadow.json'), JSON.stringify(observation))
    }
  }
  const result = compareDirectories(current, prior)
  assert.deepEqual(result.lanes.map(lane => lane.lane), ['verify-macos', 'verify-linux', 'verify-windows-hosted'])
  assert.equal(result.evidenceState, 'present')
  assert.equal(result.lanes.every(lane => lane.sameInputs), true)
  assert.equal(result.lanes.every(lane => lane.completeInputs), true)
  assert.equal(result.reuseAuthorized, false)
  assert.equal(result.requiredWorkSkipped, false)
  // The real documented CLI must compare the same two downloaded directory shapes.
  const output = path.join(root, 'report.json')
  const cli = spawnSync(process.execPath, [path.resolve(import.meta.dirname, '../validation-shadow-report.mjs'),
    current, prior, output], {encoding: 'utf8'})
  assert.equal(cli.status, 0, cli.stderr)
  assert.equal(JSON.parse(readFileSync(output, 'utf8')).lanes.every(lane => lane.sameInputs), true)
  // Complete Windows evidence copied into every folder cannot qualify Mac/Linux.
  const windowsInputs = fixtureInputs()
  const wrongHost = {candidateSha: 'a'.repeat(40), inputs: windowsInputs, fingerprint: fingerprint(windowsInputs)}
  for (const [directory, runId] of [[current, '123'], [prior, '122']])
    for (const lane of ['verify-macos', 'verify-linux', 'verify-windows-hosted'])
      writeFileSync(path.join(directory, `${lane}-evidence-${runId}`, '.claude/gate-evidence/validation-inputs-shadow.json'), JSON.stringify(wrongHost))
  const wrong = compareDirectories(current, prior)
  assert.equal(wrong.evidenceState, 'unknown')
  for (const lane of wrong.lanes.slice(0, 2)) {
    assert.equal(lane.completeInputs, false)
    assert.deepEqual(lane.problems, ['current observation host profile differs from expected lane',
      'prior observation host profile differs from expected lane'])
  }
  assert.equal(wrong.lanes[2].completeInputs, true)
  assert.equal(wrong.reuseAuthorized, false)
  assert.equal(wrong.requiredWorkSkipped, false)
  for (const candidateSha of [undefined, 'invalid', 'a'.repeat(39), ['a'.repeat(40)], {}, 123]) {
    writeFileSync(path.join(current, 'verify-windows-hosted-evidence-123', '.claude/gate-evidence/validation-inputs-shadow.json'),
      JSON.stringify({...wrongHost, candidateSha}))
    const malformed = compareDirectories(current, prior)
    assert.equal(malformed.evidenceState, 'unknown')
    assert.equal(malformed.lanes[2].completeInputs, false)
    assert.match(malformed.lanes[2].problems.join(), /candidate identity missing or invalid/)
  }
  rmSync(path.join(current, 'verify-linux-evidence-123'), {recursive: true})
  const missingCurrent = compareDirectories(current, prior)
  assert.equal(missingCurrent.evidenceState, 'unknown')
  assert.equal(missingCurrent.lanes.length, 3)
  assert.match(missingCurrent.lanes[1].problems.join(), /current observation missing/)
  rmSync(path.join(prior, 'verify-windows-hosted-evidence-122'), {recursive: true})
  assert.match(compareDirectories(current, prior).lanes[2].problems.join(), /prior observation missing/)
})

test('complete per-host observations reject adversarial identity types and profiles on either side', () => {
  const profiles = [
    ['verify-macos', 'darwin', 'arm64', 'eng/baselines/host-test-baseline.macos.json'],
    ['verify-linux', 'linux', 'x64', 'eng/baselines/host-test-baseline.ubuntu.json'],
    ['verify-windows-hosted', 'win32', 'x64', 'eng/baselines/host-test-baseline.json'],
  ]
  for (const [lane, os, architecture, baseline] of profiles) {
    const manifest = fixtureInputs()
    manifest.platform = {os, architecture, release: 'observed-release'}
    manifest.selection.hostBaseline = baseline
    const observation = {candidateSha: 'a'.repeat(40), inputs: manifest, fingerprint: fingerprint(manifest)}
    assert.equal(compareObservations(observation, observation, lane).completeInputs, true, lane)
    const changes = [
      ['candidate-tree array', m => {m.candidateTree = ['a'.repeat(40)]}],
      ['candidate-tree object', m => {m.candidateTree = {sha: 'a'.repeat(40)}}],
      ['candidate-tree number', m => {m.candidateTree = 123}],
      ['repository array', m => {m.repository = ['Harborline-Software/harborline-api']}],
      ['lane array', m => {m.lane = ['host']}],
      ['wrong lane', m => {m.lane = 'shared'}],
      ['OS array', m => {m.platform.os = [os]}],
      ['wrong OS', m => {m.platform.os = os === 'linux' ? 'win32' : 'linux'}],
      ['architecture array', m => {m.platform.architecture = [architecture]}],
      ['wrong architecture', m => {m.platform.architecture = architecture === 'x64' ? 'arm64' : 'x64'}],
      ['baseline array', m => {m.selection.hostBaseline = [baseline]}],
      ['wrong supported baseline', m => {m.selection.hostBaseline = baseline === profiles[2][3] ? profiles[0][3] : profiles[2][3]}],
      ['selection array', m => {m.selection.host = ['Lane!=perf']}],
      ['coverage string', m => {m.coverage.enabled = 'false'}],
      ['quality string', m => {m.selection.quality = 'false'}],
      ['pin commit array', m => {m.pins.platform.commit = ['b'.repeat(40)]}],
      ['pin tree array', m => {m.pins.platform.tree = ['c'.repeat(40)]}],
      ['pin declared array', m => {m.pins.platform.declared = ['b'.repeat(40)]}],
      ['producer hash array', m => {m.producer.files[0].sha256 = ['3'.repeat(64)]}],
      ['native hash array', m => {m.dependencies.native[0].sha256 = ['1'.repeat(64)]}],
      ['lock hash array', m => {m.dependencies.restoredLocks[0].sha256 = ['2'.repeat(64)]}],
    ]
    const malformed = changes.map(([label, mutate]) => {
      const inputs = structuredClone(manifest); mutate(inputs)
      return [label, {...observation, inputs, fingerprint: fingerprint(inputs)}]
    })
    for (const candidateSha of [undefined, null, {}, 123, ['a'.repeat(40)], 'A'.repeat(40), 'a'.repeat(39)])
      malformed.push(['candidate SHA type/format', {...observation, candidateSha}])
    for (const [label, invalid] of malformed) for (const side of ['current', 'prior']) {
      const result = compareObservations(side === 'current' ? invalid : observation,
        side === 'prior' ? invalid : observation, lane)
      assert.equal(result.completeInputs, false, `${lane}: ${side}: ${label}`)
      assert.ok(result.problems.length > 0)
      assert.equal(result.reuseAuthorized, false)
      assert.equal(result.requiredWorkSkipped, false)
    }
  }
})

test('empty, malformed, unrecognized and duplicate directory evidence never becomes an empty successful lane set', t => {
  const root = mkdtempSync(path.join(tmpdir(), 'shadow-directory-refusal-'))
  t.after(() => rmSync(root, {recursive: true, force: true}))
  const current = path.join(root, 'current'), prior = path.join(root, 'prior')
  mkdirSync(current); mkdirSync(prior)
  const empty = compareDirectories(current, prior)
  assert.equal(empty.evidenceState, 'unknown')
  assert.equal(empty.lanes.length, 3)
  for (const lane of empty.lanes) {
    assert.deepEqual(lane.problems, ['current observation missing or corrupt', 'prior observation missing or corrupt'])
    assert.equal(lane.completeInputs, false); assert.equal(lane.wouldReuse, false)
  }
  const folder = path.join(current, 'verify-macos-evidence-123')
  mkdirSync(folder)
  writeFileSync(path.join(folder, 'validation-inputs-shadow.json'), 'malformed JSON')
  assert.match(compareDirectories(current, prior).currentProblems.join(), /unreadable or malformed/)
  const unknown = path.join(current, 'unrecognized-evidence-123'); mkdirSync(unknown)
  writeFileSync(path.join(unknown, 'validation-inputs-shadow.json'), '{}')
  assert.match(compareDirectories(current, prior).currentProblems.join(), /unrecognized/)
  const duplicate = path.join(current, 'verify-macos-evidence-124'); mkdirSync(duplicate)
  writeFileSync(path.join(duplicate, 'validation-inputs-shadow.json'), '{}')
  assert.throws(() => compareDirectories(current, prior), /duplicate lane observations/)
})
