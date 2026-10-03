import test from 'node:test'
import assert from 'node:assert/strict'
import {digest, fingerprint} from '../validation-reuse.mjs'
import {transportProblems, createGitHubClient, observeRun, compareRuns, readArchive} from '../validation-github-shadow.mjs'
import {execFileSync} from 'node:child_process'

const archive = Buffer.from('archive fixture')
const run = {id: 123, run_attempt: 1, repository: {full_name: 'Harborline-Software/harborline-api'},
  head_repository: {full_name: 'Harborline-Software/harborline-api'}, head_sha: 'a'.repeat(40),
  path: '.github/workflows/verify.yml', event: 'pull_request', status: 'completed', conclusion: 'success'}
const job = {id: 456, name: 'verify-macos', run_id: 123, run_attempt: 1, status: 'completed', conclusion: 'success',
  steps: [{name: "Host lane (exact-clone against this host's baseline)", status: 'completed', conclusion: 'success'}]}
const artifact = {id: 789, name: 'verify-macos-evidence-123', workflow_run: {id: 123, head_sha: 'a'.repeat(40)},
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
  dependencies: {}, producer: {}, toolchain: {}, platform: {}, pins: {}, selection: {}, coverage: {enabled: false},
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
