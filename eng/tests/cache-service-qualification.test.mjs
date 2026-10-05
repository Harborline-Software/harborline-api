import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import test from 'node:test'
import {exercise, qualificationContext, serviceClient} from '../cache-service-qualification.mjs'
import {probeWorkflow} from '../cache-service-probe.mjs'

const namespace = 'api356-synthetic-123456-0123456789abcdef0123456789abcdef'
const env = mode => ({INPUT_MODE: mode, INPUT_NAMESPACE: namespace, GITHUB_RUN_ID: '123456',
  ACTIONS_CACHE_MODE: ['read', 'deny'].includes(mode) ? 'read' : 'write',
  GITHUB_REPOSITORY: 'Harborline-Software/harborline-api', GITHUB_REPOSITORY_ID: '123',
  GITHUB_EVENT_NAME: ['seed', 'read'].includes(mode) ? 'workflow_dispatch' : 'pull_request',
  GITHUB_REF: ['seed', 'read'].includes(mode) ? 'refs/heads/main' : 'refs/pull/9/merge',
  GITHUB_WORKFLOW_REF: 'Harborline-Software/harborline-api/.github/workflows/cache-service-qualification.yml@refs/heads/main'})

test('roles require real main or PR merge-ref context and disposable namespaces', () => {
  assert.equal(qualificationContext(env('read')).namespace, namespace)
  assert.equal(qualificationContext(env('attack')).namespace, namespace)
  assert.match(qualificationContext(env('seed')).namespace, /^api356-synthetic-123456-[a-f0-9]{32}$/)
  for (const change of [{GITHUB_REF: 'refs/heads/feature'}, {GITHUB_EVENT_NAME: 'pull_request_target'},
    {GITHUB_WORKFLOW_REF: 'attacker/workflow@refs/heads/main'}, {GITHUB_REPOSITORY: 'attacker/fork'},
    {INPUT_NAMESPACE: 'production-package-cache'}, {INPUT_NAMESPACE: `${namespace}\nforged=value`}])
    assert.throws(() => qualificationContext({...env('read'), ...change}))
  assert.throws(() => qualificationContext({...env('attack'), GITHUB_REF: 'refs/heads/main'}))
  assert.throws(() => qualificationContext({...env('attack'), INPUT_NAMESPACE: ''},
    {pull_request: {body: `Cache qualification namespace: ${namespace}`}}))
})

test('permanent workflow is main-dispatch only; disposable probe runs immutable main action without candidate code', () => {
  const main = readFileSync(new URL('../../.github/workflows/cache-service-qualification.yml', import.meta.url), 'utf8')
  assert.match(main, /^  workflow_dispatch:/m)
  assert.doesNotMatch(main, /pull_request|github\.event\.pull_request\.body|mode: (attack|deny)/)
  assert.equal((main.match(/ref: \$\{\{ github.workflow_sha \}\}/g) || []).length, 2)
  const probe = probeWorkflow({trustedSha: 'a'.repeat(40), namespace})
  assert.equal((probe.match(/uses: Harborline-Software\/harborline-api\/\.github\/actions\/cache-qualification@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/g) || []).length, 2)
  assert.match(probe, /^  pull_request:/m)
  assert.match(probe, /cache-mode: write/)
  assert.match(probe, /cache-mode: read/)
  assert.doesNotMatch(probe, /uses: actions\/checkout|uses: \.\/|\brun:|pull_request\.body/)
  assert.doesNotMatch(probe, /permissions:\n\s+[^\n]*write/)
  for (const change of [{trustedSha: 'main'}, {trustedSha: 'a'.repeat(39)}, {namespace: 'existing-package-cache'},
    {trustedSha: 'a'.repeat(40) + '\n    run: unreviewed'}])
    assert.throws(() => probeWorkflow({trustedSha: 'a'.repeat(40), namespace, ...change}))
})

