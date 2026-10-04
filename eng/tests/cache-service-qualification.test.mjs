import assert from 'node:assert/strict'
import test from 'node:test'
import {exercise, qualificationContext, serviceClient} from '../cache-service-qualification.mjs'

const namespace = 'api356-synthetic-123456-0123456789abcdef0123456789abcdef'
const env = mode => ({INPUT_MODE: mode, INPUT_NAMESPACE: namespace, GITHUB_RUN_ID: '123456',
  GITHUB_REPOSITORY: 'Harborline-Software/harborline-api', GITHUB_REPOSITORY_ID: '123',
  GITHUB_EVENT_NAME: ['seed', 'read'].includes(mode) ? 'workflow_dispatch' : 'pull_request',
  GITHUB_REF: ['seed', 'read'].includes(mode) ? 'refs/heads/main' : 'refs/pull/9/merge',
  GITHUB_WORKFLOW_REF: 'Harborline-Software/harborline-api/.github/workflows/cache-service-qualification.yml@refs/heads/main'})
const event = {pull_request: {body: `Cache qualification namespace: ${namespace}`}}

test('roles require real main or PR merge-ref context and disposable namespaces', () => {
  assert.equal(qualificationContext(env('read'), {}).namespace, namespace)
  assert.equal(qualificationContext(env('attack'), event).namespace, namespace)
  assert.match(qualificationContext(env('seed'), {}).namespace, /^api356-synthetic-123456-[a-f0-9]{32}$/)
  for (const change of [{GITHUB_REF: 'refs/heads/feature'}, {GITHUB_EVENT_NAME: 'pull_request_target'},
    {GITHUB_WORKFLOW_REF: 'attacker/workflow@refs/heads/main'}, {GITHUB_REPOSITORY: 'attacker/fork'},
    {INPUT_NAMESPACE: 'production-package-cache'}, {INPUT_NAMESPACE: `${namespace}\nforged=value`}])
    assert.throws(() => qualificationContext({...env('read'), ...change}, {}))
  assert.throws(() => qualificationContext({...env('attack'), GITHUB_REF: 'refs/heads/main'}, event))
  assert.throws(() => qualificationContext(env('attack'), {pull_request: {body: 'namespace missing'}}))
})

function fixture({overrideMain = false, deniedStatus = 403} = {}) {
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
    if (readOnly) return {status: deniedStatus, ok: false}
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
  assert.deepEqual(result, {status: 403, ok: false, upload: undefined, download: undefined})
  assert.equal(requests[0].options.headers.Authorization, 'Bearer synthetic-test-token')
  await assert.rejects(client('CreateCacheEntry', {...request, key: 'trusted-production-key'}), /outside synthetic/)
  await assert.rejects(client('DeleteCacheEntry', request), /outside synthetic/)
  assert.throws(() => serviceClient({ACTIONS_RESULTS_URL: 'https://attacker.example', ACTIONS_RUNTIME_TOKEN: 'x'}), /unavailable/)
})
