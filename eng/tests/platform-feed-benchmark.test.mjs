import assert from 'node:assert/strict'
import test from 'node:test'
import {readFileSync} from 'node:fs'
import {benchmarkWork, compareMeasurements, measureCase} from '../platform-feed-benchmark.mjs'
import {benchmarkCache} from '../platform-feed-benchmark-cache.mjs'

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

const rows = () => ['cold', 'warm', 'forcedmiss'].map((mode, index) => ({mode, route: index === 1 ? 'cached' : 'fresh',
  cacheState: index === 2 ? 'miss' : 'hit', inputFingerprint: 'literal-input', totalMs: [100, 110, 120][index],
  validationReused: false, fullBuild: {passed: true, workDigest: 'literal-full-work'}}))
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
  const workflow = readFileSync(new URL('../../.github/workflows/platform-feed-qualification.yml', import.meta.url), 'utf8')
  const job = workflow.slice(workflow.indexOf('  benchmark-linux:'))
  assert.match(job, /if: github.event_name == 'workflow_dispatch' && inputs.benchmark/)
  assert.match(job, /pair: \[1, 2, 3\]/)
  assert.match(job, /cache-mode: write/)
  assert.doesNotMatch(job, /github\.event\.pull_request|secrets\.|GH_TOKEN|GITHUB_TOKEN|@v[0-9]/)
  assert.match(job, /persist-credentials: false/)
  const action = readFileSync(new URL('../../.github/actions/platform-feed-benchmark/action.yml', import.meta.url), 'utf8')
  assert.match(action, /using: node24/)
  assert.match(action, /main: \.\.\/\.\.\/\.\.\/eng\/platform-feed-benchmark\.mjs/)
})
