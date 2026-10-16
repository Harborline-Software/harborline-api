import { existsSync, rmSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

import type { InvokeRequest, TtsCore } from '@harborline-software/api-contracts'
import { afterEach, describe, expect, it } from 'vitest'

import type { Sandbox, SandboxSpec } from '../sandbox/sandbox.js'
import { SayTtsRuntime } from './say-tts-runtime.js'

function request(timeout: number): InvokeRequest {
  const core: TtsCore = { text: 'test', voice: null, format: 'aiff', timeout }
  return {
    capabilityId: 'tts', core, providerInputs: {}, attachments: [],
    idempotencyKey: 'tts-test', correlationId: 'tts-test', transport: 'sync',
  }
}

describe('SayTtsRuntime bounded timeout and cleanup', () => {
  const directories: string[] = []
  afterEach(() => {
    for (const dir of directories.splice(0)) rmSync(dir, { recursive: true, force: true })
  })

  function sandbox(outcome: 'success' | 'empty' | 'timeout' | 'error', recorded: SandboxSpec[]): Sandbox {
    return {
      platform: process.platform,
      implemented: true,
      async run(spec) {
        recorded.push(spec)
        directories.push(spec.workDir)
        if (outcome === 'error') throw new Error('simulated spawn failure')
        if (outcome === 'timeout') {
          return { exitCode: null, signal: 'SIGKILL', stdout: '', stderr: '', ok: false }
        }
        writeFileSync(join(spec.workDir, 'out.aiff'), outcome === 'empty' ? '' : 'audio')
        return { exitCode: 0, signal: null, stdout: '', stderr: '', ok: true }
      },
    }
  }

  it.each([[2 ** 31, 1_800_000], [NaN, 30_000]])('bounds timeout %s to %s', async (timeout, expected) => {
    const recorded: SandboxSpec[] = []
    const runtime = new SayTtsRuntime({ sandbox: sandbox('success', recorded) })
    const result = await runtime.invoke(request(timeout))
    expect(result.status).toBe('succeeded')
    expect(recorded[0]?.timeoutMs).toBe(expected)
    const spec = recorded[0]
    if (spec === undefined) throw new Error('expected a sandbox invocation')
    expect(existsSync(join(spec.workDir, 'out.aiff'))).toBe(true)
  })

  it.each(['timeout', 'empty', 'error'] as const)('settles and removes its directory after %s', async (outcome) => {
    const recorded: SandboxSpec[] = []
    const runtime = new SayTtsRuntime({ sandbox: sandbox(outcome, recorded) })
    const result = await runtime.invoke(request(1000))
    expect(result.status).toBe('failed')
    const spec = recorded[0]
    if (spec === undefined) throw new Error('expected a sandbox invocation')
    expect(existsSync(spec.workDir)).toBe(false)
  })
})
