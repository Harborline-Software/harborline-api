// Test-only cache v2 protocol controls. No package caches, secrets, or release outputs.
// Request schema: actions/toolkit packages/cache/src/generated/results/{api,entities}/v1.
import {createHash, randomBytes} from 'node:crypto'
import {appendFileSync, mkdirSync, writeFileSync} from 'node:fs'
import path from 'node:path'

class QualificationFailure extends Error {}

const repository = 'Harborline-Software/harborline-api'
const literal = 'Harborline synthetic main fixture v1\n'
const altered = 'Harborline synthetic candidate fixture v1\n'
const version = createHash('sha256').update('api356-synthetic-cache-v1-linux-x64').digest('hex')
const namespacePattern = /^api356-synthetic-[1-9][0-9]{0,19}-[a-f0-9]{32}$/

export function validateSyntheticNamespace(namespace) {
  if (typeof namespace !== 'string' || !namespacePattern.test(namespace))
    throw new QualificationFailure('disposable synthetic namespace required')
  return namespace
}

export function qualificationContext(env) {
  const mode = env.INPUT_MODE
  if (!['seed', 'read', 'attack', 'deny'].includes(mode) || env.GITHUB_REPOSITORY !== repository)
    throw new QualificationFailure('unsupported qualification context')
  const main = env.GITHUB_EVENT_NAME === 'workflow_dispatch' && env.GITHUB_REF === 'refs/heads/main'
    && env.GITHUB_WORKFLOW_REF === `${repository}/.github/workflows/cache-service-qualification.yml@refs/heads/main`
  const pr = env.GITHUB_EVENT_NAME === 'pull_request' && /^refs\/pull\/[1-9][0-9]*\/merge$/.test(env.GITHUB_REF)
  if ((['seed', 'read'].includes(mode) && !main) || (['attack', 'deny'].includes(mode) && !pr))
    throw new QualificationFailure('qualification role does not match service namespace context')
  const namespace = validateSyntheticNamespace(mode === 'seed'
    ? `api356-synthetic-${env.GITHUB_RUN_ID}-${randomBytes(16).toString('hex')}` : env.INPUT_NAMESPACE)
  return {mode, namespace, ref: env.GITHUB_REF}
}

