// Prelanding measurement only: no package promotion, verdict reuse, or in-job tamper resistance claim.
import {execFileSync} from 'node:child_process'
import {closeSync, fstatSync, mkdirSync, mkdtempSync, openSync, readFileSync, readSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {performance} from 'node:perf_hooks'
import {prepareContainer} from './platform-feed-container.mjs'
import {readPin} from './build-local-feed.mjs'
import {canonical, hash} from './platform-feed-reuse-policy.mjs'
import {createBundle, validateBundle, publishVerifiedSameJobFeed} from './same-job-platform-feed.mjs'
import {buildEnvironment} from './platform-feed-environment.mjs'
import {benchmarkCache} from './platform-feed-benchmark-cache.mjs'
import {safeFailure} from './platform-feed-qualification.mjs'

export async function measureCase({mode, prepare, cache, key, expectedDigest, publish, validate, fullBuild,
  clock = () => performance.now(), onFailure = () => {}}) {
  if (!['cold', 'warm', 'forcedmiss'].includes(mode)) throw new Error('unsupported benchmark case')
  const started = clock(), timings = {}
  let stage, route = 'fresh', cacheState = 'not-requested'
  const timed = async (name, operation) => {
    const begin = clock(); stage = name
    try {return await operation()} finally {timings[name] = clock() - begin}
  }
  try {
  const prepared = await timed('inputVerificationMs', prepare)
  let bytes = null, files
  if (mode !== 'cold') {
    try {
      bytes = await timed('cacheDownloadMs', () => cache.get(key(prepared.input, mode)))
      cacheState = bytes === null ? 'miss' : 'hit'
      if (bytes !== null) {
        files = await timed('bundleValidationMs', () => validate(bytes, expectedDigest, prepared.input))
        route = 'cached'
      }
    } catch { cacheState = 'unusable'; files = undefined }
  }
  if (!files) {
    files = await timed('freshPackMs', () => prepared.pack())
    bytes = await timed('bundleCreationMs', () => prepared.bundle(files))
  }
  // Only a cold, fresh dependency build can publish in this disposable experiment.
  if (mode === 'cold') await timed('cacheUploadMs', () => cache.put(key(prepared.input, mode), bytes))
  const handoff = await timed('handoffMs', () => publish(files))
  const build = await timed('fullBuildMs', () => fullBuild(handoff))
  const result = {mode, route, cacheState, bundleBytes: bytes.length, inputFingerprint: hash(canonical(prepared.input)),
    timings, totalMs: clock() - started, fullBuild: build, validationReused: false}
  return {result, digest: hash(bytes)}
  } catch (error) {
    try {onFailure({mode, stage, route, cacheState, timings, elapsedMs: clock() - started,
      failure: safeFailure(error), ...(error?.benchmarkDiagnostics ? {validation: error.benchmarkDiagnostics} : {})})} catch {}
    throw error
  }
}

// Explicit field selection: never retain child log text, arguments, paths, URLs or exception messages.
export function benchmarkDiagnostics(head, proof, output = '', boundaryOutput = '', boundaryPartial = false) {
  const result = {stages: []}
  const validId = id => typeof id === 'string' && /^[a-z][a-z0-9-]{0,63}$/.test(id)
  const number = value => Number.isFinite(value) && value >= 0 && value <= 1e9
  for (const line of String(output).slice(-262144).split('\n')) {
    if (!line.startsWith('[exact-clone] ')) continue
    let row; try {row = JSON.parse(line.slice(14))} catch {continue}
    if (row && typeof row === 'object' && validId(row.id) && ['started', 'running', 'completed'].includes(row.state)) {
      result.lastStage = {id: row.id, state: row.state,
        ...(number(row.elapsedMs) ? {elapsedMs: row.elapsedMs} : {}),
        ...(Number.isInteger(row.exitCode) && row.exitCode >= 0 && row.exitCode <= 255 ? {exitCode: row.exitCode} : {})}
    }
  }
  // Reject the tracked historical report when the current invocation did not write one.
  if (proof?.apiCommit !== head || !['PASS', 'FAIL'].includes(proof.status) || !Array.isArray(proof.steps)) return result
  if (boundaryOutput) {
    const bounded = String(boundaryOutput).slice(-262144)
    const names = bounded.split('\n').flatMap(line => {
      const match = /^(?:not ok \d+ - |✖ )(.+?)(?: \([\d.]+ms\))?$/.exec(line.trim())
      return match ? [match[1]] : []
    })
    result.boundary = {capturedOutputDigest: hash(bounded), partial: boundaryPartial || bounded.length < String(boundaryOutput).length,
      failedTests: names.slice(0, 64).map(failureIdentity), diagnosticCodes: safeFailure({stdout: bounded}).diagnosticCodes ?? []}
  }
  result.status = proof.status
  result.stages = proof.steps.filter(row => row && typeof row === 'object' && validId(row.id) && typeof row.passed === 'boolean').slice(0, 64)
    .map(row => ({id: row.id, passed: row.passed,
      ...(number(row.durationMs) ? {durationMs: row.durationMs} : {}),
      ...(Number.isInteger(row.exitCode) && row.exitCode >= 0 && row.exitCode <= 255 ? {exitCode: row.exitCode} : {}),
      ...(row.observed ? {observed: Object.fromEntries(['total', 'passed', 'failed', 'notExecuted']
        .filter(key => number(row.observed[key])).map(key => [key, row.observed[key]]))} : {}),
      ...(Array.isArray(row.newFailures) ? {newFailureCount: row.newFailures.length,
        newFailures: row.newFailures.filter(id => typeof id === 'string').slice(0, 64).map(failureIdentity)} : {})}))
  return result
}

export function failureIdentity(identity) {
  const digest = hash(identity)
  // Plain custom titles and relative Vitest paths are useful diagnostics. Unsafe/unbounded titles retain identity by digest.
  const plain = /^[\p{L}\p{N} _().,"'=-]{1,512}$/u.test(identity)
  const qualified = /^Harborline\.[A-Za-z0-9_.]+\.Tests\.[A-Za-z0-9_.,: ()"'=-]{1,384}$/.test(identity)
  const vitest = /^(?!\/|[A-Za-z]:)[A-Za-z0-9_./-]+\.(?:test|spec)\.[cm]?[jt]sx? :: [\p{L}\p{N} _().,"'=-]{1,384}$/u.test(identity)
  return {digest, ...((plain || qualified || vitest) && !/\b(?:bearer|password|token|secret)\s*[:=]/i.test(identity)
    ? {display: identity} : {})}
}

export function readBoundaryTail(file) {
  const descriptor = openSync(file, 'r')
  try {
    const size = fstatSync(descriptor).size, length = Math.min(size, 262144)
    const buffer = Buffer.alloc(length)
    const read = readSync(descriptor, buffer, 0, length, size - length)
    return {output: buffer.subarray(0, read).toString('utf8'), partial: size > length}
  } finally {closeSync(descriptor)}
}

export function compareMeasurements(results) {
  if (results.length !== 3 || results.map(row => row.mode).join(',') !== 'cold,warm,forcedmiss'
    || results.some(row => row.fullBuild?.passed !== true || row.validationReused !== false
      || !Number.isFinite(row.totalMs) || row.totalMs <= 0)
    || results[0].route !== 'fresh' || results[1].route !== 'cached'
    || results[2].route !== 'fresh' || results[2].cacheState !== 'miss'
    || new Set(results.map(row => row.inputFingerprint)).size !== 1
    || new Set(results.map(row => row.fullBuild.workDigest)).size !== 1)
    throw new Error('benchmark controls incomplete; no speedup verdict')
  return {coldMs: results[0].totalMs, warmMs: results[1].totalMs, forcedMissMs: results[2].totalMs,
    coldConsumerMs: results[0].totalMs - (results[0].timings?.cacheUploadMs || 0),
    improvementPercent: 100 * (results[0].totalMs - (results[0].timings?.cacheUploadMs || 0) - results[1].totalMs)
      / (results[0].totalMs - (results[0].timings?.cacheUploadMs || 0))}
}

export function benchmarkWork(proof, inventory, head) {
  const required = ['dotnet-restore', 'platform-feed-consumption', 'dotnet-build', 'dotnet-host-tests',
    'capability-contracts-tests', 'capability-tests', 'host-baseline-match', 'capability-baseline-match']
  if (proof.status !== 'PASS' || proof.apiCommit !== head || inventory.apiCommit !== head
    || !Array.isArray(proof.steps) || required.some(id => !proof.steps.some(row => row.id === id && row.passed === true)))
    throw new Error('full fresh benchmark work missing or provenance differs')
  const observed = Object.fromEntries(['host', 'capability'].map(name => {
    const result = proof.steps.find(row => row.id === `${name}-baseline-match`)
    const identities = inventory[name]?.identities
    if (!result?.observed || !Array.isArray(identities) || !identities.length
      || identities.some(id => typeof id !== 'string') || new Set(identities).size !== identities.length)
      throw new Error('benchmark test evidence incomplete')
    return [name, {counts: result.observed, identities: [...identities].sort()}]
  }))
  return {passed: true, workDigest: hash(canonical(observed)),
    inventories: Object.fromEntries(Object.entries(observed).map(([name, value]) => [name,
      {counts: value.counts, identityCount: value.identities.length, identityDigest: hash(canonical(value.identities))}])),
    stages: proof.steps.filter(row => required.includes(row.id)).map(row => ({id: row.id, durationMs: row.durationMs}))}
}

export async function benchmark(platform, env = process.env) {
  const pair = env.BENCHMARK_PAIR
  if (process.platform !== 'linux' || Number(process.versions.node.split('.')[0]) !== 24
    || !/^[1-3]$/.test(pair || '') || !/^[1-9][0-9]{0,19}$/.test(env.GITHUB_RUN_ID || '')
    || !/^[1-9][0-9]{0,9}$/.test(env.GITHUB_RUN_ATTEMPT || '')
    || env.GITHUB_REPOSITORY !== 'Harborline-Software/harborline-api'
    || env.GITHUB_EVENT_NAME !== 'workflow_dispatch' || env.GITHUB_JOB !== 'benchmark-linux')
    throw new Error('unsupported benchmark execution context')
  const root = path.resolve(import.meta.dirname, '..'), pin = readPin(path.join(root, 'eng/platform-pin.json'))
  const execute = (command, args, options = {}) => execFileSync(command, args, {encoding: 'utf8',
    env: buildEnvironment(env), stdio: 'pipe', timeout: 75 * 60000, maxBuffer: 64 * 1024 * 1024, ...options})
  const head = execute('git', ['-C', root, 'rev-parse', 'HEAD']).trim()
  if (head !== env.GITHUB_SHA || execute('git', ['-C', root, 'status', '--porcelain']).trim())
    throw new Error('benchmark source must be the clean dispatched commit')
  const sdk = execute('dotnet', ['--version'], {cwd: root}).trim()
  const profile = JSON.parse(readFileSync(path.join(root, 'eng/platform-feed-profile.json')))
  if (sdk !== profile.sdk) throw new Error('benchmark host SDK differs from fixed profile')
  // Equalize immutable image setup; cold must not pay a first pull that warm avoids.
  const setupStarted = Number(env.BENCHMARK_SETUP_STARTED_MS)
  if (!Number.isSafeInteger(setupStarted) || setupStarted <= 0 || setupStarted > Date.now())
    throw new Error('benchmark common setup timing unavailable')
  execute('docker', ['pull', '--platform=linux/amd64', profile.image])
  const commonSetupMs = Date.now() - setupStarted
  const cache = benchmarkCache(env)
  const destination = path.join(root, '.claude/platform-feed-benchmark/evidence.json')
  mkdirSync(path.dirname(destination), {recursive: true})
  const evidence = {schemaVersion: 1, apiCommit: head, sdk, node: process.version, image: profile.image,
    runId: env.GITHUB_RUN_ID, runAttempt: env.GITHUB_RUN_ATTEMPT,
    pair: Number(pair), commonSetupMs, results: [], reuseAuthorized: false, verdictReused: false,
    scope: 'controlled disposable transport measurement; production authority remains unqualified'}
  const record = () => writeFileSync(destination, `${JSON.stringify(evidence, null, 2)}\n`)
  let digest
  try {
    for (const mode of ['cold', 'warm', 'forcedmiss']) {
      let prepared, handoff, clone
      const scratch = mkdtempSync(path.join(env.RUNNER_TEMP || tmpdir(), 'api-feed-benchmark-'))
      try {
        const measured = await measureCase({mode, expectedDigest: digest, cache,
          onFailure: details => {evidence.failedCase = details; record()},
          key: (input, selected) => `api356-benchmark-${env.GITHUB_RUN_ID}-${env.GITHUB_RUN_ATTEMPT}-${pair}-${hash(canonical(input))}-${selected === 'forcedmiss' ? 'forcedmiss' : 'bundle'}`,
          prepare: () => {
            clone = path.join(scratch, 'source')
            execute('git', ['clone', '--quiet', '--no-hardlinks', root, clone])
            prepared = prepareContainer({apiRoot: root, platform, pin})
            return {...prepared, bundle: files => createBundle(files, prepared.input, pin)}
          },
          validate: (bytes, bound, input) => validateBundle(bytes, bound, input, pin),
          publish: files => {
            handoff = publishVerifiedSameJobFeed(files, platform, {...env, GITHUB_ENV: undefined}, clone)
            return handoff
          },
          fullBuild: transfer => {
            const buildEnv = {...buildEnvironment(env), HARBORLINE_VERIFY_LANE: 'host', HARBORLINE_GATE_QUALITY: '',
              HARBORLINE_PLATFORM_REPO: platform, NUGET_PACKAGES: path.join(scratch, 'nuget'),
              npm_config_cache: path.join(scratch, 'npm-cache'), npm_config_store_dir: path.join(scratch, 'pnpm-store'),
              DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER: '1', UseSharedCompilation: 'false',
              HARBORLINE_PLATFORM_FEED_HANDOFF_PATH: transfer.path,
              HARBORLINE_PLATFORM_FEED_HANDOFF_SHA256: transfer.sha256}
            try {
              execute(process.execPath, ['eng/verify-preflight.mjs'], {cwd: clone, env: buildEnv})
              execute(process.execPath, ['eng/run-exact-clone.mjs', '--record', '--host-baseline',
                'eng/baselines/host-test-baseline.ubuntu.json'], {cwd: clone, env: buildEnv})
            } catch (error) {
              let proof; try {proof = JSON.parse(readFileSync(path.join(clone, 'docs/evidence/exact-clone.json')))} catch {}
              let boundary = {output: '', partial: false}
              if (proof?.apiCommit === head && Array.isArray(proof.steps)
                && proof.steps.some(row => row?.id === 'boundary-check' && row.passed === false)) {
                try {boundary = readBoundaryTail(path.join(clone, '.claude/gate-evidence',
                  `exact-clone-${head}-boundary-check.log`))} catch {}
              }
              try {error.benchmarkDiagnostics = benchmarkDiagnostics(head, proof, error?.stdout, boundary.output, boundary.partial)} catch {}
              throw error
            }
            const proof = JSON.parse(readFileSync(path.join(clone, 'docs/evidence/exact-clone.json')))
            // The gate always emits the actual candidate test inventory, including successful runs.
            const inventory = JSON.parse(readFileSync(path.join(clone, '.claude/gate-evidence/known-tests-candidate.json')))
            return benchmarkWork(proof, inventory, head)
          }})
        if (mode === 'cold') digest = measured.digest
        measured.result.totalWithCommonSetupMs = measured.result.totalMs + commonSetupMs
        evidence.results.push(measured.result); record()
        console.log(JSON.stringify({pair: Number(pair), mode, route: measured.result.route, cacheState: measured.result.cacheState,
          totalMs: measured.result.totalMs}))
      } finally {
        if (handoff) rmSync(path.dirname(handoff.path), {recursive: true, force: true})
        if (prepared) rmSync(prepared.directory, {recursive: true, force: true})
        rmSync(scratch, {recursive: true, force: true})
      }
    }
    evidence.measurement = compareMeasurements(evidence.results)
    const cold = evidence.measurement.coldConsumerMs + commonSetupMs
    const warm = evidence.measurement.warmMs + commonSetupMs
    evidence.measurement.withCommonSetup = {coldConsumerMs: cold, warmMs: warm,
      forcedMissMs: evidence.measurement.forcedMissMs + commonSetupMs, improvementPercent: 100 * (cold - warm) / cold}
    evidence.completed = true; record()
  } catch (error) {
    evidence.completed = false; evidence.failure = safeFailure(error); record()
    throw new Error('benchmark failed; no performance verdict')
  }
  return evidence
}
if ((process.argv[1] || '').replaceAll('\\', '/').endsWith('/platform-feed-benchmark.mjs')) {
  try {await benchmark(process.argv[2] || process.env.BENCHMARK_PLATFORM)}
  catch {console.error('platform feed benchmark failed'); process.exitCode = 1}
}
