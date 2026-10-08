import assert from 'node:assert/strict'
import test from 'node:test'
import {mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {benchmarkDiagnostics, benchmarkWork, compareMeasurements, failureIdentity, measureCase, readBoundaryTail, runBoundaryDiagnostic} from '../platform-feed-benchmark.mjs'
import {benchmarkCache} from '../platform-feed-benchmark-cache.mjs'
import {safeFailure} from '../platform-feed-qualification.mjs'

const input = {source: 'literal source identity'}
const key = 'api356-benchmark-123456-1-1-' + 'a'.repeat(64) + '-bundle'
const env = {ACTIONS_RESULTS_URL: 'https://results.actions.githubusercontent.com/', ACTIONS_RUNTIME_TOKEN: 'private-token'}
function options(mode, cached = null) {
  const calls = [], bytes = Buffer.from('literal compiled dependency fixture')
  return {calls, args: {mode, expectedDigest: 'independent cold digest',
    prepare: () => ({input, pack: () => {calls.push('pack'); return [{name: 'fixture', bytes}]}, bundle: () => bytes}),
    cache: {get: () => {calls.push('download'); return cached}, put: () => calls.push('upload')}, key: () => key,
    validate: () => {calls.push('validate'); return [{name: 'fixture', bytes}]},
    publish: () => {calls.push('publish'); return {path: 'fixture'}},
    fullBuild: () => {calls.push('full-build'); return {passed: true, workDigest: 'literal-full-work'}}}}
}
test('actual measurement always runs full fresh work, includes transport/verification, and packs on misses or unusable entries', async () => {
  for (const mode of ['cold', 'warm', 'forcedmiss']) {
    const f = options(mode, mode === 'warm' ? Buffer.from('cached literal fixture') : null)
    const {result} = await measureCase(f.args)
    assert.deepEqual(f.calls, mode === 'cold' ? ['pack', 'upload', 'publish', 'full-build']
      : mode === 'warm' ? ['download', 'validate', 'publish', 'full-build'] : ['download', 'pack', 'publish', 'full-build'])
    assert.equal(result.validationReused, false)
    assert.ok(Object.hasOwn(result.timings, 'inputVerificationMs'))
    assert.ok(Object.hasOwn(result.timings, 'fullBuildMs'))
    assert.ok(Object.hasOwn(result.timings, mode === 'cold' ? 'cacheUploadMs' : 'cacheDownloadMs'))
  }
  for (const failure of ['download', 'validation']) {
    const f = options('warm', Buffer.from('damaged fixture'))
    if (failure === 'download') f.args.cache.get = () => {f.calls.push('download'); throw new Error('unavailable')}
    else f.args.validate = () => {f.calls.push('validate'); throw new Error('wrong digest or incompatible closure')}
    const {result} = await measureCase(f.args)
    assert.equal(result.cacheState, 'unusable'); assert.equal(result.route, 'fresh')
    assert.deepEqual(f.calls.slice(-3), ['pack', 'publish', 'full-build'])
  }
  const f = options('cold'); f.args.fullBuild = () => {throw new Error('real fresh test failed')}
  await assert.rejects(measureCase(f.args), /fresh test failed/)
})

test('failed actual measurement preserves completed timings and phase without a performance result', async () => {
  const f = options('cold'), events = []; let tick = 0
  const error = Object.assign(new Error('private-message'), {status: 1})
  f.args.clock = () => ++tick
  f.args.onFailure = row => events.push(row)
  f.args.fullBuild = () => {throw error}
  await assert.rejects(measureCase(f.args), value => value === error)
  assert.equal(events.length, 1)
  assert.equal(events[0].mode, 'cold'); assert.equal(events[0].stage, 'fullBuildMs')
  assert.equal(events[0].route, 'fresh'); assert.equal(events[0].elapsedMs, 13)
  assert.equal(events[0].timings.fullBuildMs, 1)
  assert.equal(events[0].timings.cacheUploadMs, 1)
  assert.deepEqual(events[0].failure, {kind: 'command-exit', exitCode: 1})
  assert.doesNotMatch(JSON.stringify(events), /private-message/)
  for (const thrown of [error, null, undefined]) {
    const f = options('cold'); f.args.fullBuild = () => {throw thrown}
    f.args.onFailure = () => {throw new Error('observer failed')}
    let caught = false
    try {await measureCase(f.args)} catch (actual) {caught = true; assert.equal(actual, thrown)}
    assert.equal(caught, true)
  }
})

test('actual diagnostic selection retains current stage, counts and test failure identity while excluding raw data and stale reports', () => {
  const head = 'a'.repeat(40), id = 'Harborline.Api.LocalNodeHost.Tests.LiteralTests.RealFailure'
  const proof = {apiCommit: head, status: 'FAIL', steps: [{id: 'host-baseline-match', passed: false,
    durationMs: 12, observed: {total: 7, passed: 6, failed: 1, notExecuted: 0, private: 'private-value'},
    newFailures: [id, 'https://private.example/signed?secret=private-value'],
    fullOutput: 'private-value', tail: 'private-value', problems: ['private-value']}]}
  const output = 'private-value\n[exact-clone] {"id":"dotnet-host-tests","state":"completed","elapsedMs":23,"exitCode":1,"diagnostics":{"private":"private-value"}}\n'
  assert.deepEqual(benchmarkDiagnostics(head, proof, output), {
    lastStage: {id: 'dotnet-host-tests', state: 'completed', elapsedMs: 23, exitCode: 1}, status: 'FAIL',
    stages: [{id: 'host-baseline-match', passed: false, durationMs: 12,
      observed: {total: 7, passed: 6, failed: 1, notExecuted: 0}, newFailureCount: 2,
      // SHA-256 literals calculated independently with Python hashlib, not the production helper.
      newFailures: [{digest: '67780deefcdf5c999bdaae2e27de39519d5c86bc82dfcfdc644032769ad0677a', display: id},
        {digest: 'c3ca30e1f00371d3b95de89be3541352583e788c9eb0ba49b60cdcf3aac8b3a5'}]}]})
  assert.doesNotMatch(JSON.stringify(benchmarkDiagnostics(head, proof, output)), /private-value|private\.example/)
  assert.deepEqual(benchmarkDiagnostics('b'.repeat(40), proof), {stages: []})
  assert.deepEqual(benchmarkDiagnostics(head, undefined, '[exact-clone] malformed'), {stages: []})
  assert.deepEqual(benchmarkDiagnostics(head, {apiCommit: head, status: 'FAIL', steps: [null, 3, 'bad']},
    '[exact-clone] null\n[exact-clone] 3\n[exact-clone] []'), {stages: [], status: 'FAIL'})
  const boundary = benchmarkDiagnostics(head, proof, '', 'not ok 1 - actual boundary title\n✖ another boundary title (2.1ms)\nprivate-value')
  assert.deepEqual(boundary.boundary.failedTests.map(row => row.display), ['actual boundary title', 'another boundary title'])
  assert.doesNotMatch(JSON.stringify(boundary), /private-value/)
})

test('failure identities retain Vitest and custom display names, with digest-only unsafe or unbounded identities', () => {
  assert.equal(failureIdentity('address.test.ts :: title').display, 'address.test.ts :: title')
  assert.equal(failureIdentity('A custom xUnit display name').display, 'A custom xUnit display name')
  for (const identity of ['https://private.example/?token=private-value', '/private/absolute.test.ts :: title',
    'password=private-value', 'a'.repeat(513), 'title\nprivate-value']) {
    const retained = failureIdentity(identity)
    assert.deepEqual(Object.keys(retained), ['digest'])
    assert.match(retained.digest, /^[a-f0-9]{64}$/)
  }
})

test('actual boundary capture bounds the disk read and marks partial evidence', () => {
  const directory = mkdtempSync(path.join(tmpdir(), 'benchmark-boundary-tail-')), file = path.join(directory, 'log')
  try {
    writeFileSync(file, 'private-prefix' + 'a'.repeat(300000) + '\nnot ok 1 - actual boundary title\n')
    const capture = readBoundaryTail(file)
    assert.equal(capture.partial, true); assert.equal(Buffer.byteLength(capture.output), 262144)
    assert.doesNotMatch(capture.output, /private-prefix/)
    const result = benchmarkDiagnostics('a'.repeat(40), {apiCommit: 'a'.repeat(40), status: 'FAIL', steps: []},
      '', capture.output, capture.partial)
    assert.equal(result.boundary.partial, true)
    assert.deepEqual(result.boundary.failedTests.map(row => row.display), ['actual boundary title'])
    writeFileSync(file, 'short output')
    assert.deepEqual(readBoundaryTail(file), {output: 'short output', partial: false})
  } finally {rmSync(directory, {recursive: true, force: true})}
})

const rows = () => ['cold', 'warm', 'forcedmiss'].map((mode, index) => ({mode, route: index === 1 ? 'cached' : 'fresh',
  cacheState: index === 2 ? 'miss' : 'hit', inputFingerprint: 'literal-input', totalMs: [100, 110, 120][index],
  validationReused: false, fullBuild: {passed: true, workDigest: 'literal-full-work'}}))
test('smaller boundary diagnostic materializes the fresh handoff and runs mandatory boundaries without qualifying full work', () => {
  const calls = [], env = {literal: 'environment'}, head = 'a'.repeat(40)
  const execute = (command, args, options) => {calls.push({command, args, options}); return ''}
  const result = runBoundaryDiagnostic({execute, clone: 'literal-clone', scratch: 'literal-scratch', env, head})
  assert.deepEqual(calls.map(row => [row.command, row.args]), [
    [process.execPath, ['eng/verify-preflight.mjs']],
    [process.execPath, ['eng/exact-clone-platform-feed.mjs', 'literal-clone', 'literal-scratch']],
    ['bash', ['eng/verify-boundaries.sh']]])
  for (const row of calls) assert.deepEqual(row.options, {cwd: 'literal-clone', env})
  assert.equal(result.passed, false); assert.equal(result.diagnosticOnly, true); assert.equal(result.boundaryPassed, true)
  assert.equal(Object.hasOwn(result, 'workDigest'), false)
  const data = rows(); data[1].fullBuild.diagnosticOnly = true
  assert.throws(() => compareMeasurements(data), /controls incomplete/)
})

test('smaller boundary diagnostic preserves the constituent failure and bounded safe test identities', () => {
  for (const thrown of [Object.assign(new Error('private-value'), {status: 1,
    stdout: 'not ok 1 - actual boundary title\nhttps://private.example/?token=private-value'}), null, undefined]) {
    let calls = 0, caught = false
    const execute = () => {if (++calls === 3) throw thrown}
    try {runBoundaryDiagnostic({execute, clone: 'literal-clone', scratch: 'literal-scratch', env: {}, head: 'a'.repeat(40)})}
    catch (actual) {caught = true; assert.equal(actual, thrown)}
    assert.equal(caught, true); assert.equal(calls, 3)
    if (thrown) {
      assert.deepEqual(thrown.benchmarkDiagnostics.stages.map(row => [row.id, row.passed]), [
        ['preflight', true], ['platform-feed-materialization', true], ['boundary-check', false]])
      assert.deepEqual(thrown.benchmarkDiagnostics.boundary.failedTests.map(row => row.display), ['actual boundary title'])
      assert.doesNotMatch(JSON.stringify(thrown.benchmarkDiagnostics), /private-value|private\.example/)
    }
  }
})
test('measurement refuses missing hits, fallback misses, changed inputs/work and reused verdicts; negative benefit remains negative', () => {
  assert.deepEqual(compareMeasurements(rows()), {coldMs: 100, coldConsumerMs: 100, warmMs: 110, forcedMissMs: 120, improvementPercent: -10})
  const seeded = rows(); seeded[0].timings = {cacheUploadMs: 20}
  assert.equal(compareMeasurements(seeded).improvementPercent, -37.5)
  for (const mutation of [r => r.pop(), r => r[1].route = 'fresh', r => r[2].cacheState = 'unusable',
    r => r[2].route = 'cached', r => r[1].inputFingerprint = 'changed', r => r[1].fullBuild.workDigest = 'changed',
    r => r[1].fullBuild.passed = false, r => r[1].validationReused = true, r => r[1].totalMs = NaN]) {
    const data = rows(); mutation(data); assert.throws(() => compareMeasurements(data), /controls incomplete/)
  }
})

function fullWork() {
  const head = 'a'.repeat(40)
  const proof = {status: 'PASS', apiCommit: head, steps: ['dotnet-restore', 'platform-feed-consumption', 'dotnet-build',
    'dotnet-host-tests', 'capability-contracts-tests', 'capability-tests', 'host-baseline-match', 'capability-baseline-match']
    .map(id => ({id, passed: true, durationMs: 1, observed: {total: 1, passed: 1, failed: 0, notExecuted: 0}}))}
  const inventory = {apiCommit: head, recordedAt: 'one timestamp', host: {identities: ['literal .NET test']},
    capability: {identities: ['literal capability test']}}
  return {proof, inventory, head}
}
test('actual gate evidence binds commit, every required stage and actual test identities; timestamps do not change work', () => {
  const f = fullWork(), before = benchmarkWork(f.proof, f.inventory, f.head).workDigest
  const recorded = benchmarkWork(f.proof, f.inventory, f.head)
  assert.equal(recorded.inventories.host.identityCount, 1)
  assert.deepEqual(recorded.inventories.host.counts, {total: 1, passed: 1, failed: 0, notExecuted: 0})
  f.inventory.recordedAt = 'another timestamp'
  assert.equal(benchmarkWork(f.proof, f.inventory, f.head).workDigest, before)
  f.inventory.host.identities = ['different .NET test']
  assert.notEqual(benchmarkWork(f.proof, f.inventory, f.head).workDigest, before)
  for (const mutate of [f => f.proof.status = 'FAIL', f => f.proof.apiCommit = 'b'.repeat(40),
    f => f.inventory.apiCommit = 'b'.repeat(40), f => f.proof.steps.pop(),
    f => f.proof.steps[0].passed = false, f => f.inventory.host.identities = []]) {
    const f = fullWork(); mutate(f); assert.throws(() => benchmarkWork(f.proof, f.inventory, f.head))
  }
})

test('actual disposable cache transport proves write/read, enforces exact matched key and keeps credentials private', async () => {
  const calls = []
  const client = benchmarkCache(env, async (url, options) => {
    calls.push({url: String(url), options})
    if (String(url).includes('blob.core.windows.net')) return {ok: true, body: (async function* () {yield Buffer.from('literal bytes')})()}
    const method = String(url).split('/').at(-1)
    return {status: 200, ok: true, json: async () => method === 'CreateCacheEntry'
      ? {ok: true, signed_upload_url: 'https://fixture.blob.core.windows.net/upload'}
      : method === 'GetCacheEntryDownloadURL' ? {ok: true, matched_key: key,
        signed_download_url: 'https://fixture.blob.core.windows.net/download'} : {ok: true}}
  })
  await client.put(key, Buffer.from('literal bytes'))
  assert.equal((await client.get(key)).toString(), 'literal bytes')
  assert.equal(calls[0].options.headers.Authorization, 'Bearer private-token')
  assert.equal(calls.filter(row => row.options.headers?.Authorization).length, 3)
  await assert.rejects(client.get('production-package-key'), /outside disposable/)
  for (const data of [{ok: 'false'}, {ok: 0}, {ok: null}, {ok: false, message: 'rate limit'}, {code: 'internal'},
    {ok: true, matched_key: 'wrong', signed_download_url: 'https://fixture.blob.core.windows.net/download'},
    {ok: false, signed_download_url: 'https://fixture.blob.core.windows.net/download'},
    {ok: true, signed_download_url: '', signedDownloadUrl: 'conflicting'}]) {
    const bad = benchmarkCache(env, async () => ({status: 200, ok: true, json: async () => data}))
    await assert.rejects(bad.get(key), /benchmark/)
  }
  const miss = benchmarkCache(env, async () => ({status: 200, ok: true, json: async () => ({})}))
  assert.equal(await miss.get(key), null)
  for (const status of [403, 429, 500]) {
    const bad = benchmarkCache(env, async () => ({status, ok: false, json: async () => ({ok: false})}))
    await assert.rejects(bad.get(key), /unavailable or malformed/)
  }
})

test('actual workflow restricts benchmark writers to manual dispatch and runs three pairs with pinned actions', () => {
  const mandatory = readFileSync(new URL('../verify-boundaries.sh', import.meta.url), 'utf8')
  assert.match(mandatory, /node --test "\$repo_root\/eng\/tests\/platform-feed-benchmark\.test\.mjs" \|\| exit 1/)
  const workflow = readFileSync(new URL('../../.github/workflows/platform-feed-qualification.yml', import.meta.url), 'utf8')
  const job = workflow.slice(workflow.indexOf('  benchmark-linux:'))
  assert.match(job, /if: github.event_name == 'workflow_dispatch' && inputs.benchmark/)
  assert.ok(job.includes("pair: ${{ inputs.boundary_diagnostic && fromJSON('[1]') || fromJSON('[1,2,3]') }}"))
  assert.match(workflow, /boundary_diagnostic:\n\s+description: [^\n]+\n\s+type: boolean\n\s+default: false/)
  assert.ok(job.includes('BENCHMARK_BOUNDARY_DIAGNOSTIC: ${{ inputs.boundary_diagnostic }}'))
  assert.match(job, /cache-mode: write/)
  assert.doesNotMatch(job, /github\.event\.pull_request|secrets\.|GH_TOKEN|GITHUB_TOKEN|@v[0-9]/)
  assert.match(job, /persist-credentials: false/)
  const action = readFileSync(new URL('../../.github/actions/platform-feed-benchmark/action.yml', import.meta.url), 'utf8')
  assert.match(action, /using: node24/)
  assert.match(action, /main: \.\.\/\.\.\/\.\.\/eng\/platform-feed-benchmark\.mjs/)
})

// Execute the actual exported orchestrator body with external process/command fixtures;
// no Docker, SDK build or cache service is launched by this regression.
test('actual benchmark writes bounded failure evidence for every fallible common setup operation', async () => {
  const source = readFileSync(new URL('../platform-feed-benchmark.mjs', import.meta.url), 'utf8')
  const body = source.slice(source.indexOf('export async function benchmark('), source.indexOf("\nif ((process.argv[1]"))
    .replace('export async function', 'async function').replaceAll('import.meta.dirname', 'moduleDirectory')
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor
  for (const failure of ['sdk-exit', 'sdk-mismatch', 'setup-clock', 'docker-pull', 'cache-client']) {
    const root = mkdtempSync(path.join(tmpdir(), 'benchmark-setup-evidence-'))
    const head = 'a'.repeat(40), calls = [], snapshots = []
    const profile = {sdk: '11.0.100-literal', image: 'literal-image@sha256:' + 'b'.repeat(64)}
    const privateText = 'private-value https://private.example/?token=private-value'
    const commandError = Object.assign(new Error(privateText), {status: 7, stderr: privateText + ' NU1301'})
    const execute = (command, args) => {
      calls.push(command)
      if (command === 'git') return args.includes('rev-parse') ? head : ''
      if (command === 'dotnet') {
        assert.equal(snapshots.length, 1, 'initial evidence precedes fallible SDK command')
        if (failure === 'sdk-exit') throw commandError
        return failure === 'sdk-mismatch' ? privateText : profile.sdk
      }
      assert.equal(command, 'docker')
      if (failure === 'docker-pull') throw commandError
      return ''
    }
    const run = new AsyncFunction('moduleDirectory', 'process', 'path', 'readPin', 'execFileSync',
      'buildEnvironment', 'readFileSync', 'mkdirSync', 'writeFileSync', 'benchmarkCache', 'safeFailure',
      'fixtureEnv', body + '\nreturn benchmark("literal-platform", fixtureEnv)')
    const fixtureEnv = {BENCHMARK_PAIR: '1', GITHUB_RUN_ID: '123456', GITHUB_RUN_ATTEMPT: '1',
      GITHUB_REPOSITORY: 'Harborline-Software/harborline-api', GITHUB_EVENT_NAME: 'workflow_dispatch',
      GITHUB_JOB: 'benchmark-linux', GITHUB_SHA: head,
      BENCHMARK_SETUP_STARTED_MS: failure === 'setup-clock' ? 'invalid-private-value' : String(Date.now() - 1000)}
    try {
      await assert.rejects(run(path.join(root, 'eng'), {platform: 'linux', versions: {node: '24.0.0'}, version: 'v24.0.0'},
        path, () => ({}), execute, () => ({}), () => JSON.stringify(profile),
        (directory, options) => {assert.equal(options.recursive, true); mkdirSync(directory, options)},
        (destination, text) => {snapshots.push(JSON.parse(text)); writeFileSync(destination, text)},
        benchmarkCache, safeFailure, fixtureEnv), /benchmark failed; no performance verdict/)
      const evidence = JSON.parse(readFileSync(path.join(root, '.claude/platform-feed-benchmark/evidence.json')))
      assert.equal(snapshots[0].completed, false)
      assert.deepEqual(snapshots[0].results, [])
      assert.equal(evidence.completed, false)
      assert.equal(evidence.failedSetupStage, failure.startsWith('sdk-') ? 'sdk-check' : failure)
      assert.deepEqual(evidence.results, [])
      assert.equal(evidence.apiCommit, head)
      assert.equal(evidence.runId, '123456'); assert.equal(evidence.runAttempt, '1'); assert.equal(evidence.pair, 1)
      assert.equal(evidence.reuseAuthorized, false); assert.equal(evidence.verdictReused, false)
      assert.equal(Object.hasOwn(evidence, 'measurement'), false)
      assert.deepEqual(evidence.failure, ['sdk-exit', 'docker-pull'].includes(failure)
        ? {kind: 'command-exit', exitCode: 7, diagnosticCodes: ['NU1301']} : {kind: 'validation-or-spawn'})
      assert.doesNotMatch(JSON.stringify(evidence), /private-value|private\.example/)
      assert.deepEqual(calls, ['git', 'git', 'dotnet', ...(['docker-pull', 'cache-client'].includes(failure) ? ['docker'] : [])])
    } finally {rmSync(root, {recursive: true, force: true})}
  }
})