export function serviceClient(env, fetcher = fetch) {
  const base = new URL(env.ACTIONS_RESULTS_URL || 'https://invalid.example')
  if (base.protocol !== 'https:' || !base.hostname.endsWith('.actions.githubusercontent.com')
    || base.username || base.password || !env.ACTIONS_RUNTIME_TOKEN)
    throw new QualificationFailure('cache v2 service unavailable')
  return async (method, request) => {
    if (!['CreateCacheEntry', 'FinalizeCacheEntryUpload', 'GetCacheEntryDownloadURL'].includes(method)
      || !/^api356-synthetic-[1-9][0-9]{0,19}-[a-f0-9]{32}-(shared|read-positive|pr-only|override|denied)$/.test(request.key)
      || request.version !== version) throw new QualificationFailure('request outside synthetic test namespace')
    const response = await fetcher(new URL(`/twirp/github.actions.results.api.v1.CacheService/${method}`, base), {
      method: 'POST', redirect: 'error', signal: AbortSignal.timeout(30000),
      headers: {'Content-Type': 'application/json', Authorization: `Bearer ${env.ACTIONS_RUNTIME_TOKEN}`},
      body: JSON.stringify(request)
    })
    const data = await response.json()
    if (!Number.isInteger(response.status) || response.status < 100 || response.status > 599
      || typeof response.ok !== 'boolean' || response.ok !== (response.status >= 200 && response.status < 300)
      || data === null || typeof data !== 'object' || Array.isArray(data)
      || (Object.hasOwn(data, 'ok') && typeof data.ok !== 'boolean'))
      throw new QualificationFailure('malformed cache service response')
    const field = (snake, camel) => {
      for (const name of [snake, camel]) if (Object.hasOwn(data, name) && typeof data[name] !== 'string')
        throw new QualificationFailure('malformed cache service response')
      if (Object.hasOwn(data, snake) && Object.hasOwn(data, camel) && data[snake] !== data[camel])
        throw new QualificationFailure('conflicting cache service response aliases')
      return data[snake] ?? data[camel]
    }
    const upload = field('signed_upload_url', 'signedUploadUrl')
    const download = field('signed_download_url', 'signedDownloadUrl')
    const matched = field('matched_key', 'matchedKey')
    if ((Object.hasOwn(data, 'message') && (typeof data.message !== 'string' || data.message.length > 4096))
      || ['code', 'msg', 'error'].some(name => Object.hasOwn(data, name)))
      throw new QualificationFailure('malformed or error cache service response')
    const message = data.message || ''
    if (data.ok === true) {
      if (!response.ok || message || (method === 'CreateCacheEntry' && !upload)
        || (method === 'GetCacheEntryDownloadURL' && (!download || matched !== request.key)))
        throw new QualificationFailure('incomplete or mismatched cache service success')
    } else if (upload || download || matched) throw new QualificationFailure('cache service miss contains success fields')
    if (method === 'GetCacheEntryDownloadURL' && data.ok !== true && message)
      throw new QualificationFailure('cache lookup returned an error; no isolation verdict')
    // Official saveCacheV2 classifies this stable service prefix as CacheWriteDeniedError.
    // Generic false reservations, HTTP errors and omitted messages prove no permission denial.
    const writeDenied = method === 'CreateCacheEntry' && response.status === 200
      && data.ok !== true && message.startsWith('cache write denied:')
    // Raw messages, signed URLs and credentials never enter evidence or errors.
    return {status: response.status, ok: response.ok && data.ok === true, upload, download, writeDenied}
  }
}

function blobURL(value) {
  const url = new URL(value)
  if (url.protocol !== 'https:' || url.username || url.password || !url.hostname.endsWith('.blob.core.windows.net'))
    throw new QualificationFailure('unsupported cache blob endpoint')
  return url
}

