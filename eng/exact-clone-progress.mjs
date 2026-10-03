import {spawnSync} from 'node:child_process'
import {appendFileSync, mkdirSync, writeSync} from 'node:fs'
import path from 'node:path'
import {isMainThread, Worker, workerData} from 'node:worker_threads'

// Only caller-assigned stage identifiers and bounded process metadata cross this boundary.
// Commands, arguments, environment values and child output can contain credentials.
const safeCode = value => typeof value === 'string' && /^[A-Z][A-Z0-9_]{0,63}$/.test(value) ? value : 'UNKNOWN'
const emit = (file, event) => {
  const line = `${JSON.stringify(event)}\n`
  appendFileSync(file, line)
  writeSync(1, `[exact-clone] ${line}`)
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

export function observedSpawnSync(id, executable, args, options, {file, intervalMs = 30_000}) {
  if (!/^[a-z][a-z0-9-]{0,79}$/.test(id)) throw new Error('Invalid exact-clone stage identifier')
  mkdirSync(path.dirname(file), {recursive: true})
  const started = Date.now()
  emit(file, {id, state: 'started', recordedAt: new Date(started).toISOString()})
  const shared = new SharedArrayBuffer(3 * Int32Array.BYTES_PER_ELEMENT)
  const state = new Int32Array(shared)
  const worker = new Worker(new URL(import.meta.url), {workerData: {file, id, started, shared, intervalMs}})
  worker.unref()
  if (Atomics.wait(state, 1, 0, 5_000) === 'timed-out') {
    worker.terminate()
    emit(file, {id, state: 'diagnostic-start-failed', elapsedMs: Date.now() - started})
    throw new Error('Exact-clone progress worker failed to start')
  }
  let result
  try {
    result = spawnSync(executable, args, options)
    return result
  } finally {
    Atomics.store(state, 0, 1)
    Atomics.notify(state, 0)
    const stopped = Atomics.wait(state, 2, 0, 5_000) !== 'timed-out'
    worker.terminate()
    emit(file, {id, state: 'completed', elapsedMs: Date.now() - started,
      exitCode: result?.status ?? null,
      ...(result?.signal ? {signal: safeCode(result.signal)} : {}),
      ...(result?.error ? {spawnError: safeCode(result.error.code)} : {}),
      ...(!result ? {spawnThrew: true} : {}),
      ...(!stopped ? {diagnosticStopTimedOut: true} : {})})
  }
}
