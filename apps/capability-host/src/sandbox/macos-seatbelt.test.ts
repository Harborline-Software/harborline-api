import { ChildProcess, spawn } from 'node:child_process'
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { PassThrough } from 'node:stream'

import { afterEach, describe, expect, it, vi } from 'vitest'

import { buildSeatbeltProfile, MacosSeatbeltSandbox } from './macos-seatbelt.js'
import { boundedTimeoutMs, MAX_SANDBOX_TIMEOUT_MS, SandboxSpecError, type SandboxSpec } from './sandbox.js'

vi.mock('node:child_process', async (importOriginal) => ({
  ...await importOriginal<typeof import('node:child_process')>(),
  spawn: vi.fn(),
}))

vi.mock('node:fs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('node:fs')>()
  return {
    ...actual,
    mkdtempSync: vi.fn(actual.mkdtempSync),
    writeFileSync: vi.fn(actual.writeFileSync),
  }
})

const spec: SandboxSpec = { command: '/usr/bin/say', args: [], workDir: '/tmp/capability-test' }

class FakeChild extends ChildProcess {
  override stdout = new PassThrough()
  override stderr = new PassThrough()
  override kill = vi.fn<ChildProcess['kill']>(() => true)
}

afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
  vi.mocked(spawn).mockReset()
})

describe('sandbox timeout bounds', () => {
  it.each([
    [undefined, 5000], [NaN, 5000], [-1, 5000], [0, 5000],
    [Infinity, 5000], [-Infinity, 5000], [2 ** 31, 1_800_000],
    [5000, 5000], [1_800_000, 1_800_000],
  ])('bounds %s to %s', (requested, expected) => {
    expect(boundedTimeoutMs(requested, 5000)).toBe(expected)
  })

  it('pins the project ceiling to thirty minutes', () => {
    expect(MAX_SANDBOX_TIMEOUT_MS).toBe(1_800_000)
  })

  it.each([0, -1, NaN, Infinity, -Infinity, 1_800_001])('rejects invalid sandbox timeout %s', (timeoutMs) => {
    expect(() => buildSeatbeltProfile({ ...spec, timeoutMs }, [])).toThrow(SandboxSpecError)
  })

  it('accepts the maximum and an omitted timeout', () => {
    expect(() => buildSeatbeltProfile({ ...spec, timeoutMs: 1_800_000 }, [])).not.toThrow()
    expect(() => buildSeatbeltProfile(spec, [])).not.toThrow()
  })
})

describe('seatbelt child lifecycle with mocked spawn', () => {
  function start(timeoutMs = 1000) {
    const child = new FakeChild()
    vi.mocked(spawn).mockReturnValue(child)
    const run = new MacosSeatbeltSandbox([]).run({ ...spec, timeoutMs })
    const args = vi.mocked(spawn).mock.calls[0]?.[1]
    if (!Array.isArray(args) || typeof args[1] !== 'string') throw new Error('expected profile argument')
    return { child, run, profilePath: args[1] }
  }

  it('kills on timeout, settles on close, and releases its timer and profile', async () => {
    vi.useFakeTimers()
    const { child, run, profilePath } = start()
    expect(existsSync(profilePath)).toBe(true)
    await vi.advanceTimersByTimeAsync(1000)
    expect(child.kill).toHaveBeenCalledExactlyOnceWith('SIGKILL')
    child.emit('close', null, 'SIGKILL')
    await expect(run).resolves.toMatchObject({ exitCode: null, signal: 'SIGKILL', ok: false })
    expect(vi.getTimerCount()).toBe(0)
    expect(existsSync(profilePath)).toBe(false)
  })

  it('clears the timer after normal close without killing', async () => {
    vi.useFakeTimers()
    const { child, run, profilePath } = start()
    child.emit('close', 0, null)
    await expect(run).resolves.toMatchObject({ ok: true })
    expect(vi.getTimerCount()).toBe(0)
    await vi.advanceTimersByTimeAsync(1000)
    expect(child.kill).not.toHaveBeenCalled()
    expect(existsSync(profilePath)).toBe(false)
  })

  it('clears the timer and profile after spawn error', async () => {
    vi.useFakeTimers()
    const { child, run, profilePath } = start()
    const rejected = expect(run).rejects.toThrow('simulated spawn error')
    child.emit('error', new Error('simulated spawn error'))
    await rejected
    expect(vi.getTimerCount()).toBe(0)
    expect(existsSync(profilePath)).toBe(false)
  })

  it('removes the profile directory when writing the profile fails', async () => {
    const actual = await vi.importActual<typeof import('node:fs')>('node:fs')
    const dir = mkdtempSync(join(tmpdir(), 'capability-seatbelt-test-'))
    vi.mocked(mkdtempSync).mockReturnValue(dir)
    vi.mocked(writeFileSync).mockImplementation(() => { throw new Error('profile write failed') })
    try {
      await expect(new MacosSeatbeltSandbox([]).run(spec)).rejects.toThrow('profile write failed')
      expect(existsSync(dir)).toBe(false)
      expect(spawn).not.toHaveBeenCalled()
    } finally {
      vi.mocked(mkdtempSync).mockImplementation(actual.mkdtempSync)
      vi.mocked(writeFileSync).mockImplementation(actual.writeFileSync)
      rmSync(dir, { recursive: true, force: true })
    }
  })
})
