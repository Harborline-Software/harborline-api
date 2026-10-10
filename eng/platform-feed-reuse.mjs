#!/usr/bin/env node
// Artifact reuse for Linux dependency packages. Every API gate still executes freshly.
import {execFileSync} from 'node:child_process'
import {createHash} from 'node:crypto'
import {lstatSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {prepareContainer, producerPaths} from './platform-feed-container.mjs'
import {canonical, hash, repository, reuseProblems, definitionProblems} from './platform-feed-reuse-policy.mjs'
import {readPin} from './build-local-feed.mjs'
import {createBundle, validateBundle, publishVerifiedSameJobFeed} from './same-job-platform-feed.mjs'
import {buildEnvironment} from './platform-feed-environment.mjs'

const root = path.resolve(import.meta.dirname, '..'), prefix = `/repos/${repository}`
const commit = value => typeof value === 'string' && /^[0-9a-f]{40}$/.test(value)
const git = (...args) => execFileSync('git', ['-c', `safe.directory=${root}`, '-C', root, ...args],
  {encoding: 'utf8', timeout: 30000, env: buildEnvironment()}).trim()
const blobHash = bytes => createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex')

export function githubClient(token, fetcher = fetch) {
  return async (endpoint, binary = false) => {
    if (!/^\/repos\/Harborline-Software\/harborline-(?:api|platform)\/[A-Za-z0-9_./?=&-]+$/.test(endpoint))
      throw new Error('unsupported API endpoint')
    const response = await fetcher(`https://api.github.com${endpoint}`, {redirect: 'manual',
      headers: {Accept: 'application/vnd.github+json', ...(token ? {Authorization: `Bearer ${token}`} : {}),
        'X-GitHub-Api-Version': '2022-11-28'}, signal: AbortSignal.timeout(30000)})
    if (binary && response.status === 302) {
      const target = new URL(response.headers.get('location'))
      if (target.protocol !== 'https:' || target.username || target.password) throw new Error('unsafe download redirect')
      const download = await fetcher(target.href, {redirect: 'error', signal: AbortSignal.timeout(30000)})
      if (!download.ok) throw new Error('artifact unavailable')
      const chunks = []; let size = 0
      for await (const chunk of download.body) {
        size += chunk.length
        if (size > 64 * 1024 * 1024) throw new Error('artifact exceeds size bound')
        chunks.push(Buffer.from(chunk))
      }
      return Buffer.concat(chunks)
    }
    if (!response.ok || binary) throw new Error('GitHub metadata unavailable')
    return response.json()
  }
}

export function unpackFeed(archive, authenticatedDigest, {python = process.env.HARBORLINE_FEED_PYTHON} = {}) {
  if (typeof authenticatedDigest !== 'string' || !/^sha256:[a-f0-9]{64}$/.test(authenticatedDigest)
    || archive.length > 64 * 1024 * 1024 || `sha256:${hash(archive)}` !== authenticatedDigest)
    throw new Error('artifact archive digest differs from authenticated metadata')
  // Workflow step outputs bind this path before candidate tests can alter PATH.
  if (typeof python !== 'string' || !path.isAbsolute(python) || !lstatSync(python).isFile())
    throw new Error('trusted archive interpreter unavailable')
  const directory = mkdtempSync(path.join(tmpdir(), 'api-feed-archive-'))
  try {
    const file = path.join(directory, 'artifact.zip'); writeFileSync(file, archive)
    // -I ignores cwd, PYTHONPATH and user site modules. Both ZIP bytes and decoded entry
    // are bound in one isolated standard-library process; no candidate module is imported.
    const script = "import sys,zipfile,hashlib,io,json,base64\nraw=open(sys.argv[1],'rb').read()\nwith zipfile.ZipFile(io.BytesIO(raw)) as z:\n if z.namelist()!=['feed-bundle.json']: raise ValueError('unexpected artifact entries')\n if z.getinfo('feed-bundle.json').file_size>64*1024*1024: raise ValueError('oversize feed')\n entry=z.read('feed-bundle.json')\n print(json.dumps({'archiveSha256':hashlib.sha256(raw).hexdigest(),'entryBase64':base64.b64encode(entry).decode('ascii')}))\n"
    const decoded = JSON.parse(execFileSync(python, ['-I', '-c', script, file],
      {cwd: directory, timeout: 10000, maxBuffer: 90 * 1024 * 1024, stdio: 'pipe', env: buildEnvironment()}))
    if (`sha256:${decoded.archiveSha256}` !== authenticatedDigest || typeof decoded.entryBase64 !== 'string')
      throw new Error('decoded entry is not bound to authenticated archive')
    const entry = Buffer.from(decoded.entryBase64, 'base64')
    if (entry.length > 64 * 1024 * 1024 || entry.toString('base64') !== decoded.entryBase64)
      throw new Error('invalid decoded archive entry')
    return entry
  } finally {rmSync(directory, {recursive: true, force: true})}
}

export async function verifyMainInputs({api, apiRoot = root, pin = readPin(path.join(apiRoot, 'eng/platform-pin.json'))}) {
  const main = await api(`${prefix}/branches/main`)
  if (main.protected !== true || !commit(main.commit?.sha)) throw new Error('API main is not protected')
  const tree = await api(`${prefix}/git/trees/${main.commit.sha}?recursive=1`)
  if (tree.truncated !== false || !Array.isArray(tree.tree)) throw new Error('incomplete main tree')
  for (const name of producerPaths) {
    const expected = tree.tree.filter(file => file.path === name)
    const absolute = path.join(apiRoot, name)
    if (expected.length !== 1 || expected[0].type !== 'blob' || expected[0].mode !== '100644'
      || !lstatSync(absolute).isFile() || lstatSync(absolute).isSymbolicLink()
      || blobHash(readFileSync(absolute)) !== expected[0].sha) throw new Error('local producer definitions differ from protected main')
  }
  if (pin.repository !== 'Harborline-Software/harborline-platform' || !commit(pin.commit)) throw new Error('invalid platform pin')
  const platformPrefix = '/repos/Harborline-Software/harborline-platform'
  const platformMain = await api(`${platformPrefix}/branches/main`)
  if (platformMain.protected !== true || !commit(platformMain.commit?.sha)) throw new Error('platform main is not protected')
  const ancestry = await api(`${platformPrefix}/compare/${pin.commit}...${platformMain.commit.sha}`)
  if (!['ahead', 'identical'].includes(ancestry.status)) throw new Error('platform pin has not landed on protected main')
  return {mainSha: main.commit.sha, tree, pin}
}

export async function findReusableFeed({api, trusted, expected, unpack = unpackFeed, now = Date.now(), list}) {
  list ??= await api(`${prefix}/actions/workflows/platform-feed-producer.yml/runs?branch=main&status=success&per_page=20`)
  for (const summary of list.workflow_runs ?? []) {
    if (!/^[1-9][0-9]*$/.test(String(summary.id))) continue
    const run = await api(`${prefix}/actions/runs/${summary.id}`)
    if (!commit(run.head_sha)) continue
    const [sourceTree, ancestry, artifacts, jobs] = await Promise.all([
      api(`${prefix}/git/trees/${run.head_sha}?recursive=1`), api(`${prefix}/compare/${run.head_sha}...${trusted.mainSha}`),
      api(`${prefix}/actions/runs/${run.id}/artifacts?per_page=100`),
      api(`${prefix}/actions/runs/${run.id}/attempts/${run.run_attempt}/jobs?per_page=100`)])
    if (artifacts.total_count > 100 || jobs.total_count > 100) continue
    const artifactMatches = artifacts.artifacts.filter(item => item.name === `platform-feed-linux-${run.id}`)
    const jobMatches = jobs.jobs.filter(item => item.name === 'produce-linux')
    if (artifactMatches.length !== 1 || jobMatches.length !== 1) continue
    const artifact = artifactMatches[0], job = jobMatches[0]
    if (!/^[1-9][0-9]*$/.test(String(artifact.id))) continue
    const archive = await api(`${prefix}/actions/artifacts/${artifact.id}/zip`, true)
    const binding = {expected, run, job, artifact, archive, now,
      trustedDefinitionsMatch: definitionProblems({trustedTree: trusted.tree, sourceTree, paths: producerPaths}).length === 0,
      sourceOnProtectedMain: ['ahead', 'identical'].includes(ancestry.status)}
    if (reuseProblems({...binding, observed: expected}).length) continue
    const raw = unpack(archive, artifact.digest), bundle = JSON.parse(raw)
    if (reuseProblems({...binding, observed: bundle.identity}).length) continue
    // The archive is authenticated above; this inner digest merely binds the decoded entry.
    const files = validateBundle(raw, hash(raw), expected, trusted.pin)
    return {files, producerRun: run.id, producerAttempt: run.run_attempt, archiveDigest: artifact.digest,
      inputFingerprint: hash(canonical(expected)), artifactReuse: true, validationReuse: false}
  }
  return null
}

export async function consumeFeed({api, platform, prepare = prepareContainer, publish = publishVerifiedSameJobFeed,
  env = process.env, apiRoot = root, unpack, now, host = {os: process.platform, architecture: process.arch}}) {
  let prepared
  let inspectionStage = 'consumer-context'
  try {
    if (host.os !== 'linux' || host.architecture !== 'x64' || env.GITHUB_REPOSITORY !== repository
      || env.GITHUB_JOB !== 'verify-linux' || env.HARBORLINE_VERIFY_LANE !== 'host') throw new Error('unsupported consumer lane')
    inspectionStage = 'protected-main-definitions'
    const trusted = await verifyMainInputs({api, apiRoot})
    inspectionStage = 'producer-discovery'
    const list = await api(`${prefix}/actions/workflows/platform-feed-producer.yml/runs?branch=main&status=success&per_page=20`)
    if (!Array.isArray(list.workflow_runs) || list.workflow_runs.length === 0)
      return {reused: false, reason: 'no-successful-producer', validationReuse: false}
    inspectionStage = 'container-inputs'
    prepared = prepare({apiRoot, platform, pin: trusted.pin})
    inspectionStage = 'artifact-verification'
    const result = await findReusableFeed({api, trusted, expected: prepared.input, unpack, now, list})
    if (!result) return {reused: false, reason: 'no-verified-matching-feed', validationReuse: false}
    inspectionStage = 'same-job-publication'
    const handoff = publish(result.files, platform, env, apiRoot)
    return {reused: true, ...result, files: undefined, handoff, validationReuse: false}
  } catch {
    return {reused: false, reason: 'feed-inspection-unavailable', inspectionStage, validationReuse: false}
  } finally {
    if (prepared?.directory && path.basename(prepared.directory).startsWith('api-feed-container-'))
      rmSync(prepared.directory, {recursive: true, force: true})
  }
}

if (import.meta.main) {
  const [command, platform, candidateRoot] = process.argv.slice(2)
  const api = githubClient(process.env.GH_TOKEN)
  if (command === 'consume') {
    const apiRoot = candidateRoot && path.isAbsolute(candidateRoot) ? candidateRoot : root
    const result = await consumeFeed({api, platform, apiRoot})
    mkdirSync(path.join(apiRoot, '.claude/gate-evidence'), {recursive: true})
    writeFileSync(path.join(apiRoot, '.claude/gate-evidence/platform-feed-reuse.json'), JSON.stringify(result, null, 2))
    console.log(result.reused ? 'platform-feed: verified dependency artifact; fresh API validation retained'
      : 'platform-feed: artifact unavailable or mismatched; fresh canonical pack required')
    // Exit 2 signals the existing builder fallback, never a green required-check shortcut.
    process.exitCode = result.reused ? 0 : 2
  } else if (command === 'produce') {
    let prepared
    try {
      if (process.env.GITHUB_REPOSITORY !== repository || process.env.GITHUB_JOB !== 'produce-linux'
        || process.env.GITHUB_REF !== 'refs/heads/main' || !commit(process.env.GITHUB_WORKFLOW_SHA)
        || git('rev-parse', 'HEAD') !== process.env.GITHUB_WORKFLOW_SHA) throw new Error('untrusted producer context')
      const trusted = await verifyMainInputs({api})
      const ancestry = await api(`${prefix}/compare/${process.env.GITHUB_WORKFLOW_SHA}...${trusted.mainSha}`)
      if (!['ahead', 'identical'].includes(ancestry.status)) throw new Error('producer is not protected-main source')
      prepared = prepareContainer({apiRoot: root, platform, pin: trusted.pin})
      const raw = createBundle(prepared.pack(), prepared.input, trusted.pin)
      if (raw.length > 64 * 1024 * 1024) throw new Error('feed bundle too large')
      mkdirSync(path.join(root, '.claude/platform-feed-producer'), {recursive: true})
      writeFileSync(path.join(root, '.claude/platform-feed-producer/feed-bundle.json'), raw)
      console.log('isolated Linux dependency feed built; no API code or test verdict produced')
    } catch {
      console.error('platform feed producer failed: trusted isolated build unavailable')
      process.exitCode = 1
    } finally {
      if (prepared?.directory && path.basename(prepared.directory).startsWith('api-feed-container-')) rmSync(prepared.directory, {recursive: true, force: true})
    }
  } else {console.error('usage: platform-feed-reuse.mjs produce|consume <platform>'); process.exitCode = 1}
}
