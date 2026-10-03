// Prelanding qualification only. Never authorizes a cross-run artifact or reuses an API verdict.
import {execFileSync} from 'node:child_process'
import {mkdtempSync, mkdirSync, readFileSync, writeFileSync, readdirSync, renameSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {prepareContainer} from './platform-feed-container.mjs'
import {readPin} from './build-local-feed.mjs'
import {createBundle, materializeFeed, restoreSameJobFeed, sha256} from './same-job-platform-feed.mjs'
import {verifyConsumedFeed} from './platform-feed-consumption.mjs'
import {handoffRestored} from './exact-clone-platform-feed.mjs'
import {buildEnvironment} from './platform-feed-environment.mjs'
import {crashSignatures, diagnosticContainerRun} from './platform-feed-crash-diagnostics.mjs'
import {probePidOneRestore} from './platform-feed-pid1-probe.mjs'

export function safeFailure(error) {
  const result = {kind: Number.isInteger(error?.status) ? 'command-exit' : 'validation-or-spawn'}
  if (Number.isInteger(error?.status) && error.status >= 0 && error.status <= 255) result.exitCode = error.status
  if (['ENOENT', 'EACCES', 'EPERM', 'ETIMEDOUT', 'ENOBUFS', 'ENOMEM', 'EIO', 'E2BIG', 'EBADF',
    'ECONNRESET', 'ECONNREFUSED', 'ENETUNREACH', 'ERR_CHILD_PROCESS_STDIO_MAXBUFFER'].includes(error?.code)) result.osCode = error.code
  if (['SIGABRT', 'SIGTERM', 'SIGKILL', 'SIGSEGV'].includes(error?.signal)) result.signal = error.signal
  // Recognize fixed failure codes, never retain raw messages, URLs, arguments or paths.
  const text = [error?.stdout, error?.stderr].map(value => typeof value === 'string' || Buffer.isBuffer(value) ? String(value).slice(-16384) : '').join('\n')
  const codes = [...new Set(text.match(/\b(?:NU\d{4}|MSB\d{4}|NETSDK\d{4})\b/g) ?? [])].slice(0, 8)
  if (codes.length) result.diagnosticCodes = codes
  const signatures = crashSignatures(text)
  if (signatures.length) result.observedSignatures = signatures
  for (const library of ['libatomic.so.1', 'libstdc++.so.6', 'libgcc_s.so.1', 'libc.so.6', 'libm.so.6', 'libdl.so.2', 'libpthread.so.0'])
    if (text.includes(`error while loading shared libraries: ${library}:`)) result.missingSharedLibrary = library
  return result
}

export function qualify(platform) {
  if (process.platform !== 'linux' || Number(process.versions.node.split('.')[0]) !== 24)
    throw new Error('qualification requires Linux Node24')
  const root = path.resolve(import.meta.dirname, '..'), pin = readPin(path.join(root, 'eng/platform-pin.json'))
  const profile = JSON.parse(readFileSync(path.join(root, 'eng/platform-feed-profile.json')))
  const execute = (command, args) => execFileSync(command, args, {encoding: 'utf8',
    env: buildEnvironment(), stdio: 'pipe', maxBuffer: 64 * 1024 * 1024, timeout: 20 * 60 * 1000})
  const directory = mkdtempSync(path.join(process.env.RUNNER_TEMP ?? tmpdir(), 'api-feed-qualification-'))
  const clone = path.join(directory, 'clone'), packages = path.join(directory, 'nuget-packages')
  const evidencePath = path.join(root, '.claude/platform-feed-qualification/evidence.json')
  mkdirSync(path.dirname(evidencePath), {recursive: true})
  const evidence = {schemaVersion: 1, profile: 'Linux Docker Node24', apiCommit: execute('git', ['-C', root, 'rev-parse', 'HEAD']).trim(),
    node: process.version, image: profile.image, trustedCrossRunHit: false, apiValidationReused: false,
    hostedEndToEndSpeedupMeasured: false, stages: []}
  const record = (stage, details = {}) => {
    evidence.stages.push({stage, ...details}); writeFileSync(evidencePath, JSON.stringify(evidence, null, 2))
    console.log(JSON.stringify({stage, ...details}))
  }
  let currentStage = 'started'
  let currentDetails = {}
  const stage = (name, details = {}) => {currentStage = name; currentDetails = details; record('operation-started', {operation: name, ...details})}
  const containerRun = diagnosticContainerRun({observe: state => record('container-resource-state', {operation: currentStage, ...currentDetails, state})})
  let failedRestore
  const observedRun = (command, args, options) => {
    try {return containerRun(command, args, options)} catch (error) {
      const index = args.indexOf(profile.image)
      if (command === 'docker' && args[0] === 'run' && index >= 0 && args[index + 1] === 'dotnet' && args[index + 2] === 'restore')
        failedRestore = {args}
      throw error
    }
  }
  let prepared
  try {
    record('started')
    stage('docker-engine-profile')
    const runtime = JSON.parse(execute('docker', ['version', '--format', '{{json .}}']))
    if (!runtime.Server?.Components?.some(component => component.Name === 'Engine')
      || !runtime.Client?.Platform?.Name?.startsWith('Docker Engine')) throw new Error('real Docker Engine required')
    stage('isolated-api-clone')
    execute('git', ['clone', '--quiet', '--no-hardlinks', root, clone])
    let started = Date.now()
    prepared = prepareContainer({apiRoot: root, platform, pin, observe: stage, run: observedRun})
    record('independent-platform-restore', {durationMs: Date.now() - started})
    started = Date.now()
    const files = prepared.pack(), raw = createBundle(files, prepared.input, pin)
    record('production-builder-pack', {durationMs: Date.now() - started, files: files.length})
    const bundlePath = path.join(directory, 'feed-bundle.json')
    stage('bundle-materialization')
    writeFileSync(bundlePath, raw); materializeFeed(files, path.join(clone, '.feed')); mkdirSync(packages)
    started = Date.now()
    stage('full-api-restore')
    containerRun('docker', ['run', '--rm', '--platform=linux/amd64', '--read-only', '--cap-drop=ALL',
      '--security-opt=no-new-privileges', '--pids-limit=256', '--cpus=4', '--user', `${process.getuid()}:${process.getgid()}`,
      '--tmpfs', '/tmp:rw,nosuid,nodev,size=1073741824', '--network', 'bridge',
      '-e', 'HOME=/tmp', '-e', 'DOTNET_CLI_HOME=/tmp', '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
      '-e', `NUGET_PACKAGES=${packages}`, '--mount', `type=bind,source=${directory},target=${directory}`,
      '--workdir', clone, profile.image, 'dotnet', 'restore', 'Harborline.Api.slnx', '--packages', packages,
      '--configfile', path.join(clone, 'nuget.config'), '-nodeReuse:false', '-maxcpucount:4'],
    {encoding: 'utf8', env: buildEnvironment(), stdio: 'pipe', maxBuffer: 64 * 1024 * 1024, timeout: 20 * 60 * 1000})
    record('full-api-restore', {durationMs: Date.now() - started})
    const options = {clone, packages, bundlePath, bundleDigest: sha256(raw), pin}
    stage('consumption-proof')
    const positive = verifyConsumedFeed(options)
    record('consumed-bytes', {proof: positive})
    const refuse = (name, call) => {
      stage('negative-control', {control: name})
      let rejected = false; try {call()} catch {rejected = true}
      if (!rejected) throw new Error('negative qualification control accepted')
      record(name, {rejected})
    }
    refuse('wrong-bundle-digest', () => verifyConsumedFeed({...options, bundleDigest: '0'.repeat(64)}))
    refuse('stale-platform-pin', () => verifyConsumedFeed({...options, pin: {...pin, commit: '0'.repeat(40)}}))
    const folder = path.join(packages, positive.packages[0].package.toLowerCase())
    const archive = path.join(folder, readdirSync(folder).find(name => name.endsWith('.nupkg')))
    const original = readFileSync(archive)
    try {writeFileSync(archive, 'same version different bytes'); refuse('archive-tamper', () => verifyConsumedFeed(options))}
    finally {writeFileSync(archive, original)}
    renameSync(archive, archive + '.missing')
    try {refuse('missing-package', () => verifyConsumedFeed(options))} finally {renameSync(archive + '.missing', archive)}
    const framework = readdirSync(path.join(folder, 'lib'))[0]
    const dll = path.join(folder, 'lib', framework, readdirSync(path.join(folder, 'lib', framework)).find(name => name.endsWith('.dll')))
    const dllBytes = readFileSync(dll)
    try {writeFileSync(dll, 'changed extracted DLL'); refuse('extracted-assembly-tamper', () => verifyConsumedFeed(options))}
    finally {writeFileSync(dll, dllBytes)}
    const env = {...buildEnvironment(), HARBORLINE_PLATFORM_FEED_HANDOFF_PATH: path.join(directory, 'absent-transfer.json'),
      HARBORLINE_PLATFORM_FEED_HANDOFF_SHA256: 'a'.repeat(64)}
    stage('missing-handoff-route')
    const miss = restoreSameJobFeed(platform, env)
    if (miss.restored !== false || handoffRestored({passed: true,
      fullOutput: `platform-feed-handoff-result:${JSON.stringify(miss)}\n`}, env) !== false)
      throw new Error('missing handoff selected reuse')
    record('missing-handoff-selects-fresh-route', {restored: false, reason: miss.reason})
    stage('final-consumption-proof')
    verifyConsumedFeed(options)
    record('complete', {productionBuilderQualified: true, trustedCrossRunHit: false})
    return evidence
  } catch (error) {
    const failure = safeFailure(error)
    record('failed', {qualificationPassed: false, operation: currentStage, failure})
    if (failedRestore && failure.exitCode === 139 && failure.observedSignatures?.includes('runtime-fail-fast')) {
      // The workflow establishes this deadline before checkout/setup. Leave two
      // minutes for scratch cleanup and upload; diagnostics never extend the job.
      const deadline = Number(process.env.HARBORLINE_QUALIFICATION_DEADLINE_MS)
      const budgetMs = Number.isSafeInteger(deadline) && deadline > 0
        ? Math.min(300000, Math.max(0, deadline - Date.now() - 120000)) : 0
      record('pid1-probe-started', {pairs: 3, budgetMs, productionQualificationPassed: false})
      try {probePidOneRestore({args: failedRestore.args, image: profile.image, commit: pin.commit,
        classifyFailure: safeFailure, observe: record, budgetMs})}
      catch {record('pid1-probe-unavailable', {productionQualificationPassed: false})}
    }
    throw new Error('platform feed qualification failed; see bounded stage evidence')
  } finally {
    rmSync(directory, {recursive: true, force: true})
    if (prepared) rmSync(prepared.directory, {recursive: true, force: true})
  }
}
if (import.meta.main) {
  try {qualify(process.argv[2])} catch {console.error('platform feed qualification failed'); process.exitCode = 1}
}
