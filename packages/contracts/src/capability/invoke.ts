/**
 * The Invoke request envelope (ADR 0124 Part III/IV + paper-gate R1/SEC-2).
 *
 * The membrane wire for invoking a capability. UNIFORM across all three
 * execution tiers (Registry/Inner/Outer) and all four transports — the
 * membrane normalizes every provider's native call into / out of this shape at
 * the M3 boundary ("adopt the patterns, OWN the envelope", ADR 0124 M3).
 *
 * Composition of the request envelope (paper-gate R1, the gate's primary
 * structural finding):
 *   { capabilityId, core, providerInputs, attachments[], idempotencyKey,
 *     correlationId, transport }
 *
 * `attachments[]` is a request-envelope FACE — binary inputs (ComfyUI init
 * image, ControlNet refs, uploaded CSV/CAMT file) live here, parallel to the
 * RESULT envelope's `artifacts[]`, NEVER in the CORE. This is shared across all
 * capabilities; the *semantic* binding (which attachment is the init image)
 * lives in the provider-schema's attachment-ref slots.
 */

import type {
  CapabilityId,
  CorrelationId,
} from './common.js'
import type { CapabilityCore } from './cores.js'
import type { ProviderInputs } from './schema.js'

/**
 * The transport discriminator (ADR 0123 §S3 / ADR 0124 Part II). The consumer
 * dispatches on this, NOT on provider identity:
 *  - `sync`    — result returned inline on the call (sub-deadline)
 *  - `poll`    — `jobId` returned; consumer polls `/status` (mid-flight progress)
 *  - `stream`  — `jobId` returned; consumer opens a stream of progress envelopes
 *  - `webhook` — `jobId` returned; result delivered async out-of-band
 */
export type Transport = 'sync' | 'poll' | 'stream' | 'webhook'

/**
 * A binary input attachment reference on the Invoke REQUEST envelope (R1).
 * Parallel to the RESULT envelope's `artifacts[]`. The bytes are carried
 * out-of-band (multipart part or a pre-staged `IBlobStore` ref); the provider
 * schema references this attachment by `attachmentId`.
 *
 * Shared across ALL capabilities — the same mechanism carries a ComfyUI init
 * image (Case 2) and an uploaded bank-statement file (Cases 5/6). Pinning this
 * as a request-envelope face is what keeps `initImage`/uploaded-CSV from being
 * wrongly promoted into a CORE.
 */
export interface RequestAttachment {
  /** Stable id the provider-schema references this attachment by. */
  attachmentId: string
  /** MIME type of the binary payload (e.g. `image/png`, `text/csv`, `application/xml`). */
  mime: string
  /** Original file name, if meaningful (drives bank-import adapter format-detection). */
  fileName?: string | null
  /**
   * Content digest of the bytes (sha256, hex). Lets the membrane dedupe / pin /
   * trace the attachment without inlining it.
   */
  sha256?: string | null
  /**
   * Byte length, if known up-front — lets the membrane enforce a size cap
   * before streaming the body.
   */
  byteLength?: number | null
}

/**
 * The canonical Invoke request envelope. UNIFORM across tiers + transports.
 *
 * SEC-2 (load-bearing): `idempotencyKey` is REQUIRED on financial-bearing
 * Invoke (`bank-import` and any future financial capability) and is enforced
 * **fail-closed at the route** — a missing key on a financial Invoke is
 * REJECTED, never silently allowed (a retried financial post without it
 * double-posts, regressing the ADR 0112/0115 `SourceReference` discipline).
 * The membrane preserves the key across M3 normalization and the MCP hop. It is
 * optional only for non-financial capabilities (e.g. `image`). See the type-level
 * note: the field is required here so the route cannot forget it; consumers of
 * non-financial capabilities may pass a generated key.
 */
export interface InvokeRequest {
  /** The stable capability being invoked — the membrane routes on this. */
  capabilityId: CapabilityId
  /** The thin, provider-universal CORE for `capabilityId` (see `cores.ts`). */
  core: CapabilityCore
  /**
   * Provider-specific inputs conforming to the resolved provider input schema
   * (`ProviderInputSchemaRef`). Validated at the membrane boundary. Binary
   * inputs are NOT here — they ride `attachments[]`.
   */
  providerInputs: ProviderInputs
  /**
   * Binary inputs (R1) — request-envelope face, parallel to RESULT `artifacts[]`,
   * never in the CORE. Empty array when the capability takes no binary input.
   */
  attachments: RequestAttachment[]
  /**
   * Caller-supplied idempotency key (SEC-2). REQUIRED + fail-closed on
   * financial-bearing Invoke; the membrane preserves it across normalization +
   * the MCP hop. Modeled as required so the route physically cannot omit it on
   * a financial capability.
   */
  idempotencyKey: string
  /** Correlation id propagated across the shell→runtime→provider→remote hops. */
  correlationId: CorrelationId
  /**
   * The transport the consumer dispatches on. The adapter declares which
   * transport(s) a provider supports; the resolved value rides the request.
   */
  transport: Transport
}

/**
 * The cancellation verb (ADR 0124 Part IV.3). Every `jobId`-returning transport
 * (`poll`/`stream`/`webhook`) is cancellable. TYPES ONLY — the membrane owns the
 * behaviour.
 */
export interface CancelRequest {
  /** The membrane-minted job to cancel. */
  jobId: string
  /** Correlation id for the cancellation, propagated like the originating Invoke. */
  correlationId: CorrelationId
}
