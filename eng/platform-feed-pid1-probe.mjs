// Failure-only diagnostic experiment. Never changes production settings or grants a verdict.
import {execFileSync} from 'node:child_process'
import {mkdirSync, mkdtempSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {buildEnvironment} from './platform-feed-environment.mjs'
import {diagnosticContainerRun} from './platform-feed-crash-diagnostics.mjs'

export function probePidOneRestore({args, image, commit, run = execFileSync, observe = () => {}, classifyFailure}) {
  // Accept only an already observed production restore and the independently pinned image.
  const imageIndex = args.indexOf(image)
  if (!/^mcr\.microsoft\.com\/dotnet\/sdk@sha256:[0-9a-f]{64}$/.test(image) || !/^[0-9a-f]{40}$/.test(commit)
    || args[0] !== 'run' || args[1] !== '--rm' || args.includes('--init') || imageIndex < 2
    || args[imageIndex + 1] !== 'dotnet' || args[imageIndex + 2] !== 'restore'
    || typeof args[imageIndex + 3] !== 'string' || !args[imageIndex + 3].startsWith('/platform/')
    || args[imageIndex + 3].split('/').includes('..') || !args[imageIndex + 3].endsWith('.csproj')
    || typeof classifyFailure !== 'function') throw new Error('unsupported diagnostic restore')
  const mount = target => {
    const matches = args.flatMap((arg, index) => arg === '--mount' && typeof args[index + 1] === 'string'
      ? [{index: index + 1, match: /^type=bind,source=([^,]+),target=([^,]+)$/.exec(args[index + 1])}] : [])
      .filter(item => item.match?.[2] === target)
    if (matches.length !== 1 || !path.isAbsolute(matches[0].match[1])) throw new Error('unsupported diagnostic mount')
    return {index: matches[0].index, source: matches[0].match[1]}
  }
  const platform = mount('/platform'), packages = mount('/packages')
  const directory = mkdtempSync(path.join(tmpdir(), 'api-feed-pid1-probe-'))
  const record = (stage, details) => {try {observe(stage, details)} catch { /* Diagnostics never decide a verdict. */ }}
  const childOptions = {encoding: 'utf8', stdio: 'pipe', timeout: 30000, maxBuffer: 4096, env: buildEnvironment()}
  const results = []
  try {
    // Three interleaved pairs prevent one stochastic success being treated as causal proof.
    // Every case restores committed source into a fresh package cache; failed prior
    // work, downloaded packages and obj state cannot bias the second member of a pair.
    for (let pair = 0; pair < 3; pair++) for (const init of (pair % 2 === 0 ? [false, true] : [true, false])) {
      const caseIndex = results.length, variant = init ? 'docker-init' : 'direct-pid1'
      const clone = path.join(directory, `case-${caseIndex}`), cache = path.join(directory, `packages-${caseIndex}`)
      const started = Date.now()
      let result = {caseIndex, pair, variant, succeeded: false}
      let phase = 'input-clone'
      record('pid1-probe-case-started', {caseIndex, pair, variant})
      try {
        run('git', ['-c', `safe.directory=${platform.source}`, 'clone', '--quiet', '--no-local', '--no-hardlinks', platform.source, clone], childOptions)
        phase = 'input-identity'
        const identity = run('git', ['-c', `safe.directory=${clone}`, '-C', clone, 'rev-parse', 'HEAD'], childOptions).trim()
        if (identity !== commit) throw new Error('diagnostic clone identity mismatch')
        mkdirSync(cache)
        const probeArgs = [...args]
        probeArgs[platform.index] = `type=bind,source=${clone},target=/platform`
        probeArgs[packages.index] = `type=bind,source=${cache},target=/packages`
        if (init) probeArgs.splice(2, 0, '--init')
        const invoke = diagnosticContainerRun({run, observe: state => record('pid1-probe-container-state', {caseIndex, pair, variant, state})})
        phase = 'container-restore'
        invoke('docker', probeArgs, {...childOptions, timeout: 180000, maxBuffer: 16 * 1024 * 1024})
        result.succeeded = true
      } catch (error) {
        // classifyFailure is the same bounded allowlist used for the original failure.
        result.failure = classifyFailure(error)
        result.phase = phase
      }
      result.durationMs = Date.now() - started
      results.push(result); record('pid1-probe-case-complete', result)
    }
    record('pid1-probe-complete', {cases: results.length, productionQualificationPassed: false,
      productionSettingsChanged: false, causalConclusionEstablished: false})
    return results
  } finally {try {rmSync(directory, {recursive: true, force: true})} catch { /* Owned diagnostic scratch only. */ }}
}
