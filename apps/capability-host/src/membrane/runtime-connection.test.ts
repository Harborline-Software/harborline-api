import type { CancelRequest, InvokeRequest } from '@harborline-software/api-contracts'
import { describe, expect, it, vi } from 'vitest'

import { type ShellNegotiationProfile } from './negotiate.js'
import {
  cancelOnConnection,
  connectRuntime,
  invokeOnConnection,
  probeRuntime,
} from './runtime-connection.js'
import type { RuntimeTransport } from './runtime-transport.js'

const shell: ShellNegotiationProfile = {
  contractVersion: '1.2.0',
  supportedSchemaVersions: { image: '1.0.0' },
}

// A REAL `image` core, not an empty object behind a cast. The transport is stubbed, so the core's
// contents never matter to these assertions — but a request that could not exist is exactly the
// shape a typecheck is supposed to catch, so it is spelled out in full.
const request: InvokeRequest = {
  capabilityId: 'image',
  core: {
    prompt: 'a test image',
    size: { w: 512, h: 512 },
    seed: 1,
    count: 1,
    format: 'png',
    timeout: 30_000,
  },
  providerInputs: {},
  attachments: [],
  idempotencyKey: 'idem-1',
  correlationId: 'corr-1',
  transport: 'sync',
}

function transport(overrides: Partial<RuntimeTransport> = {}): RuntimeTransport {
  return {
    runtimeId: 'runtime-1',
    mode: 'in-process',
    announce: vi.fn().mockResolvedValue({
      runtimeId: 'runtime-1',
      name: 'Reference runtime',
      contractVersion: '1.0.0',
      capabilities: [{ capabilityId: 'image', schemaVersion: '1.0.0', providers: [] }],
    }),
    negotiateOffer: vi.fn().mockResolvedValue({
      contractVersion: '1.0.0',
      capabilitySchemaVersions: { image: '1.0.0' },
    }),
    health: vi.fn().mockImplementation(async (kind) => ({ kind, state: 'up' })),
    invoke: vi.fn().mockResolvedValue({ status: 'succeeded', jobId: 'runtime-job' }),
    cancel: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  } as RuntimeTransport
}

describe('runtime connection', () => {
  it('connects by announcing, indexing, and negotiating the runtime capabilities', async () => {
    const runtime = transport()
    const connection = await connectRuntime(runtime, shell)

    expect(runtime.announce).toHaveBeenCalledOnce()
    expect(runtime.negotiateOffer).toHaveBeenCalledOnce()
    expect(connection.runtimeId).toBe('runtime-1')
    expect(connection.transport).toBe(runtime)
    expect(connection.registry.byCapability.get('image')?.schemaVersion).toBe('1.0.0')
    expect(connection.negotiation).toMatchObject({ compatible: true, acceptedCapabilities: ['image'] })
  })

  it('retains empty capabilities and an invalid contract offer as an incompatible connection', async () => {
    const runtime = transport({
      announce: vi.fn().mockResolvedValue({
        runtimeId: 'empty-runtime', name: '', contractVersion: 'invalid', capabilities: [],
      }),
      negotiateOffer: vi.fn().mockResolvedValue({ contractVersion: 'invalid', capabilitySchemaVersions: {} }),
    })

    const connection = await connectRuntime(runtime, shell)

    expect(connection.registry.byCapability.size).toBe(0)
    expect(connection.negotiation).toMatchObject({ compatible: false, acceptedCapabilities: [] })
  })

  it('probes a connected runtime and turns a thrown probe into down', async () => {
    const healthy = await connectRuntime(transport(), shell)
    expect(await probeRuntime(healthy, 'liveness')).toEqual({ kind: 'liveness', state: 'up' })

    const failing = await connectRuntime(transport({ health: vi.fn().mockRejectedValue('offline') }), shell)
    expect(await probeRuntime(failing, 'readiness')).toEqual({
      kind: 'readiness', state: 'down', detail: 'offline',
    })
  })

  it('invokes only negotiated capabilities and rejects an invalid capability before the transport', async () => {
    const runtime = transport()
    const connection = await connectRuntime(runtime, shell)

    await expect(invokeOnConnection(connection, request)).resolves.toMatchObject({
      jobId: 'runtime-job', status: 'succeeded', progress: 1,
    })
    await expect(invokeOnConnection(connection, { ...request, capabilityId: 'unknown' } as InvokeRequest)).resolves.toMatchObject({
      jobId: 'job:runtime-1:rejected:corr-1',
      status: 'failed',
      error: { code: 'membrane.capability_not_negotiated', faultDomain: 'membrane' },
    })
    expect(runtime.invoke).toHaveBeenCalledTimes(1)
  })

  it('cancels a job through the connection transport, including an empty boundary job id', async () => {
    const runtime = transport()
    const connection = await connectRuntime(runtime, shell)
    const cancellation = { jobId: '', correlationId: 'cancel-1' } as CancelRequest

    await expect(cancelOnConnection(connection, cancellation)).resolves.toBeUndefined()
    expect(runtime.cancel).toHaveBeenCalledWith(cancellation)
  })
})
