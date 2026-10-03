// Qualification-only observations. Never retain Docker output or change a workload verdict.
import {execFileSync} from 'node:child_process'
import {lstatSync, mkdtempSync, readFileSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {buildEnvironment} from './platform-feed-environment.mjs'

export function crashSignatures(text) {
  // These are observed signatures, not inferred root causes. In particular 137/139
  // alone says nothing about OOM, resource limits or a runtime bug.
  const patterns = [
    ['segmentation-fault', /\bSegmentation fault\b|\bSIGSEGV\b/i],
    ['abort', /\bSIGABRT\b|\bAborted(?: \(core dumped\))?\b/],
    ['managed-out-of-memory', /\bSystem\.OutOfMemoryException\b/],
    ['access-violation', /\bSystem\.AccessViolationException\b/],
    ['allocation-failure', /\bOut of memory\b|\bCannot allocate memory\b|\bENOMEM\b/i],
    ['allocation-hresult', /\b0x8007000[8e]\b/i],
    ['thread-or-process-resource-unavailable', /\bResource temporarily unavailable\b|\bpthread_create[^\r\n]{0,120}\b(?:failed|EAGAIN)\b|\bFailed to create (?:a )?(?:thread|worker thread)\b/i],
    ['disk-full', /\bNo space left on device\b|\bENOSPC\b/i],
    ['stack-overflow', /\bStack overflow\b|\bSystem\.StackOverflowException\b/i],
    ['runtime-fail-fast', /\b(?:Environment\.)?FailFast\b|\bFatal error\. Internal CLR error\b/],
    ['runtime-assertion', /\bAssertion failed\b|\bAssert failure\b/i],
    ['access-denied', /\bPermission denied\b|\bOperation not permitted\b|\bUnauthorizedAccessException\b/i],
  ]
  return patterns.filter(([, pattern]) => pattern.test(text)).map(([name]) => name)
}

const inspectFormat = '{"exitCode":{{.State.ExitCode}},"oomKilled":{{.State.OOMKilled}},"running":{{.State.Running}},"memoryLimitBytes":{{.HostConfig.Memory}},"memorySwapLimitBytes":{{.HostConfig.MemorySwap}},"pidsLimit":{{json .HostConfig.PidsLimit}},"nanoCpus":{{.HostConfig.NanoCpus}},"errorMessage":{{json .State.Error}}}'
export function safeContainerState(raw) {
  if (typeof raw !== 'string' || Buffer.byteLength(raw) > 4096) throw new Error('unavailable container observation')
  const value = JSON.parse(raw)
  if (!value || Array.isArray(value) || typeof value !== 'object'
    || typeof value.oomKilled !== 'boolean' || typeof value.running !== 'boolean') throw new Error('invalid container observation')
  const result = {available: true, oomKilled: value.oomKilled, running: value.running,
    peakMemoryObserved: false, peakPidsObserved: false}
  for (const [name, minimum, maximum] of [['exitCode', 0, 255], ['memoryLimitBytes', 0, Number.MAX_SAFE_INTEGER],
    ['memorySwapLimitBytes', -1, Number.MAX_SAFE_INTEGER], ['nanoCpus', 0, Number.MAX_SAFE_INTEGER]]) {
    if (!Number.isSafeInteger(value[name]) || value[name] < minimum || value[name] > maximum) throw new Error('invalid container numeric observation')
    result[name] = value[name]
  }
  if (value.pidsLimit !== null && (!Number.isSafeInteger(value.pidsLimit) || value.pidsLimit < -1)) throw new Error('invalid container PID observation')
  result.pidsLimit = value.pidsLimit
  const signatures = typeof value.errorMessage === 'string' ? crashSignatures(value.errorMessage.slice(-2048)) : []
  if (signatures.length) result.runtimeObservedSignatures = signatures
  return result
}

export function diagnosticContainerRun({run = execFileSync, observe = () => {}} = {}) {
  return (command, args, options) => {
    if (command !== 'docker' || args[0] !== 'run' || args[1] !== '--rm') return run(command, args, options)
    let directory, cidFile
    try {
      directory = mkdtempSync(path.join(tmpdir(), 'api-feed-container-observation-'))
      cidFile = path.join(directory, 'container.cid')
    } catch {
      // Diagnostics setup is optional; the original invocation still decides the verdict.
      try {observe({available: false})} catch { /* Non-gating. */ }
      return run(command, args, options)
    }
    let cid
    const diagnosticOptions = {encoding: 'utf8', stdio: 'pipe', timeout: 5000, maxBuffer: 4096, env: buildEnvironment()}
    try {
      // Only lifecycle changes: keep the container until bounded inspection. The
      // exact image, workload arguments, mounts and security/resource flags stay intact.
      return run(command, ['run', '--cidfile', cidFile, ...args.slice(2)], options)
    } finally {
      let observation = {available: false}
      try {
        const stat = lstatSync(cidFile)
        if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 128) throw new Error('invalid owned container ID')
        cid = readFileSync(cidFile, 'utf8').trim()
        if (!/^[0-9a-f]{64}$/.test(cid)) {cid = undefined; throw new Error('invalid owned container ID')}
        observation = safeContainerState(run('docker', ['inspect', '--format', inspectFormat, cid], diagnosticOptions))
      } catch { /* No raw inspection failures leave this boundary. */ }
      let cleanupSucceeded = false
      if (cid) {
        try {run('docker', ['rm', '--force', cid], diagnosticOptions); cleanupSucceeded = true} catch { /* Preserve the workload verdict. */ }
      }
      try {observe({...observation, cleanupSucceeded})} catch { /* Non-gating. */ }
      try {rmSync(directory, {recursive: true, force: true})} catch { /* Owned temporary observation only. */ }
    }
  }
}
