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

export function qualify(platform) {
  if (process.platform !== 'linux' || Number(process.versions.node.split('.')[0]) !== 24)
    throw new Error('qualification requires Linux Node24')
  const root = path.resolve(import.meta.dirname, '..'), pin = readPin(path.join(root, 'eng/platform-pin.json'))
  const profile = JSON.parse(readFileSync(path.join(root, 'eng/platform-feed-profile.json')))
  const execute = (command, args) => execFileSync(command, args, {encoding: 'utf8',
    env: buildEnvironment(), stdio: 'pipe', maxBuffer: 64 * 1024 * 1024, timeout: 20 * 60 * 1000})
  const runtime = JSON.parse(execute('docker', ['version', '--format', '{{json .}}']))
  if (!runtime.Server?.Components?.some(component => component.Name === 'Engine')
    || !runtime.Client?.Platform?.Name?.startsWith('Docker Engine')) throw new Error('real Docker Engine required')
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
  let prepared
  try {
    record('started')
    execute('git', ['clone', '--quiet', '--no-hardlinks', root, clone])
    let started = Date.now()
    prepared = prepareContainer({apiRoot: root, platform, pin})
    record('independent-platform-restore', {durationMs: Date.now() - started})
    started = Date.now()
    const files = prepared.pack(), raw = createBundle(files, prepared.input, pin)
    record('production-builder-pack', {durationMs: Date.now() - started, files: files.length})
    const bundlePath = path.join(directory, 'feed-bundle.json')
    writeFileSync(bundlePath, raw); materializeFeed(files, path.join(clone, '.feed')); mkdirSync(packages)
    started = Date.now()
    execute('docker', ['run', '--rm', '--platform=linux/amd64', '--read-only', '--cap-drop=ALL',
      '--security-opt=no-new-privileges', '--pids-limit=256', '--cpus=4', '--user', `${process.getuid()}:${process.getgid()}`,
      '--tmpfs', '/tmp:rw,nosuid,nodev,size=1073741824', '--network', 'bridge',
      '-e', 'HOME=/tmp', '-e', 'DOTNET_CLI_HOME=/tmp', '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
      '-e', `NUGET_PACKAGES=${packages}`, '--mount', `type=bind,source=${directory},target=${directory}`,
      '--workdir', clone, profile.image, 'dotnet', 'restore', 'Harborline.Api.slnx', '--packages', packages,
      '--configfile', path.join(clone, 'nuget.config'), '-nodeReuse:false', '-maxcpucount:4'])
    record('full-api-restore', {durationMs: Date.now() - started})
    const options = {clone, packages, bundlePath, bundleDigest: sha256(raw), pin}
    const positive = verifyConsumedFeed(options)
    record('consumed-bytes', {proof: positive})
    const refuse = (name, call) => {
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
    const miss = restoreSameJobFeed(platform, env)
    if (miss.restored !== false || handoffRestored({passed: true,
      fullOutput: `platform-feed-handoff-result:${JSON.stringify(miss)}\n`}, env) !== false)
      throw new Error('missing handoff selected reuse')
    record('missing-handoff-selects-fresh-route', {restored: false, reason: miss.reason})
    verifyConsumedFeed(options)
    record('complete', {productionBuilderQualified: true, trustedCrossRunHit: false})
    return evidence
  } catch {
    record('failed', {qualificationPassed: false})
    throw new Error('platform feed qualification failed; see bounded stage evidence')
  } finally {
    rmSync(directory, {recursive: true, force: true})
    if (prepared) rmSync(prepared.directory, {recursive: true, force: true})
  }
}
if (import.meta.main) {
  try {qualify(process.argv[2])} catch {console.error('platform feed qualification failed'); process.exitCode = 1}
}