export async function exercise(context, env, rpc, fetcher = fetch) {
  const evidence = {schemaVersion: 1, mode: context.mode, namespace: context.namespace,
    ref: context.ref, runId: env.GITHUB_RUN_ID, workflowSha: env.GITHUB_WORKFLOW_SHA,
    declaredServiceMode: env.ACTIONS_CACHE_MODE || null, operations: [], reuseAuthorized: false}
  const key = suffix => `${context.namespace}-${suffix}`
  const call = async (method, suffix, extra = {}) => {
    const result = await rpc(method, {key: key(suffix), version, ...extra})
    evidence.operations.push({method, suffix, status: result.status, ok: result.ok,
      ...(result.writeDenied ? {refusal: 'write-denied'} : {})})
    if (env.GITHUB_ACTIONS === 'true') console.log(JSON.stringify(evidence.operations.at(-1)))
    return result
  }
  const read = async suffix => {
    const result = await call('GetCacheEntryDownloadURL', suffix, {restore_keys: []})
    if (!result.ok) {
      if (result.status !== 200) throw new QualificationFailure('cache lookup failed; no isolation verdict')
      return null
    }
    const response = await fetcher(blobURL(result.download), {redirect: 'error', signal: AbortSignal.timeout(30000)})
    if (!response.ok) throw new QualificationFailure('synthetic fixture download failed')
    const chunks = []; let size = 0
    for await (const chunk of response.body) {
      size += chunk.byteLength
      if (size > 128) throw new QualificationFailure('synthetic fixture exceeds size bound')
      chunks.push(Buffer.from(chunk))
    }
    return Buffer.concat(chunks).toString('utf8')
  }
  const write = async (suffix, bytes, extra = {}) => {
    const result = await call('CreateCacheEntry', suffix, extra)
    if (!result.ok) throw new QualificationFailure('synthetic write control unavailable')
    const upload = await fetcher(blobURL(result.upload), {method: 'PUT', redirect: 'error',
      signal: AbortSignal.timeout(30000), headers: {'x-ms-blob-type': 'BlockBlob', 'x-ms-version': '2023-11-03'}, body: bytes})
    if (!upload.ok) throw new QualificationFailure('synthetic fixture upload failed')
    if (!(await call('FinalizeCacheEntryUpload', suffix, {size_bytes: String(Buffer.byteLength(bytes)), ...extra})).ok)
      throw new QualificationFailure('synthetic fixture finalization failed')
  }
  if (context.mode === 'seed') {
    await write('shared', literal)
    await write('read-positive', literal)
  } else if (context.mode === 'read') {
    if (await read('shared') !== literal) throw new QualificationFailure('main fixture changed or unavailable')
    if (await read('pr-only') !== null || await read('override') !== null || await read('denied') !== null)
      throw new QualificationFailure('candidate entry visible to protected main')
  } else {
    if (await read(context.mode === 'deny' ? 'read-positive' : 'shared') !== literal) throw new QualificationFailure('main fixture read positive control failed')
    // Bypass both a restore-only action and the client-side mode check.
    env.ACTIONS_CACHE_MODE = 'write'
    if (context.mode === 'deny') {
      if (evidence.declaredServiceMode !== 'read') throw new QualificationFailure('read-only service mode required')
      if (await read('denied') !== null) throw new QualificationFailure('denial key already exists; collision control failed')
      const result = await call('CreateCacheEntry', 'denied')
      if (result.status !== 200 || result.ok || result.writeDenied !== true)
        throw new QualificationFailure('service read-only write denial not established')
      if (await read('denied') !== null) throw new QualificationFailure('denied reservation became visible')
      evidence.writeDenialEstablished = true
    } else {
      await write('shared', altered)
      await write('pr-only', altered)
      // The protocol has explicit metadata scope fields. Attempt main scope with the PR token.
      const metadata = {repository_id: env.GITHUB_REPOSITORY_ID,
        scope: [{scope: 'refs/heads/main', permission: '3'}]}
      const result = await call('CreateCacheEntry', 'override', {metadata})
      if (result.ok) {
        const upload = await fetcher(blobURL(result.upload), {method: 'PUT', redirect: 'error',
          signal: AbortSignal.timeout(30000), headers: {'x-ms-blob-type': 'BlockBlob'}, body: altered})
        if (!upload.ok || !(await call('FinalizeCacheEntryUpload', 'override',
          {metadata, size_bytes: String(Buffer.byteLength(altered))})).ok)
          throw new QualificationFailure('scope override control could not finalize')
        evidence.scopeOverrideAccepted = true // Fresh main reader decides visibility; acceptance alone proves nothing.
      } else if (![400, 403].includes(result.status)) throw new QualificationFailure('scope override control inconclusive')
      if (await read('shared') !== altered || await read('pr-only') !== altered)
        throw new QualificationFailure('candidate write positive control failed')
    }
  }
  evidence.completed = true
  return evidence
}

if ((process.argv[1] || '').replaceAll('\\', '/').endsWith('/cache-service-qualification.mjs')) {
  try {
    const env = process.env
    const context = qualificationContext(env)
    const result = await exercise(context, env, serviceClient(env))
    const directory = path.resolve('.claude/cache-service-qualification')
    mkdirSync(directory, {recursive: true})
    writeFileSync(path.join(directory, 'evidence.json'), `${JSON.stringify(result, null, 2)}\n`)
    appendFileSync(env.GITHUB_OUTPUT, `namespace=${context.namespace}\n`)
    console.log(JSON.stringify(result))
  } catch (error) {
    console.error(error instanceof QualificationFailure ? error.message : 'synthetic cache qualification unavailable; credential details suppressed')
    process.exitCode = 1
  }
}
