import {spawnSync} from 'node:child_process'
import {appendFileSync, mkdirSync, writeFileSync, writeSync} from 'node:fs'
import path from 'node:path'
import {isMainThread, Worker, workerData} from 'node:worker_threads'

// Only caller-assigned stage identifiers and bounded process metadata cross this boundary.
// Commands, arguments, environment values and child output can contain credentials.
const safeCode = value => typeof value === 'string' && /^[A-Z][A-Z0-9_]{0,63}$/.test(value) ? value : 'UNKNOWN'
const emit = (file, event) => {
  const line = `${JSON.stringify(event)}\n`
  try { appendFileSync(file, line) } catch { /* Diagnostics cannot decide the stage verdict. */ }
  try { writeSync(1, `[exact-clone] ${line}`) } catch { /* A closed console must not skip the child. */ }
}

if (!isMainThread) {
  const {file, id, started, shared, intervalMs} = workerData
  const state = new Int32Array(shared)
  Atomics.store(state, 1, 1)
  Atomics.notify(state, 1)
  try {
    while (Atomics.wait(state, 0, 0, intervalMs) === 'timed-out') {
      if (Atomics.load(state, 0) !== 0) break
      emit(file, {id, state: 'running', elapsedMs: Date.now() - started})
    }
  } finally {
    Atomics.store(state, 2, 1)
    Atomics.notify(state, 2)
  }
}

// Best effort: failure to reset a journal must not prevent any gate command.
export function resetProgressFile(file) {
  try {
    mkdirSync(path.dirname(file), {recursive: true})
    writeFileSync(file, '')
  } catch { /* Existing gate commands remain authoritative. */ }
}

export function observedSpawnSync(id, executable, args, options,
  {file, intervalMs = 30_000, workerUrl = new URL(import.meta.url), waitMs = 5_000}) {
  const started = Date.now()
  let worker
  let state
  const enabled = /^[a-z][a-z0-9-]{0,79}$/.test(id)
  const terminate = () => {
    try { worker?.terminate()?.catch(() => {}) } catch { /* Never replace the child result. */ }
  }
  if (enabled) {
    try {
      mkdirSync(path.dirname(file), {recursive: true})
      emit(file, {id, state: 'started', recordedAt: new Date(started).toISOString()})
      const shared = new SharedArrayBuffer(3 * Int32Array.BYTES_PER_ELEMENT)
      state = new Int32Array(shared)
      worker = new Worker(workerUrl, {workerData: {file, id, started, shared, intervalMs}})
      // Worker failures may arrive after spawnSync unblocks. They remain diagnostic only.
      worker.on('error', () => emit(file, {id, state: 'diagnostic-worker-failed', elapsedMs: Date.now() - started}))
      worker.unref()
      if (Atomics.wait(state, 1, 0, waitMs) === 'timed-out') {
        terminate()
        emit(file, {id, state: 'diagnostic-start-failed', elapsedMs: Date.now() - started})
      }
    } catch {
      terminate()
      emit(file, {id, state: 'diagnostic-start-failed', elapsedMs: Date.now() - started})
    }
  }
  let result
  try {
    // Exact-clone commands use argv throughout. Environment-derived paths remain
    // literal argv values, even if an accidental caller supplies shell: true.
    result = spawnSync(executable, args, {...options, shell: false})
    return result
  } finally {
    let stopped = true
    try {
      if (state && worker) {
        Atomics.store(state, 0, 1)
        Atomics.notify(state, 0)
        stopped = Atomics.wait(state, 2, 0, waitMs) !== 'timed-out'
      }
    } catch { stopped = false }
    terminate()
    if (enabled) emit(file, {id, state: 'completed', elapsedMs: Date.now() - started,
      exitCode: result?.status ?? null,
      ...(result?.signal ? {signal: safeCode(result.signal)} : {}),
      ...(result?.error ? {spawnError: safeCode(result.error.code)} : {}),
      ...(!result ? {spawnThrew: true} : {}),
      ...(!stopped ? {diagnosticStopTimedOut: true} : {})})
  }
}
