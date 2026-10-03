#!/usr/bin/env node
// Read-only broker. GitHub authenticates artifact transport, not branch-written claims.
// Run this from reviewed code, never execute downloaded source or artifact contents.
import {execFileSync} from 'node:child_process'
import {mkdtempSync, writeFileSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {digest} from './validation-reuse.mjs'
import {compareObservations} from './validation-shadow-report.mjs'

const repository = 'Harborline-Software/harborline-api'
const lanes = {
  'verify-macos': 'verify-macos-evidence',
  'verify-linux': 'verify-linux-evidence',
  'verify-windows-hosted': 'verify-windows-hosted-evidence',
}
export function transportProblems({run, job, artifact, archive, lane}) {
  const problems = []
  if (run?.repository?.full_name !== repository || run?.head_repository?.full_name !== repository)
    problems.push('foreign repository producer')
  if (run?.path !== '.github/workflows/verify.yml' || !['pull_request', 'merge_group', 'schedule', 'workflow_dispatch'].includes(run?.event))
    problems.push('unexpected workflow producer')
  if (run?.status !== 'completed' || run?.conclusion !== 'success') problems.push('workflow is not completed success')
  if (job?.name !== lane || job?.run_id !== run?.id || job?.run_attempt !== run?.run_attempt
    || job?.status !== 'completed' || job?.conclusion !== 'success') problems.push('job is not this completed successful attempt')
  const hostSteps = job?.steps?.filter(step => step.name.startsWith('Host lane')) ?? []
  if (hostSteps.length !== 1 || hostSteps[0].status !== 'completed' || hostSteps[0].conclusion !== 'success')
    problems.push('host step absent, skipped or nonpassing')
  if (artifact?.name !== `${lanes[lane]}-${run?.id}` || artifact?.workflow_run?.id !== run?.id
    || artifact?.workflow_run?.head_sha !== run?.head_sha || artifact?.expired !== false)
    problems.push('artifact identity mismatch or expired')
  const started = Date.parse(job?.started_at ?? ''), completed = Date.parse(job?.completed_at ?? '')
  const created = Date.parse(artifact?.created_at ?? ''), attemptStarted = Date.parse(run?.run_started_at ?? '')
  if (![started, completed, created, attemptStarted].every(Number.isFinite)
    || started < attemptStarted || completed < started || created < started || created > completed)
    problems.push('artifact is not bound to this job attempt time window')
  if (!/^sha256:[0-9a-f]{64}$/.test(artifact?.digest ?? '') || artifact.digest !== `sha256:${digest(archive)}`)
    problems.push('GitHub archive digest missing or mismatched')
  return problems
}

export function createGitHubClient(token, fetcher = fetch) {
  return async (endpoint, binary = false) => {
    // No endpoint, host or credential is ever taken from an artifact.
    if (!/^\/repos\/Harborline-Software\/harborline-api\/[A-Za-z0-9_./?=&-]+$/.test(endpoint))
      throw new Error('unsupported API endpoint')
    const response = await fetcher(`https://api.github.com${endpoint}`, {redirect: 'manual',
      headers: {Accept: 'application/vnd.github+json', ...(token ? {Authorization: `Bearer ${token}`} : {}),
        'X-GitHub-Api-Version': '2022-11-28'}, signal: AbortSignal.timeout(30000)})
    if (binary && response.status === 302) {
      const target = new URL(response.headers.get('location'))
      if (target.protocol !== 'https:' || target.username || target.password) throw new Error('unsafe artifact redirect')
      // Do not forward the GitHub token to blob storage.
      const download = await fetcher(target.href, {redirect: 'error', signal: AbortSignal.timeout(30000)})
      if (!download.ok) throw new Error(`artifact download HTTP ${download.status}`)
      const chunks = []
      let length = 0
      for await (const chunk of download.body) {
        length += chunk.length
        if (length > 64 * 1024 * 1024) throw new Error('artifact exceeds shadow size limit')
        chunks.push(Buffer.from(chunk))
      }
      return Buffer.concat(chunks)
    }
    if (!response.ok) throw new Error(`GitHub HTTP ${response.status}`)
    if (binary) throw new Error('artifact download did not redirect')
    return response.json()
  }
}

export function readArchive(archive, python = process.platform === 'win32' ? 'python' : 'python3') {
  const directory = mkdtempSync(path.join(tmpdir(), 'validation-shadow-'))
  const archivePath = path.join(directory, 'evidence.zip')
  try {
    writeFileSync(archivePath, archive)
    // Reads two exact bounded entries; nothing is extracted and no artifact code is executed.
    const script = `import json,sys,zipfile\nwith zipfile.ZipFile(sys.argv[1]) as z:\n names=z.namelist()\n result={}\n for key,name in [('observation','.claude/gate-evidence/validation-inputs-shadow.json'),('receipt','.git/harborline-api-verify-receipt.json')]:\n  if names.count(name)!=1: raise ValueError('missing or duplicate evidence entry')\n  if z.getinfo(name).file_size>32*1024*1024: raise ValueError('evidence entry too large')\n  result[key]=json.loads(z.read(name))\n print(json.dumps(result))\n`
    return JSON.parse(execFileSync(python, ['-c', script, archivePath], {encoding: 'utf8', timeout: 10000,
      stdio: 'pipe', maxBuffer: 64 * 1024 * 1024}))
  } finally { rmSync(directory, {recursive: true, force: true}) }
}

export async function observeRun({runId, api, unpack = readArchive}) {
  if (!/^[1-9][0-9]*$/.test(String(runId))) throw new Error('invalid run id')
  const prefix = `/repos/${repository}`
  const run = await api(`${prefix}/actions/runs/${runId}`)
  const artifacts = await api(`${prefix}/actions/runs/${runId}/artifacts?per_page=100`)
  const jobs = await api(`${prefix}/actions/runs/${runId}/attempts/${run.run_attempt}/jobs?per_page=100`)
  if (artifacts.total_count > 100 || jobs.total_count > 100) throw new Error('shadow pagination limit exceeded')
  const result = []
  for (const [lane, artifactPrefix] of Object.entries(lanes)) {
    const matches = artifacts.artifacts.filter(item => item.name === `${artifactPrefix}-${run.id}`)
    const jobMatches = jobs.jobs.filter(item => item.name === lane)
    if (matches.length !== 1 || jobMatches.length !== 1) {
      result.push({lane, transportVerified: false, problems: ['missing or duplicate job/artifact']}); continue
    }
    const artifact = matches[0], job = jobMatches[0]
    const archive = await api(`${prefix}/actions/artifacts/${artifact.id}/zip`, true)
    const problems = transportProblems({run, job, artifact, archive, lane})
    if (problems.length) { result.push({lane, transportVerified: false, problems}); continue }
    try {
      const contents = unpack(archive)
      const sha = contents.observation.candidateSha
      if (!/^[0-9a-f]{40}$/.test(sha ?? '')) throw new Error('invalid observed candidate')
      const commit = await api(`${prefix}/commits/${sha}`)
      // PR Actions checkouts are synthetic merge commits; require the run's source as a parent.
      const belongs = sha === run.head_sha || (run.event === 'pull_request'
        && commit.parents?.some(parent => parent.sha === run.head_sha))
      if (!belongs || commit.commit.tree.sha !== contents.observation.inputs?.candidateTree
        || contents.receipt.baseHead !== sha || contents.receipt.testedTree !== commit.commit.tree.sha
        || contents.receipt.lane !== 'host') throw new Error('candidate tree or host receipt binding mismatch')
      result.push({lane, transportVerified: true, runId: String(run.id), runAttempt: run.run_attempt,
        jobId: job.id, artifactId: artifact.id, artifactDigest: artifact.digest, observation: contents.observation,
        producerTrustedForReuse: false})
    } catch { result.push({lane, transportVerified: true, producerTrustedForReuse: false,
      problems: ['observation absent, malformed or not bound to this run candidate']}) }
  }
  return result
}

export async function compareRuns({currentRunId, priorRunId, api, unpack}) {
  const [current, prior] = await Promise.all([
    observeRun({runId: currentRunId, api, unpack}), observeRun({runId: priorRunId, api, unpack})])
  return {schemaVersion: 1, mode: 'shadow', currentRunId: String(currentRunId), priorRunId: String(priorRunId),
    reuseAuthorized: false, requiredWorkSkipped: false, lanes: current.map(item => {
      const before = prior.find(entry => entry.lane === item.lane)
      return {lane: item.lane, currentTransportVerified: item.transportVerified,
        priorTransportVerified: before?.transportVerified ?? false,
        currentProblems: item.problems ?? [], priorProblems: before?.problems ?? [],
        ...compareObservations(item.observation, before?.observation)}
    })}
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  const [currentRunId, priorRunId, output] = process.argv.slice(2)
  try {
    const result = await compareRuns({currentRunId, priorRunId, api: createGitHubClient(process.env.GH_TOKEN)})
    writeFileSync(output, JSON.stringify(result, null, 2) + '\n')
    console.log('GitHub validation shadow report written; required work was not skipped')
  } catch (error) {
    // Fetch/Headers errors can echo Authorization values. Never log upstream messages,
    // stacks, URLs or the error object; the fixed diagnostic is deliberately credential-free.
    console.error('validation shadow unavailable: request, artifact or evidence validation failed')
    process.exitCode = 1
  }
}