function fixture({overrideMain = false, deniedStatus = 200} = {}) {
  const stores = new Map([['main', new Map()], ['pr', new Map()]])
  const pending = new Map()
  let sequence = 0
  const client = (scope, readOnly = false) => async (method, request) => {
    const target = overrideMain && request.metadata ? 'main' : scope
    if (method === 'GetCacheEntryDownloadURL') {
      const bytes = stores.get(scope).get(request.key) ?? stores.get('main').get(request.key)
      if (bytes === undefined) return {status: 200, ok: false}
      const id = String(++sequence); pending.set(id, {bytes})
      return {status: 200, ok: true, download: `https://fixture.blob.core.windows.net/${id}`}
    }
    if (readOnly) return {status: deniedStatus, ok: false, writeDenied: true}
    if (method === 'CreateCacheEntry') {
      const id = String(++sequence); pending.set(id, {target, key: request.key})
      return {status: 200, ok: true, upload: `https://fixture.blob.core.windows.net/${id}`}
    }
    return {status: 200, ok: true}
  }
  const fetcher = async (url, options) => {
    const entry = pending.get(new URL(url).pathname.slice(1))
    if (options.method === 'PUT') stores.get(entry.target).set(entry.key, options.body)
    const bytes = Buffer.from(entry.bytes || '')
    return {ok: true, body: (async function* () { yield bytes })()}
  }
  return {client, fetcher, stores}
}

test('actual harness seeds literal bytes, exercises candidate writes, and independently reads main', async () => {
  const f = fixture()
  const run = mode => exercise({mode, namespace, ref: env(mode).GITHUB_REF}, env(mode),
    f.client(['seed', 'read'].includes(mode) ? 'main' : 'pr', mode === 'deny'), f.fetcher)
  await run('seed')
  assert.equal(f.stores.get('main').get(`${namespace}-shared`), 'Harborline synthetic main fixture v1\n')
  await run('attack')
  await run('deny') // Separate positive-read key avoids the deliberate PR shadow.
  const result = await run('read')
  assert.equal(result.completed, true)
  assert.equal(result.reuseAuthorized, false)
  assert.equal(f.stores.get('pr').get(`${namespace}-shared`), 'Harborline synthetic candidate fixture v1\n')
  assert.equal(f.stores.get('main').has(`${namespace}-pr-only`), false)
})

test('main reader kills service scope override even when PR job reports successful writes', async () => {
  const f = fixture({overrideMain: true})
  for (const mode of ['seed', 'attack']) await exercise({mode, namespace}, env(mode),
    f.client(mode === 'seed' ? 'main' : 'pr'), f.fetcher)
  await assert.rejects(exercise({mode: 'read', namespace}, env('read'), f.client('main'), f.fetcher),
    /candidate entry visible to protected main/)
})

test('failed positive read or malformed API response cannot become an isolation PASS', async () => {
  await assert.rejects(exercise({mode: 'attack', namespace}, env('attack'),
    async () => ({status: 403, ok: false})), /no isolation verdict/)
  const f = fixture({deniedStatus: 400})
  await exercise({mode: 'seed', namespace}, env('seed'), f.client('main'), f.fetcher)
  await assert.rejects(exercise({mode: 'deny', namespace}, env('deny'), f.client('pr', true), f.fetcher),
    /read-only write denial not established/)
})

test('oversized downloaded fixture is refused before further stream chunks are consumed', async () => {
  let consumed = 0
  const fetcher = async () => ({ok: true, body: (async function* () {
    consumed++; yield Buffer.alloc(129)
    consumed++; yield Buffer.alloc(129)
  })()})
  await assert.rejects(exercise({mode: 'read', namespace}, env('read'),
    async () => ({status: 200, ok: true, download: 'https://fixture.blob.core.windows.net/object'}), fetcher),
    /exceeds size bound/)
  assert.equal(consumed, 1)
})

