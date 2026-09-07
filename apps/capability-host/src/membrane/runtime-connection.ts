/**
 * A connected runtime, as the shell sees it (the membrane's per-runtime handle).
 *
 * Ties the five v0 faces together for ONE runtime, in lifecycle order
 * (ADR 0124 Part II): Announce → Negotiate → Address(established) → Observe.
 * Invoke rides this connection. The connection is the unit the membrane keys its
 * routing registry on.
 */

import type {
  CancelRequest,
  CapabilityResult,
  HealthProbe,
  InvokeRequest,
} from '@harborline-software/api-contracts'

import {
  indexRuntimeManifest,
  type RuntimeManifest,
  type RuntimeRegistryEntry,
} from './announce.js'
import {
  cancelInvoke,
  invokeCapability,
  type InvokeContext,
} from './invoke.js'
import {
  negotiate,
  type NegotiateResult,
  type ShellNegotiationProfile,
} from './negotiate.js'
import { driveHealthProbe, type ProbeKind } from './observe.js'
import type { RuntimeTransport } from './runtime-transport.js'

/** The connection state after the Announce + Negotiate handshake completes. */
export interface RuntimeConnection {
  /** The runtime's id. */
  readonly runtimeId: string
  /** The shell-side profile used to reconcile later port negotiations. */
  readonly shellProfile: ShellNegotiationProfile
  /** The complete Announce payload retained for language-neutral port adapters. */
  readonly manifest: RuntimeManifest
  /** The shell-side registry entry built from the runtime's Announce manifest. */
  readonly registry: RuntimeRegistryEntry
  /** The Negotiate reconciliation result (compat + accepted capabilities). */
  readonly negotiation: NegotiateResult
  /** The transport reaching this runtime (Address established). */
  readonly transport: RuntimeTransport
}

/**
 * Establish a connection to a runtime: Announce (ingest its manifest) →
 * Negotiate (reconcile contract + capability schema versions). Returns the
 * connection handle the membrane routes Invokes through.
 */
export async function connectRuntime(
  transport: RuntimeTransport,
  shellProfile: ShellNegotiationProfile,
): Promise<RuntimeConnection> {
  // Announce.
  const manifest = await transport.announce()
  const registry = indexRuntimeManifest(manifest)

  // Negotiate.
  const offer = await transport.negotiateOffer()
  const negotiation = negotiate(shellProfile, offer)

  return {
    runtimeId: manifest.runtimeId,
    shellProfile,
    manifest,
    registry,
    negotiation,
    transport,
  }
}

/** Observe(health): drive a tri-state probe against a connected runtime. */
export function probeRuntime(
  connection: RuntimeConnection,
  kind: ProbeKind,
): Promise<HealthProbe> {
  return driveHealthProbe(
    { probe: (k) => connection.transport.health(k) },
    kind,
  )
}

/**
 * Invoke a capability on a connected runtime — GATED on Negotiate having accepted
 * that capability. A request for an unaccepted/un-negotiated capability is
 * REJECTED with a uniform `membrane`-domain error envelope (it never reaches the
 * runtime), preserving the M3 promise.
 */
export async function invokeOnConnection(
  connection: RuntimeConnection,
  request: InvokeRequest,
  ctx: InvokeContext = {},
): Promise<CapabilityResult> {
  if (!connection.negotiation.acceptedCapabilities.includes(request.capabilityId)) {
    return {
      jobId: `job:${connection.runtimeId}:rejected:${request.correlationId}`,
      status: 'failed',
      progress: 0,
      artifacts: [],
      usage: { unit: 'call', quantity: 0, tier: 'local' },
      error: {
        faultDomain: 'membrane',
        retryable: false,
        code: 'membrane.capability_not_negotiated',
        message: `capability '${request.capabilityId}' was not accepted for runtime '${connection.runtimeId}' at Negotiate`,
      },
    }
  }
  return invokeCapability(connection.transport, request, ctx)
}

/** Cancel a `jobId`-returning Invoke on a connected runtime (Part IV.3). */
export function cancelOnConnection(
  connection: RuntimeConnection,
  request: CancelRequest,
  ctx: InvokeContext = {},
): Promise<void> {
  return cancelInvoke(connection.transport, request, ctx)
}
