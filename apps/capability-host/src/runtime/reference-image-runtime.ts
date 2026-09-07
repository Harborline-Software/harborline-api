/**
 * The v0 REFERENCE loopback runtime — the runtime SIDE of the membrane.
 *
 * Implements ONE capability (`image`, a non-financial capability with the
 * simplest CORE) so the Announce → Negotiate → Invoke → Observe round-trip is
 * provable END-TO-END IN-REPO, without depending on the external Python workers
 * or the .NET local-node being up (those are Phase-3+ real-runtime wiring).
 *
 * It is deliberately trivial: a deterministic stub "renderer" that echoes the
 * CORE prompt/size/seed into an `ImageArtifact`. The POINT is the membrane
 * round-trip + the uniform envelope, not real image generation.
 *
 * The runtime already speaks the uniform @harborline-software/api-contracts envelope, but the
 * membrane still normalizes through M3 (it must not assume the envelope shape).
 */

import type {
  CancelRequest,
  CapabilityResult,
  HealthProbe,
  HealthState,
  ImageArtifact,
  ImageCore,
  InvokeRequest,
  NegotiateOffer,
  ProviderManifest,
} from '@harborline-software/api-contracts'

import type { RuntimeManifest } from '../membrane/announce.js'
import type { ProbeKind } from '../membrane/observe.js'
import type { RuntimeHost } from './runtime-host.js'

/** The reference runtime's stable id. */
export const REFERENCE_RUNTIME_ID = 'capability-reference-image-runtime'

/** The membrane contract version this v0 build speaks. */
export const CAPABILITY_CONTRACT_VERSION = '0.1.0'

/** The `image` capability schema version the reference runtime hosts. */
export const IMAGE_SCHEMA_VERSION = '1.0.0'

/** The provider this runtime exposes for `image` (the bundleable CPU floor, D6). */
export const STUB_IMAGE_PROVIDER: ProviderManifest = {
  manifestVersion: 2,
  id: 'reference-stub-image',
  name: 'Reference Stub Image (CPU floor)',
  version: '0.0.0',
  kind: 'local',
  capability: ['image'],
  inputSchemaRef: null,
  // SEC-6: no credential field anywhere on the manifest — by construction.
  engineLicense: { role: 'engine', spdx: 'MIT', commercialUse: 'yes' },
  weightsLicense: [
    { role: 'weights', spdx: 'CC0-1.0', commercialUse: 'yes' },
  ],
  hardware: { 'min-cpu': { support: 'good', accel: 'cpu' } },
  tier: 'local',
  packaging: 'bundled',
  signing: 'user-space',
  outputRights: { ownership: 'operator', copyrightable: 'unknown' },
  flavor: 'reference-stub',
}

/**
 * The reference runtime. Optionally `degraded` (to demonstrate the tri-state
 * health probe) and `failNextInvoke` (to demonstrate the uniform error envelope).
 */
export class ReferenceImageRuntime implements RuntimeHost {
  private degraded = false
  private failNextInvoke = false
  /** jobIds the membrane has asked to cancel (observable for tests). */
  readonly cancelledJobs: string[] = []

  /** Force the next health probe to report `degraded` (tri-state demonstration). */
  setDegraded(degraded: boolean): void {
    this.degraded = degraded
  }

  /** Force the next Invoke to fault (uniform error-envelope demonstration). */
  setFailNextInvoke(fail: boolean): void {
    this.failNextInvoke = fail
  }

  announce(): RuntimeManifest {
    return {
      runtimeId: REFERENCE_RUNTIME_ID,
      name: 'Capability Reference Image Runtime',
      contractVersion: CAPABILITY_CONTRACT_VERSION,
      capabilities: [
        {
          capabilityId: 'image',
          schemaVersion: IMAGE_SCHEMA_VERSION,
          providers: [STUB_IMAGE_PROVIDER],
        },
      ],
    }
  }

  negotiateOffer(): NegotiateOffer {
    return {
      contractVersion: CAPABILITY_CONTRACT_VERSION,
      capabilitySchemaVersions: { image: IMAGE_SCHEMA_VERSION },
    }
  }

  health(kind: ProbeKind): HealthProbe {
    const state: HealthState = this.degraded ? 'degraded' : 'up'
    return {
      kind,
      state,
      detail: this.degraded ? 'stub renderer throttled' : 'ready',
    }
  }

  invoke(request: InvokeRequest): CapabilityResult {
    const jobId = `job:${REFERENCE_RUNTIME_ID}:${request.correlationId}`

    if (this.failNextInvoke) {
      this.failNextInvoke = false
      return {
        jobId,
        status: 'failed',
        progress: 0,
        artifacts: [],
        usage: { unit: 'image', quantity: 0, tier: 'local' },
        error: {
          faultDomain: 'provider',
          retryable: true,
          code: 'provider.stub_forced_failure',
          message: 'reference runtime: forced failure for envelope demonstration',
        },
      }
    }

    // The reference runtime only hosts `image`; anything else is an input fault.
    if (request.capabilityId !== 'image') {
      return {
        jobId,
        status: 'failed',
        progress: 0,
        artifacts: [],
        usage: { unit: 'call', quantity: 0, tier: 'local' },
        error: {
          faultDomain: 'input',
          retryable: false,
          code: 'input.capability_not_hosted',
          message: `reference runtime hosts only 'image', not '${request.capabilityId}'`,
        },
      }
    }

    const core = request.core as ImageCore
    const artifact: ImageArtifact = {
      kind: 'image',
      uri: `stub://image/${encodeURIComponent(core.prompt)}/${core.seed}`,
      mime: `image/${core.format}`,
      w: core.size.w,
      h: core.size.h,
    }

    return {
      jobId,
      status: 'succeeded',
      progress: 1,
      artifacts: [artifact],
      usage: { unit: 'image', quantity: core.count, tier: 'local' },
      error: null,
    }
  }

  cancel(request: CancelRequest): void {
    this.cancelledJobs.push(request.jobId)
  }
}