test('real protocol client bounds keys and methods and never returns token or response messages', async () => {
  const requests = []
  const client = serviceClient({ACTIONS_RESULTS_URL: 'https://results.actions.githubusercontent.com/',
    ACTIONS_RUNTIME_TOKEN: 'synthetic-test-token'}, async (url, options) => {
    requests.push({url: String(url), options})
    return {status: 403, ok: false, json: async () => ({message: 'private response details', ok: false})}
  })
  const f = fixture()
  let request
  await exercise({mode: 'seed', namespace}, env('seed'), async (method, body) => {
    request = body
    return f.client('main')(method, body)
  }, f.fetcher)
  const result = await client('CreateCacheEntry', request)
  assert.deepEqual(result, {status: 403, ok: false, upload: undefined, download: undefined, writeDenied: false})
  assert.equal(requests[0].options.headers.Authorization, 'Bearer synthetic-test-token')
  await assert.rejects(client('CreateCacheEntry', {...request, key: 'trusted-production-key'}), /outside synthetic/)
  await assert.rejects(client('DeleteCacheEntry', request), /outside synthetic/)
  assert.throws(() => serviceClient({ACTIONS_RESULTS_URL: 'https://attacker.example', ACTIONS_RUNTIME_TOKEN: 'x'}), /unavailable/)
})

async function mainReadResponse(missResponse) {
  const client = serviceClient({ACTIONS_RESULTS_URL: 'https://results.actions.githubusercontent.com/',
    ACTIONS_RUNTIME_TOKEN: 'synthetic-test-token'}, async (_url, options) => {
    const request = JSON.parse(options.body)
    const data = request.key.endsWith('-shared') ? {ok: true,
      signed_download_url: 'https://fixture.blob.core.windows.net/original', matched_key: request.key} : missResponse
    return {status: 200, ok: true, json: async () => data}
  })
  const fetcher = async () => ({ok: true, body: (async function* () {
    yield Buffer.from('Harborline synthetic main fixture v1\n')
  })()})
  return exercise({mode: 'read', namespace}, env('read'), client, fetcher)
}

test('composed main read rejects malformed ok and response bodies instead of passing their lookups as misses', async () => {
  for (const ok of ['true', 'false', 0, 1, null, {}, []])
    await assert.rejects(mainReadResponse({ok}), /malformed cache service response/)
  for (const data of [null, [], 'not a response', 0])
    await assert.rejects(mainReadResponse(data), /malformed cache service response/)
})

test('composed main read preserves explicit false and omitted proto3 defaults as legitimate misses', async () => {
  for (const data of [{ok: false}, {}, {signed_download_url: '', matched_key: ''},
    {ok: false, signedDownloadUrl: '', matchedKey: ''}])
    assert.equal((await mainReadResponse(data)).completed, true)
})

test('composed main read rejects conflicting, untyped or mismatched lookup success fields', async () => {
  for (const data of [{ok: true}, {ok: true, signed_download_url: 123},
    {ok: false, signed_download_url: 'https://fixture.blob.core.windows.net/changed'},
    {ok: false, signed_download_url: '', signedDownloadUrl: 'different'},
    {ok: true, signed_download_url: 'https://fixture.blob.core.windows.net/changed', matched_key: 'wrong-key'}])
    await assert.rejects(mainReadResponse(data), /cache service/)
})

