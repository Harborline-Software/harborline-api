import { describe, expect, it, vi } from 'vitest'
import type { CapabilityResult, InvokeRequest } from '@harborline-software/api-contracts'

import { secureInvoke } from '../membrane/pep.js'
import { currentHostPrincipal } from '../membrane/host-principal.js'
import type { RuntimeTransport } from '../membrane/runtime-transport.js'
import { CapabilityShell } from '../shell/capability-shell.js'
import {
  createSecuredCapabilityInvoke,
  createProductionCapabilityPortAdapter,
  CapabilityShellPortAdapter,
} from './capability-port-adapter.js'

const success: CapabilityResult = {
  jobId: 'job-port-1',
  status: 'succeeded',
  progress: 1,
  artifacts: [],
  usage: { unit: 'call', quantity: 1, tier: 'local' },
  error: null,
}

function shell(): CapabilityShell {
  return new CapabilityShell({
    composition: {
      solutionId: 'harborline',
      name: 'Harborline',
      capabilities: [
        { capabilityId: 'image', membership: 'core' },
        { capabilityId: 'tts', membership: 'n-a' },
      ],
    },
    negotiationProfile: {
      contractVersion: '1.0.0',
      supportedSchemaVersions: { image: '1.0.0' },
    },
  })
}

function negotiationTransport(): RuntimeTransport {
  return {
    runtimeId: 'runtime-negotiation',
    mode: 'in-process',
    announce: vi.fn().mockResolvedValue({
      runtimeId: 'runtime-negotiation',
      name: 'Negotiated runtime',
      contractVersion: '1.0.0',
      capabilities: [{ capabilityId: 'image', schemaVersion: '1.0.0', providers: [] }],
    }),
    negotiateOffer: vi.fn().mockResolvedValue({
      contractVersion: '1.0.0',
      capabilitySchemaVersions: { image: '1.0.0' },
    }),
    health: vi.fn().mockResolvedValue({ kind: 'liveness', state: 'up' }),
    invoke: vi.fn().mockResolvedValue(success),
    cancel: vi.fn().mockResolvedValue(undefined),
  }
}

async function connectedShell(): Promise<CapabilityShell> {
  const capability = shell()
  await capability.connect(negotiationTransport())
  return capability
}

function invokeRequest(correlationId: string): InvokeRequest {
  return {
    capabilityId: 'image',
    core: { prompt: 'harbor at dawn' } as never,
    providerInputs: {},
    attachments: [],
    idempotencyKey: `idem-${correlationId}`,
    correlationId,
    transport: 'sync',
  }
}

