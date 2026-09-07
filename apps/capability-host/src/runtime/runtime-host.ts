/**
 * The RUNTIME side of the membrane seam — the contract a provider-runtime
 * implements so the shell can Announce/Negotiate/Invoke/Observe it.
 *
 * This is the abstraction both v0 transports drive:
 *  - `InProcessTransport`  → calls a `RuntimeHost` directly (same process).
 *  - `LoopbackHttpTransport` → calls a `RuntimeHost` exposed over loopback HTTP.
 *
 * A real runtime (the .NET local-node, a Python worker) implements this same
 * surface over its own wire; v0 ships ONE reference runtime to prove the
 * round-trip in-repo without those external runtimes being up.
 */

import type {
  CancelRequest,
  CapabilityResult,
  HealthProbe,
  InvokeRequest,
  NegotiateOffer,
} from '@harborline-software/api-contracts'

import type { RuntimeManifest } from '../membrane/announce.js'
import type { ProbeKind } from '../membrane/observe.js'

/**
 * The runtime-side surface. The runtime OWNS its native shapes; for the v0
 * reference runtime the native Invoke response IS the uniform envelope, but the
 * membrane still normalizes through M3 (it must not assume the envelope).
 */
export interface RuntimeHost {
  /** Announce: publish the runtime's manifest (hosted capabilities + providers). */
  announce(): RuntimeManifest | Promise<RuntimeManifest>

  /** Negotiate: publish the connect-time offer (contract + capability schema versions). */
  negotiateOffer(): NegotiateOffer | Promise<NegotiateOffer>

  /** Observe(health): answer a tri-state health probe. */
  health(kind: ProbeKind): HealthProbe | Promise<HealthProbe>

  /** Invoke: run the capability, return the (native, here uniform) result. */
  invoke(request: InvokeRequest): CapabilityResult | Promise<CapabilityResult>

  /** Cancel a `jobId`-returning Invoke. */
  cancel(request: CancelRequest): void | Promise<void>
}
