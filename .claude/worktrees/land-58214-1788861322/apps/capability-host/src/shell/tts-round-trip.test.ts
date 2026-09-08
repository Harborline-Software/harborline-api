/**
 * Phase-3 ACCEPTANCE — the reference edition composes its REAL `tts` capability and an Invoke
 * round-trips through the membrane to a REAL subprocess runtime running INSIDE
 * the OS-native sandbox, with the uniform envelope + correlation-id.
 *
 * This is the v0-DONE proof: NOT the in-memory reference stub, but a genuinely
 * spawned subprocess (the TTS floor) confined by the seatbelt sandbox, reached
 * over BOTH v0 Address arms (in-process + loopback-HTTP local-subprocess), with
 * the uniform @harborline-software/api-contracts envelope holding end-to-end.
 *
 * Host-gated: the real spawn + confinement is darwin-only (`say` + `sandbox-exec`).
 * The non-darwin host skips the real-spawn suite and asserts the membrane wiring
 * (Announce/Negotiate/resolution) with an injected stub sandbox.
 */

import { existsSync, readFileSync, statSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

import type { CapabilityResult, InvokeRequest, TtsCore } from '@harborline-software/api-contracts'
import { afterEach, describe, expect, it } from 'vitest'

import { REFERENCE_APP_COMPOSITION } from '../editions/harborline.js'
import { InProcessTransport } from '../membrane/in-process-transport.js'
import { LoopbackHttpTransport } from '../membrane/loopback-transport.js'
import { InMemoryLogSink, RedactingLogSink } from '../membrane/observe.js'
import type { RuntimeTransport } from '../membrane/runtime-transport.js'
import {
  SayTtsRuntime,
  TTS_RUNTIME_ID,
} from '../runtime/say-tts-runtime.js'
import {
  startLoopbackRuntime,
  type RunningLoopbackRuntime,
} from '../runtime/loopback-runtime-server.js'
import { defaultShellNegotiationProfile, CapabilityShell } from './capability-shell.js'

const onDarwin = process.platform === 'darwin'

function ttsRequest(correlationId: string): InvokeRequest {
  const core: TtsCore = {
    text: 'Capability Harborline reference edition, sandbox confirmed.',
    voice: null,
    format: 'aiff',
    timeout: 30_000,
  }
  return {
    capabilityId: 'tts',
    core,
    providerInputs: {},
    attachments: [],
    idempotencyKey: `idem-${correlationId}`,
    correlationId,
    transport: 'sync',
  }
}

/** Assert the uniform RESULT envelope holds (six fixed top-level fields). */
function expectUniformEnvelope(env: CapabilityResult): void {
  expect(Object.keys(env).sort()).toEqual(
    ['artifacts', 'error', 'jobId', 'progress', 'status', 'usage'].sort(),
  )
}

describe.runIf(onDarwin)('Phase-3 ACCEPTANCE — real tts subprocess inside the sandbox', () => {
  const servers: RunningLoopbackRuntime[] = []
  afterEach(async () => {
    while (servers.length > 0) await servers.pop()!.stop()
  })

  /**
   * The full v0-DONE lifecycle: the reference edition boots → connect the REAL tts runtime →
   * resolve `tts` (core to the reference edition) → Observe(health) → Invoke → a REAL audio
   * file is produced by a CONFINED subprocess; uniform envelope + correlationId.
   */
  async function runTtsRoundTrip(makeTransport: () => RuntimeTransport): Promise<void> {
    const log = new InMemoryLogSink()
    const shell = new CapabilityShell({
      composition: REFERENCE_APP_COMPOSITION,
      negotiationProfile: defaultShellNegotiationProfile(),
      invokeContext: { logSink: new RedactingLogSink(log) },
    })

    // Announce + Negotiate.
    const connection = await shell.connect(makeTransport())
    expect(connection.runtimeId).toBe(TTS_RUNTIME_ID)
    expect(connection.negotiation.compatible).toBe(true)
    expect(connection.negotiation.acceptedCapabilities).toContain('tts')

    // Resolve — `tts` is `core` to the reference edition → available.
    const resolution = shell.resolve('tts')
    expect(resolution.resolutionState).toBe('available')
    expect(resolution.chosenProviderId).toBe('macos-say-floor')

    // Observe(health) — tri-state.
    const health = await shell.probe(TTS_RUNTIME_ID, 'readiness')
    expect(health.state).toBe('up')

    // Invoke — the REAL subprocess runs CONFINED; uniform envelope.
    const result = await shell.invoke(ttsRequest('corr-tts-1'))
    expectUniformEnvelope(result)
    expect(result.status).toBe('succeeded')
    expect(result.error).toBeNull()
    expect(result.usage.unit).toBe('audio')

    const artifact = result.artifacts[0]
    if (artifact === undefined) throw new Error('expected the invoke to return one artifact')
    expect(artifact.kind).toBe('audio')

    // The audio file actually exists on disk and is non-empty — proof the REAL
    // subprocess ran to completion INSIDE the sandbox (not a stub echo).
    const audioPath = fileURLToPath((artifact as { uri: string }).uri)
    expect(existsSync(audioPath)).toBe(true)
    expect(statSync(audioPath).size).toBeGreaterThan(0)
    // AIFF magic: bytes 8..12 are 'AIFF'/'AIFC' (FORM....AIFF) — it's real audio.
    const head = readFileSync(audioPath).subarray(0, 12).toString('latin1')
    expect(head.startsWith('FORM')).toBe(true)
    expect(head.includes('AIF')).toBe(true)

    // correlationId propagated end-to-end + logged.
    expect(result.jobId).toContain('corr-tts-1')
    expect(log.records.some((r) => r.correlationId === 'corr-tts-1')).toBe(true)
  }

  it('round-trips over the in-process transport (Address: in-process)', async () => {
    await runTtsRoundTrip(
      () => new InProcessTransport(TTS_RUNTIME_ID, new SayTtsRuntime()),
    )
  })

  it('round-trips over the loopback-HTTP transport (Address: local-subprocess)', async () => {
    // The local-subprocess arm: the tts RuntimeHost exposed over 127.0.0.1; the
    // Invoke crosses the loopback hop, the runtime spawns `say` CONFINED, the
    // audio comes back through the uniform envelope.
    const runtime = new SayTtsRuntime()
    const server = await startLoopbackRuntime(runtime)
    servers.push(server)
    await runTtsRoundTrip(
      () => new LoopbackHttpTransport(TTS_RUNTIME_ID, server.baseUrl),
    )
  })

  it('the runtime spawned the engine THROUGH the sandbox (never an unconfined spawn)', async () => {
    // Observe that the runtime routed its spawn through the sandbox spec — the
    // structural guarantee that no capability engine runs unconfined.
    const runtime = new SayTtsRuntime()
    const shell = new CapabilityShell({
      composition: REFERENCE_APP_COMPOSITION,
      negotiationProfile: defaultShellNegotiationProfile(),
    })
    await shell.connect(new InProcessTransport(TTS_RUNTIME_ID, runtime))
    await shell.invoke(ttsRequest('corr-tts-confined'))
    expect(runtime.ranSpecs).toHaveLength(1)
    const spec = runtime.ranSpecs[0]
    if (spec === undefined) throw new Error('expected the runtime to have recorded one spec')
    expect(spec.command).toBe('/usr/bin/say')
    // (c) the tts floor declares NO egress.
    expect(spec.allowedEgress).toEqual([])
    // (b) it writes ONLY its per-Invoke work dir.
    expect(spec.workDir).toContain('capability-tts-')
  })

  it('Observe reports `degraded` (tri-state) when the tts floor is throttled', async () => {
    const runtime = new SayTtsRuntime()
    runtime.setDegraded(true)
    const shell = new CapabilityShell({
      composition: REFERENCE_APP_COMPOSITION,
      negotiationProfile: defaultShellNegotiationProfile(),
    })
    await shell.connect(new InProcessTransport(TTS_RUNTIME_ID, runtime))
    const health = await shell.probe(TTS_RUNTIME_ID, 'liveness')
    expect(health.state).toBe('degraded')
  })
})

// --- platform-neutral wiring (runs everywhere, injected stub sandbox) --------
describe('tts membrane wiring (platform-neutral, stub sandbox)', () => {
  it('Harborline composes `tts` as a core capability', () => {
    const entry = REFERENCE_APP_COMPOSITION.capabilities.find((c) => c.capabilityId === 'tts')
    expect(entry?.membership).toBe('core')
  })

  it('an Invoke round-trips with the uniform envelope using an injected stub sandbox', async () => {
    // No real spawn: the stub sandbox returns ok+writes a fake file so the wiring
    // (Announce/Negotiate/resolve/Invoke + uniform envelope) is proven off-darwin too.
    const stubSandbox = {
      platform: process.platform,
      implemented: true,
      run: async (spec: { workDir: string }) => {
        // emulate a produced output file
        const { writeFileSync } = await import('node:fs')
        const { join } = await import('node:path')
        writeFileSync(join(spec.workDir, 'out.aiff'), 'FORM    AIFF')
        return { exitCode: 0, signal: null, stdout: '', stderr: '', ok: true }
      },
    }
    const runtime = new SayTtsRuntime({ sandbox: stubSandbox })
    const shell = new CapabilityShell({
      composition: REFERENCE_APP_COMPOSITION,
      negotiationProfile: defaultShellNegotiationProfile(),
    })
    await shell.connect(new InProcessTransport(TTS_RUNTIME_ID, runtime))
    const result = await shell.invoke(ttsRequest('corr-stub'))
    expectUniformEnvelope(result)
    expect(result.status).toBe('succeeded')
    const stubArtifact = result.artifacts[0]
    if (stubArtifact === undefined) throw new Error('expected the invoke to return one artifact')
    expect(stubArtifact.kind).toBe('audio')
  })

  it('a sandbox-unsupported platform fails closed (no unconfined spawn) into the uniform envelope', async () => {
    const failingSandbox = {
      platform: 'linux' as NodeJS.Platform,
      implemented: false,
      run: () =>
        Promise.reject(
          Object.assign(new Error('SEC-7: not implemented'), { code: 'sandbox.platform_unsupported' }),
        ),
    }
    const runtime = new SayTtsRuntime({ sandbox: failingSandbox })
    const shell = new CapabilityShell({
      composition: REFERENCE_APP_COMPOSITION,
      negotiationProfile: defaultShellNegotiationProfile(),
    })
    await shell.connect(new InProcessTransport(TTS_RUNTIME_ID, runtime))
    const result = await shell.invoke(ttsRequest('corr-unsupported'))
    expectUniformEnvelope(result)
    expect(result.status).toBe('failed')
    expect(result.error?.faultDomain).toBe('membrane')
    expect(result.error?.code).toBe('membrane.sandbox_fault')
  })
})