async function denialResponse({data = {ok: false, message: 'cache write denied: synthetic private detail'},
  status = 200, before = false, after = false, positive = true, throws = false, mode = 'read'} = {}) {
  let attempted = false
  const requests = []
  const client = serviceClient({ACTIONS_RESULTS_URL: 'https://results.actions.githubusercontent.com/',
    ACTIONS_RUNTIME_TOKEN: 'synthetic-test-token'}, async (url, options) => {
    const request = JSON.parse(options.body)
    const create = String(url).endsWith('/CreateCacheEntry')
    requests.push([create ? 'create' : 'read', request.key.endsWith('-read-positive') ? 'positive' : 'denied'])
    if (create) {
      attempted = true
      if (throws) throw new Error('synthetic transport failure')
      return {status, ok: status >= 200 && status < 300, json: async () => data}
    }
    const found = request.key.endsWith('-read-positive') ? positive : attempted ? after : before
    return {status: 200, ok: true, json: async () => found ? {ok: true,
      signed_download_url: 'https://fixture.blob.core.windows.net/original', matched_key: request.key} : {}}
  })
  const fetcher = async () => ({ok: true, body: (async function* () {
    yield Buffer.from('Harborline synthetic main fixture v1\n')
  })()})
  const evidence = await exercise({mode: 'deny', namespace}, {...env('deny'), ACTIONS_CACHE_MODE: mode}, client, fetcher)
  return {evidence, requests}
}

test('composed denial requires official policy refusal, positive read and absence before and after; evidence stays private', async () => {
  // External oracle: actions/toolkit saveCacheV2 recognizes the literal cache write denied: prefix.
  for (const data of [{ok: false, message: 'cache write denied: synthetic private detail'},
    {message: 'cache write denied: synthetic private detail'}]) {
    const {evidence, requests} = await denialResponse({data})
    assert.equal(evidence.writeDenialEstablished, true)
    assert.equal(evidence.reuseAuthorized, false)
    assert.deepEqual(requests, [['read', 'positive'], ['read', 'denied'], ['create', 'denied'], ['read', 'denied']])
    assert.deepEqual(evidence.operations[2], {method: 'CreateCacheEntry', suffix: 'denied', status: 200,
      ok: false, refusal: 'write-denied'})
    assert.doesNotMatch(JSON.stringify(evidence), /synthetic private detail|synthetic-test-token|blob\.core/)
  }
})

test('allowed writes, collisions, generic false and transport errors cannot pass the denial control', async () => {
  for (const data of [{ok: true, signed_upload_url: 'https://fixture.blob.core.windows.net/allowed'},
    {ok: false}, {}, {ok: false, message: 'already exists'}, {ok: false, message: 'rate limited'},
    {ok: false, message: 'unavailable'}, {ok: false, message: 'prefix cache write denied: misleading'},
    {ok: true, signed_upload_url: 'https://fixture.blob.core.windows.net/allowed', message: 'cache write denied:'}])
    await assert.rejects(denialResponse({data}), /not established|cache service success/)
  for (const status of [400, 403, 408, 409, 429, 500, 503])
    await assert.rejects(denialResponse({status}), /not established/)
  await assert.rejects(denialResponse({throws: true}), /transport failure/)
  await assert.rejects(denialResponse({positive: false}), /positive control failed/)
  await assert.rejects(denialResponse({before: true}), /collision control failed/)
  await assert.rejects(denialResponse({after: true}), /became visible/)
  await assert.rejects(denialResponse({mode: 'write'}), /read-only service mode required/)
})

test('composed denial rejects malformed raw booleans, error bodies and response messages', async () => {
  for (const ok of ['false', 'true', null, 0, 1, {}, []])
    await assert.rejects(denialResponse({data: {ok, message: 'cache write denied:'}}), /malformed/)
  for (const message of [null, 1, {}, [], 'x'.repeat(4097)])
    await assert.rejects(denialResponse({data: {ok: false, message}}), /malformed/)
  for (const data of [null, [], {ok: false, message: 'cache write denied:', code: 'resource_exhausted'},
    {ok: false, message: 'cache write denied:', signed_upload_url: 'https://fixture.blob.core.windows.net/allowed'}])
    await assert.rejects(denialResponse({data}), /malformed|success fields/)
  for (const data of [{ok: false, message: 'cache read denied: unavailable'}, {code: 'internal', msg: 'unavailable'}])
    await assert.rejects(mainReadResponse(data), /error/)
})