describe('CapabilityShellPortAdapter', () => {
  it('rejects a caller declaring an incompatible contract version', async () => {
    const capability = await connectedShell()
    const adapter = new CapabilityShellPortAdapter({ shell: capability })

    await expect(adapter.negotiate({
      runtimeId: 'runtime-negotiation',
      contractVersion: '2.0.0',
      capabilitySchemaVersions: { image: '1.0.0' },
    })).resolves.toMatchObject({
      compatible: false,
      acceptedCapabilities: [],
      reason: expect.stringContaining('contract-version mismatch'),
    })
  })

  it('does not accept a capability whose declared schema major is unsupported', async () => {
    const capability = await connectedShell()
    const adapter = new CapabilityShellPortAdapter({ shell: capability })

    await expect(adapter.negotiate({
      runtimeId: 'runtime-negotiation',
      contractVersion: '1.0.0',
      capabilitySchemaVersions: { image: '2.0.0' },
    })).resolves.toMatchObject({
      compatible: true,
      acceptedCapabilities: [],
      reason: null,
    })
  })

  it('fails closed for an unparseable capability schema declaration', async () => {
    const capability = await connectedShell()
    const adapter = new CapabilityShellPortAdapter({ shell: capability })

    await expect(adapter.negotiate({
      runtimeId: 'runtime-negotiation',
      contractVersion: '1.0.0',
      capabilitySchemaVersions: { image: 'not-a-version' },
    })).resolves.toMatchObject({
      compatible: false,
      acceptedCapabilities: [],
      reason: expect.stringContaining('capability-schema-version invalid'),
    })
  })

  it('preserves compatible negotiation while honoring the caller capability declaration', async () => {
    const capability = await connectedShell()
    const adapter = new CapabilityShellPortAdapter({ shell: capability })

    await expect(adapter.negotiate({
      runtimeId: 'runtime-negotiation',
      contractVersion: '1.0.0',
      capabilitySchemaVersions: { image: '1.0.0' },
    })).resolves.toEqual({
      compatible: true,
      agreedContractVersion: '1.0.0',
      acceptedCapabilities: ['image'],
      reason: null,
    })

    await expect(adapter.negotiate({
      runtimeId: 'runtime-negotiation',
      contractVersion: '1.0.0',
      capabilitySchemaVersions: {},
    })).resolves.toEqual({
      compatible: true,
      agreedContractVersion: '1.0.0',
      acceptedCapabilities: [],
      reason: null,
    })
  })

  it('rejects a negotiate request with an absent contract declaration at the inbound boundary', async () => {
    const capability = await connectedShell()
    const adapter = new CapabilityShellPortAdapter({ shell: capability })

    await expect(adapter.negotiate({
      runtimeId: 'runtime-negotiation',
      capabilitySchemaVersions: { image: '1.0.0' },
    } as never)).rejects.toMatchObject({
      code: 'protocol.invalid_inbound_payload',
      direction: 'inbound',
      operationId: 'capability.negotiate',
    })
  })

  it('routes protocol Invoke through the internally composed secured entrypoint', async () => {
    const capability = shell()
    const runtime = vi.spyOn(capability, 'invoke').mockResolvedValue(success)
    const adapter = new CapabilityShellPortAdapter({ shell: capability })

    await expect(adapter.invoke({
      capability: 'image',
      core: { prompt: 'harbor at dawn' },
      correlationId: 'corr-port-1',
      idempotencyKey: 'idem-port-1',
    })).resolves.toBe(success)

    expect(runtime).toHaveBeenCalledOnce()
    expect(runtime).toHaveBeenCalledWith(expect.objectContaining({
      capabilityId: 'image',
      correlationId: 'corr-port-1',
      idempotencyKey: 'idem-port-1',
    }), expect.objectContaining({
      principal: expect.objectContaining({ id: currentHostPrincipal().id }),
    }))
  })

  it('rejects a malformed runtime result at the generated boundary', async () => {
    const capability = shell()
    const malformed = { ...success } as Partial<CapabilityResult>
    delete malformed.usage
    vi.spyOn(capability, 'invoke').mockResolvedValue(malformed as CapabilityResult)
    const adapter = new CapabilityShellPortAdapter({ shell: capability })

    await expect(adapter.invoke({
      capability: 'image',
      core: { prompt: 'harbor at dawn' },
      correlationId: 'corr-malformed-result',
      idempotencyKey: 'idem-malformed-result',
    })).rejects.toMatchObject({
      code: 'protocol.invalid_outbound_payload',
      direction: 'outbound',
      operationId: 'capability.invoke',
    })
  })

  it('composes its secured entrypoint through policy before reaching the runtime', async () => {
    const capability = shell()
    const runtime = vi.spyOn(capability, 'invoke').mockResolvedValue(success)
    const adapter = new CapabilityShellPortAdapter({ shell: capability })

    await expect(adapter.invoke({
      capability: 'image',
      core: { prompt: 'harbor at dawn' },
      correlationId: 'corr-secured-positive',
      idempotencyKey: 'idem-secured-positive',
    })).resolves.toMatchObject({ status: 'succeeded' })

    expect(runtime).toHaveBeenCalledOnce()
  })

  it('composes the compatibility factory without accepting credential inputs', async () => {
    const trustedShell = shell()
    const trustedRuntime = vi.spyOn(trustedShell, 'invoke').mockResolvedValue(success)
    const invoke = createSecuredCapabilityInvoke({
      shell: trustedShell,
    } as never)

    await expect(invoke({
      capabilityId: 'image',
      core: {
        prompt: 'harbor at dawn',
        size: { w: 256, h: 256 },
        seed: 1,
        count: 1,
        format: 'png',
        timeout: 30_000,
      },
      providerInputs: {},
      attachments: [],
      correlationId: 'corr-secure-options-mutation',
      idempotencyKey: 'idem-secure-options-mutation',
      transport: 'sync',
    })).resolves.toBe(success)

    expect(trustedRuntime).toHaveBeenCalledOnce()
  })

  it('denies Secure preflight when no policy inspector is configured', async () => {
    const capability = shell()
    const adapter = new CapabilityShellPortAdapter({ shell: capability })
    await expect(adapter.secure({
      operationId: 'capability.invoke',
      capabilityId: 'image',
      correlationId: 'corr-port-2',
    })).resolves.toEqual(expect.objectContaining({ allowed: false, authority: 'CP' }))
  })

  it('projects composition membership without exposing shell implementation details', async () => {
    const capability = shell()
    const adapter = new CapabilityShellPortAdapter({ shell: capability })
    await expect(adapter.compose({
      editionId: 'harborline',
      capabilityIds: ['image', 'tts'],
    })).resolves.toEqual({
      editionId: 'harborline',
      acceptedCapabilityIds: ['image'],
      rejectedCapabilityIds: ['tts'],
    })
  })

  it('composes the production adapter with the host principal and ADR 0128 PDP', async () => {
    const capability = shell()
    const runtime = vi.spyOn(capability, 'invoke').mockResolvedValue(success)
    const adapter = createProductionCapabilityPortAdapter(capability)

    await expect(adapter.invoke({
      capability: 'image',
      core: { prompt: 'harbor at dawn' },
      correlationId: 'corr-production-composition',
      idempotencyKey: 'idem-production-composition',
    })).resolves.toBe(success)

    expect(runtime).toHaveBeenCalledOnce()
  })

  it('rejects a registry CP command without a broker token before reaching the shell', async () => {
    const capability = shell()
    const runtime = vi.spyOn(capability, 'invoke').mockResolvedValue(success)
    const principal = currentHostPrincipal()
    const request = invokeRequest('corr-cp-without-token')
    const result = await secureInvoke(
      { command: 'demo-cp-op', capabilityId: 'image', request },
      principal,
      (securedRequest) => capability.invoke(securedRequest, { principal }),
    )

    expect(result).toMatchObject({
      status: 'failed',
      error: { code: 'membrane.authority_rejected' },
    })
    expect(runtime).not.toHaveBeenCalled()
  })
})
