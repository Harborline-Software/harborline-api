import type { CapabilityResult, ImageArtifact, InvokeRequest } from '@harborline-software/api-contracts'
import { afterEach, describe, expect, it } from 'vitest'

import { REFERENCE_APP_COMPOSITION } from '../editions/harborline.js'
import { InProcessTransport } from '../membrane/in-process-transport.js'
import { LoopbackHttpTransport } from '../membrane/loopback-transport.js'
import { InMemoryLogSink, RedactingLogSink } from '../membrane/observe.js'
import type { RuntimeTransport } from '../membrane/runtime-transport.js'
import {
  ReferenceImageRuntime,
  REFERENCE_RUNTIME_ID,
} from '../runtime/reference-image-runtime.js'
import {
  startLoopbackRuntime,
  type RunningLoopbackRuntime,
} from '../runtime/loopback-runtime-server.js'
import { defaultShellNegotiationProfile, CapabilityShell } from './capability-shell.js'

function imageRequest(correlationId: string): InvokeRequest {
  return {
    capabilityId: 'image',
    core: {
      prompt: 'a carrier at dawn',
      size: { w: 768, h: 768 },
      seed: 42,
      count: 1,
      format: 'png',
      timeout: 30_000,
    },
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

describe('Acceptance — membrane round-trip (Announce→Negotiate→Invoke→Observe)', () => {
  const servers: RunningLoopbackRuntime[] = []
  afterEach(async () => {
    while (servers.length > 0) await servers.pop()!.stop()
  })

  /** Run the full lifecycle against a given transport (both v0 Address arms). */
  async function runRoundTrip(makeTransport: () => RuntimeTransport): Promise<void> {
    const log = new InMemoryLogSink()
    const shell = new CapabilityShell({
      composition: REFERENCE_APP_COMPOSITION,
      negotiationProfile: defaultShellNegotiationProfile(),
      invokeContext: { logSink: new RedactingLogSink(log) },
    })

    // --- Announce + Negotiate (connect) ---
    const connection = await shell.connect(makeTransport())
    expect(connection.runtimeId).toBe(REFERENCE_RUNTIME_ID)
    expect(connection.negotiation.compatible).toBe(true)
    expect(connection.negotiation.acceptedCapabilities).toContain('image')

    // --- Resolve (D7) — image is `core` to the reference edition → available ---
    const resolution = shell.resolve('image')
    expect(resolution.resolutionState).toBe('available')
    expect(resolution.chosenProviderId).toBe('reference-stub-image')

    // --- Observe (health) — tri-state ---
    const health = await shell.probe(REFERENCE_RUNTIME_ID, 'readiness')
    expect(health.state).toBe('up')

    // --- Invoke — uniform envelope + correlationId propagation ---
    const result = await shell.invoke(imageRequest('corr-roundtrip-1'))
    expectUniformEnvelope(result)
    expect(result.status).toBe('succeeded')
    expect(result.error).toBeNull()
    expect(result.usage.unit).toBe('image')

    const artifact = result.artifacts[0] as ImageArtifact
    expect(artifact.kind).toBe('image')
    expect(artifact.w).toBe(768)
    expect(artifact.mime).toBe('image/png')

    // correlationId propagated end-to-end (jobId minted off it on the runtime side).
    expect(result.jobId).toContain('corr-roundtrip-1')

    // the Invoke was logged with the correlationId (Observe Part V).
    expect(log.records.some((r) => r.correlationId === 'corr-roundtrip-1')).toBe(true)
  }

  it('round-trips over the in-process transport (Address: in-process)', async () => {
    await runRoundTrip(() => new InProcessTransport(REFERENCE_RUNTIME_ID, new ReferenceImageRuntime()))
  })

  it('round-trips over the loopback-HTTP transport (Address: local-subprocess)', async () => {
    // The loopback arm: a RuntimeHost exposed over 127.0.0.1 (the local-subprocess
    // arm of the Address superset), proving the round-trip without an in-process call.
    const runtime = new ReferenceImageRuntime()
    const server = await startLoopbackRuntime(runtime)
    servers.push(server)
    await runRoundTrip(
      () => new LoopbackHttpTransport(REFERENCE_RUNTIME_ID, server.baseUrl),
    )
  })

  it('Observe reports `degraded` (tri-state) when the runtime is throttled', async () => {
    const runtime = new ReferenceImageRuntime()
    runtime.setDegraded(true)
    const shell = new CapabilityShell({
      composition: REFERENCE_APP_COMPOSITION,
      negotiationProfile: defaultShellNegotiationProfile(),
    })
    await shell.connect(new InProcessTransport(REFERENCE_RUNTIME_ID, runtime))
    const health = await shell.probe(REFERENCE_RUNTIME_ID, 'liveness')
    expect(health.state).toBe('degraded') // not a binary up/down
  })

  it('a provider fault surfaces the uniform error envelope (no fork)', async () => {
    const runtime = new ReferenceImageRuntime()
    runtime.setFailNextInvoke(true)
    const shell = new CapabilityShell({
      composition: REFERENCE_APP_COMPOSITION,
      negotiationProfile: defaultShellNegotiationProfile(),
    })
    await shell.connect(new InProcessTransport(REFERENCE_RUNTIME_ID, runtime))
    const result = await shell.invoke(imageRequest('corr-fail-1'))
    expectUniformEnvelope(result)
    expect(result.status).toBe('failed')
    expect(result.error?.faultDomain).toBe('provider')
    expect(result.error?.retryable).toBe(true)
  })

  it('a capability not in the edition (bank-import = n-a) is rejected before any runtime hop', async () => {
    const shell = new CapabilityShell({
      composition: REFERENCE_APP_COMPOSITION,
      negotiationProfile: defaultShellNegotiationProfile(),
    })
    await shell.connect(new InProcessTransport(REFERENCE_RUNTIME_ID, new ReferenceImageRuntime()))
    const req: InvokeRequest = {
      capabilityId: 'bank-import',
      core: {
        source: { kind: 'file', attachmentId: 'a', fileName: 's.csv' },
        accountId: 'acct',
        timeout: 1000,
      },
      providerInputs: {},
      attachments: [{ attachmentId: 'a', mime: 'text/csv' }],
      idempotencyKey: 'idem-bank',
      correlationId: 'corr-bank-na',
      transport: 'sync',
    }
    const result = await shell.invoke(req)
    expect(result.status).toBe('failed')
    expect(result.error?.faultDomain).toBe('membrane')
    expect(result.error?.code).toBe('membrane.resolution_not_in_edition')
  })
})
