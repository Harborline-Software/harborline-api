/**
 * The runtime-side transport seam (ADR 0124 Part III — the Inner-Loop mechanism).
 *
 * The membrane reaches a runtime through one of these. v0 implements the two
 * local arms of the Address superset:
 *  - `in-process`       → a direct object call (the reference runtime).
 *  - `local-subprocess` → loopback HTTP (127.0.0.1) to a child process.
 *
 * CRITICAL (ADR 0124 M3 — "adopt the patterns, OWN the envelope"): a transport
 * carries the runtime's NATIVE wire. The membrane NORMALIZES that native shape
 * into the uniform @harborline-software/api-contracts envelope at the M3 boundary (in
 * `invoke.ts`) — the transport itself does not promise the uniform envelope; the
 * MEMBRANE does. This is why a `NativeInvokeResponse` is deliberately loose.
 */

import type {
  CancelRequest,
  CapabilityResult,
  HealthProbe,
  InvokeRequest,
  NegotiateOffer,
} from '@harborline-software/api-contracts'

import type { RuntimeManifest } from './announce.js'
import type { ProbeKind } from './observe.js'

/**
 * A runtime's native Invoke response, BEFORE M3 normalization. A v0 reference
 * runtime already speaks the uniform envelope (so this is the envelope), but the
 * type is loose to model the general case where a native runtime (a Python
 * worker, a CLI) returns something the membrane must map. The membrane treats it
 * as `unknown`-ish and normalizes it.
 */
export type NativeInvokeResponse = CapabilityResult | Record<string, unknown>

/**
 * The runtime side of the membrane seam. Every face the shell drives against a
 * runtime goes through this. v0 transports: `InProcessTransport`,
 * `LoopbackHttpTransport`.
 */
export interface RuntimeTransport {
  /** The runtime this transport reaches. */
  readonly runtimeId: string
  /** The Address mode this transport implements (`in-process`/`local-subprocess`). */
  readonly mode: 'in-process' | 'local-subprocess'

  /** Announce: fetch the runtime's manifest (hosted capabilities + providers). */
  announce(): Promise<RuntimeManifest>

  /** Negotiate: fetch the runtime's connect-time offer. */
  negotiateOffer(): Promise<NegotiateOffer>

  /** Observe(health): drive a tri-state health probe against the runtime. */
  health(kind: ProbeKind): Promise<HealthProbe>

  /**
   * Invoke: send the request to the runtime and return its NATIVE response (the
   * membrane normalizes it to the uniform envelope at the M3 boundary). The
   * `idempotencyKey` + `correlationId` ride the request unchanged.
   */
  invoke(request: InvokeRequest): Promise<NativeInvokeResponse>

  /** Cancellation verb (ADR 0124 Part IV.3) — cancel a `jobId`-returning Invoke. */
  cancel(request: CancelRequest): Promise<void>
}
