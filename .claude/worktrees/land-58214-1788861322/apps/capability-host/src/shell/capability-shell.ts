/**
 * The composition-driven Capability shell (ADR 0125 D8 — "Capability realizes this model").
 *
 * The shell:
 *   1. boots a COMPOSITION manifest (an edition's capabilities + membership);
 *   2. CONNECTS runtimes over the membrane (Announce → Negotiate);
 *   3. builds the D7 RESOLUTION pipeline, wiring its hardware stage off the
 *      connected runtimes' announced provider-manifests;
 *   4. RESOLVES a capability to a typed `ResolutionResult` (FE-1);
 *   5. INVOKES a resolved capability through the membrane, getting the uniform
 *      `CapabilityResult` envelope with `correlationId` propagation + redaction.
 *
 * v0 stubs entitlement/license to "available" (named seams), but the pipeline
 * ORDER and the typed output are real. This is the minimal host the directive
 * calls for — Harborline App's UI chrome is a later phase.
 */

import type {
  CapabilityId,
  CapabilityResult,
  InvokeRequest,
  ResolutionResult,
} from '@harborline-software/api-contracts'

import type { CompositionManifest } from '../resolution/composition.js'
import {
  defaultConfiguration,
  resolveCapability,
  staticHardwareResolver,
  stubEntitledAll,
  stubLicensePassAll,
  type ProviderCandidate,
  type ResolutionPipeline,
} from '../resolution/pipeline.js'
import {
  connectRuntime,
  invokeOnConnection,
  probeRuntime,
  type RuntimeConnection,
} from '../membrane/runtime-connection.js'
import type { ShellNegotiationProfile } from '../membrane/negotiate.js'
import type { InvokeContext } from '../membrane/invoke.js'
import { type HealthProbe, type ProbeKind } from '../membrane/observe.js'
import type { RuntimeTransport } from '../membrane/runtime-transport.js'
import { CAPABILITY_CONTRACT_VERSION } from '../runtime/reference-image-runtime.js'

/** Options for booting the shell. */
export interface CapabilityShellOptions {
  /** The edition composition to boot (e.g. the v0 reference edition). */
  composition: CompositionManifest
  /** The shell's membrane-contract + per-capability schema-version profile. */
  negotiationProfile: ShellNegotiationProfile
  /** Redaction/log context threaded into every Invoke (SEC-3 + Observe). */
  invokeContext?: InvokeContext
}

/** The booted Capability shell. */
export class CapabilityShell {
  private readonly connections = new Map<string, RuntimeConnection>()

  constructor(private readonly options: CapabilityShellOptions) {}

  /** The edition this shell booted. */
  get composition(): CompositionManifest {
    return this.options.composition
  }

  /** The connected runtimes (post-Announce/Negotiate). */
  get runtimes(): readonly RuntimeConnection[] {
    return [...this.connections.values()]
  }

  /**
   * Connect a runtime over the membrane (Announce → Negotiate → Address-established).
   * Idempotent on `runtimeId` (a re-connect replaces the prior connection).
   */
  async connect(transport: RuntimeTransport): Promise<RuntimeConnection> {
    const connection = await connectRuntime(transport, this.options.negotiationProfile)
    this.connections.set(connection.runtimeId, connection)
    return connection
  }

  /** Observe(health): probe a connected runtime. */
  probe(runtimeId: string, kind: ProbeKind): Promise<HealthProbe> {
    const connection = this.requireConnection(runtimeId)
    return probeRuntime(connection, kind)
  }

  /**
   * Resolve a capability through the D7 pipeline against the booted composition +
   * the providers announced by connected runtimes (the hardware stage's candidate
   * source). Returns the typed `ResolutionResult` (FE-1).
   */
  resolve(capabilityId: CapabilityId): ResolutionResult {
    const pipeline = this.buildPipeline()
    return resolveCapability(pipeline, capabilityId)
  }

