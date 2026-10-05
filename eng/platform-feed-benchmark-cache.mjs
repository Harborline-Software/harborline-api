// Disposable measurement transport, never a production cache authority.
import {createHash} from 'node:crypto'
const version = createHash('sha256').update('api356-benchmark-raw-bundle-v1').digest('hex')
const limit = 64 * 1024 * 1024
const blob = value => {
  const url = new URL(value)
  if (url.protocol !== 'https:' || url.username || url.password || !url.hostname.endsWith('.blob.core.windows.net'))
    throw new Error('benchmark blob endpoint refused')
  return url
}
export function benchmarkCache(env, fetcher = fetch) {
  const base = new URL(env.ACTIONS_RESULTS_URL || 'https://invalid.example')
  if (base.protocol !== 'https:' || base.username || base.password
    || !base.hostname.endsWith('.actions.githubusercontent.com') || !env.ACTIONS_RUNTIME_TOKEN)
    throw new Error('benchmark cache service unavailable')
  const rpc = async (method, key, extra = {}) => {
    if (!/^api356-benchmark-[1-9][0-9]{0,19}-[1-9][0-9]{0,9}-[1-3]-[a-f0-9]{64}-(bundle|forcedmiss)$/.test(key))
      throw new Error('benchmark key outside disposable namespace')
    const response = await fetcher(new URL(`/twirp/github.actions.results.api.v1.CacheService/${method}`, base), {
      method: 'POST', redirect: 'error', signal: AbortSignal.timeout(30000),
      headers: {'Content-Type': 'application/json', Authorization: `Bearer ${env.ACTIONS_RUNTIME_TOKEN}`},
      body: JSON.stringify({key, version, ...extra})})
    const data = await response.json()
    if (response.status !== 200 || response.ok !== true || !data || typeof data !== 'object' || Array.isArray(data)
      || (Object.hasOwn(data, 'ok') && typeof data.ok !== 'boolean')
      || (Object.hasOwn(data, 'message') && (typeof data.message !== 'string' || data.message))
      || ['code', 'msg', 'error'].some(name => Object.hasOwn(data, name)))
      throw new Error('benchmark cache reply unavailable or malformed')
    const field = (snake, camel) => {
      for (const name of [snake, camel]) if (Object.hasOwn(data, name) && typeof data[name] !== 'string')
        throw new Error('benchmark cache reply malformed')
      if (Object.hasOwn(data, snake) && Object.hasOwn(data, camel) && data[snake] !== data[camel])
        throw new Error('benchmark cache aliases conflict')
      return data[snake] ?? data[camel]
    }
    const upload = field('signed_upload_url', 'signedUploadUrl')
    const download = field('signed_download_url', 'signedDownloadUrl')
    const matched = field('matched_key', 'matchedKey')
    if (!data.ok && (upload || download || matched)) throw new Error('benchmark miss has success fields')
    if (data.ok && ((method === 'CreateCacheEntry' && !upload)
      || (method === 'GetCacheEntryDownloadURL' && (!download || matched !== key))))
      throw new Error('benchmark cache success incomplete')
    return {ok: data.ok === true, upload, download}
  }
  return {
    async get(key) {
      const result = await rpc('GetCacheEntryDownloadURL', key, {restore_keys: []})
      if (!result.ok) return null
      const response = await fetcher(blob(result.download), {redirect: 'error', signal: AbortSignal.timeout(120000)})
      if (!response.ok) throw new Error('benchmark download unavailable')
      const chunks = []; let size = 0
      for await (const chunk of response.body) {
        size += chunk.byteLength
        if (size > limit) throw new Error('benchmark download exceeds bound')
        chunks.push(Buffer.from(chunk))
      }
      return Buffer.concat(chunks)
    },
    async put(key, bytes) {
      if (!Buffer.isBuffer(bytes) || !bytes.length || bytes.length > limit) throw new Error('benchmark upload exceeds bound')
      const result = await rpc('CreateCacheEntry', key)
      if (!result.ok) throw new Error('benchmark write positive control unavailable')
      const uploaded = await fetcher(blob(result.upload), {method: 'PUT', redirect: 'error',
        signal: AbortSignal.timeout(120000), headers: {'x-ms-blob-type': 'BlockBlob'}, body: bytes})
      if (!uploaded.ok || !(await rpc('FinalizeCacheEntryUpload', key, {size_bytes: String(bytes.length)})).ok)
        throw new Error('benchmark write positive control unavailable')
    }
  }
}