  /**
   * Invoke a capability: resolve it (D7), then dispatch through the membrane to
   * the runtime hosting the chosen provider, returning the uniform envelope.
   * A non-resolving `ResolutionResult` yields a uniform `membrane`-domain error
   * envelope (the M3 promise holds — resolution failure does not throw).
   *
   * `ctx` is an optional PER-INVOKE override merged over the shell's construction-
   * time `invokeContext` (the construction-time secrets/logSink stay; the per-call
   * fields — notably the host-stamped `principal`, ADR 0134 Decision 3 — layer on
   * top). This is how the Secure-face PEP carries WHO into a specific Invoke without
   * a per-shell rebuild.
   */
  async invoke(request: InvokeRequest, ctx?: InvokeContext): Promise<CapabilityResult> {
    const resolution = this.resolve(request.capabilityId)
    if (resolution.chosenProviderId === null) {
      return {
        jobId: `job:unresolved:${request.correlationId}`,
        status: 'failed',
        progress: 0,
        artifacts: [],
        usage: { unit: 'call', quantity: 0, tier: resolution.tier },
        error: {
          faultDomain: 'membrane',
          retryable: false,
          code: `membrane.resolution_${resolution.resolutionState.replace(/-/g, '_')}`,
          message: resolution.reason,
        },
      }
    }

    const connection = this.connectionHosting(request.capabilityId)
    if (connection === null) {
      return {
        jobId: `job:no-runtime:${request.correlationId}`,
        status: 'failed',
        progress: 0,
        artifacts: [],
        usage: { unit: 'call', quantity: 0, tier: resolution.tier },
        error: {
          faultDomain: 'membrane',
          retryable: false,
          code: 'membrane.no_connected_runtime',
          message: `no connected runtime hosts '${request.capabilityId}'`,
        },
      }
    }

    const invokeContext: InvokeContext = { ...this.options.invokeContext, ...ctx }
    return invokeOnConnection(connection, request, invokeContext)
  }

  // --- internals -----------------------------------------------------------

  /**
   * Build the D7 pipeline. The hardware stage's candidate source is the set of
   * providers announced by connected runtimes for each capability (real Announce
   * data); entitlement + license are the v0 stubs (named seams).
   */
  private buildPipeline(): ResolutionPipeline {
    const byCapability: Record<CapabilityId, ProviderCandidate[]> = {}
    for (const connection of this.connections.values()) {
      for (const [capabilityId, announced] of connection.registry.byCapability) {
        const candidates = announced.providers.map<ProviderCandidate>((p) => ({
          providerId: p.id,
          tier: p.tier,
          isFloor: p.packaging === 'bundled' || p.packaging === 'managed-install',
          speedHint: p.tier === 'local' ? 'moderate' : 'fast',
        }))
        const existing = byCapability[capabilityId] ?? []
        byCapability[capabilityId] = [...existing, ...candidates]
      }
    }

    return {
      composition: this.options.composition,
      entitlement: stubEntitledAll,
      hardware: staticHardwareResolver(byCapability),
      license: stubLicensePassAll,
      configuration: defaultConfiguration,
    }
  }

  /** The first connected runtime that hosts (and negotiated) a capability. */
  private connectionHosting(capabilityId: CapabilityId): RuntimeConnection | null {
    for (const connection of this.connections.values()) {
      if (
        connection.registry.byCapability.has(capabilityId) &&
        connection.negotiation.acceptedCapabilities.includes(capabilityId)
      ) {
        return connection
      }
    }
    return null
  }

  private requireConnection(runtimeId: string): RuntimeConnection {
    const connection = this.connections.get(runtimeId)
    if (connection === undefined) {
      throw new Error(`no connected runtime '${runtimeId}'`)
    }
    return connection
  }
}

/**
 * The default shell negotiation profile for v0: the Capability contract version + the
 * capability schema-versions the shell understands. A real shell derives the
 * schema-version map from the @harborline-software/api-contracts capability namespace version.
 */
export function defaultShellNegotiationProfile(): ShellNegotiationProfile {
  return {
    contractVersion: CAPABILITY_CONTRACT_VERSION,
    supportedSchemaVersions: {
      tts: '1.0.0',
      image: '1.0.0',
      'bank-import': '1.0.0',
    },
  }
}
