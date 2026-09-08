/**
 * The Invoke face + M3 normalization boundary (ADR 0124 Part III/IV + M3).
 *
 * The data plane. The membrane:
 *   1. enforces SEC-2 (idempotencyKey fail-closed on financial Invoke);
 *   2. sends the request to the runtime over its transport (the tier mechanism);
 *   3. NORMALIZES the runtime's NATIVE response into the uniform
 *      @harborline-software/api-contracts `CapabilityResult` envelope (M3 — "adopt the patterns,
 *      OWN the envelope");
 *   4. threads `idempotencyKey` + `correlationId` end-to-end;
 *   5. maps any fault into the typed error taxonomy + retryability;
 *   6. redacts secrets out of any native error text at the boundary (SEC-3);
 *   7. wires the cancellation verb (ADR 0124 Part IV.3).
 *
 * Every result the shell sees is the UNIFORM envelope, regardless of tier/
 * transport/capability (the M3 promise).
 */

import type {
  CancelRequest,
  CapabilityError,
  CapabilityResult,
  FaultDomain,
  InvokeRequest,
} from '@harborline-software/api-contracts'

import { isFinancialCapability } from './financial.js'
import type { LogSink } from './observe.js'
import type { PrincipalAttribution } from './pep.js'
import { redact } from './redaction.js'
import type {
  NativeInvokeResponse,
  RuntimeTransport,
} from './runtime-transport.js'

/** Raised when SEC-2's fail-closed idempotency-key gate rejects a financial Invoke. */
export class IdempotencyKeyRequiredError extends Error {
  readonly code = 'membrane.idempotency_key_required'
  constructor(capabilityId: string) {
    super(
      `SEC-2: financial capability '${capabilityId}' requires a non-blank idempotencyKey (fail-closed)`,
    )
    this.name = 'IdempotencyKeyRequiredError'
  }
}

/** Options threaded into an Invoke (the redaction secret-set + an optional log sink). */
export interface InvokeContext {
  /** Secret literals scrubbed from any native error text at M3 (SEC-3). */
  secrets?: readonly string[]
  /** Where the membrane emits redacted Invoke logs (Observe Part V). */
  logSink?: LogSink
  /**
   * WHO is invoking — host-stamped, carried end-to-end (ADR 0134 Decision 3, the
   * principal a first-class Invoke input). The Secure-face PEP (`secureInvoke`)
   * CHECKS it (authenticate + authorize) BEFORE the request reaches here; the
   * membrane carries it through so the Invoke log + the future durable attribution
   * feed record WHO, not just WHEN. Attribution-grade at P0 (not crypto-verified
   * until P1) — no authz keys on it here. Optional only at this transport-level
   * primitive; the PEP chokepoint REQUIRES it.
   *
   * INERT attribution, never the credential. The shell is caller-supplied, so anything reaching
   * it reaches untrusted code — and the PEP authenticates by object identity, which would make
   * the credential a bearer token good for any command. This path only ever reads `.id`.
   */
  principal?: PrincipalAttribution
}

/**
 * SEC-2 gate: a financial Invoke MUST carry a non-blank idempotencyKey. Throws
 * `IdempotencyKeyRequiredError` (fail-closed) otherwise. Non-financial Invokes
 * pass through (a generated key is acceptable for them). Called BEFORE the
 * request leaves the membrane — a rejected financial Invoke never reaches the
 * runtime, so it cannot double-post.
 */
export function assertIdempotencyKey(request: InvokeRequest): void {
  if (!isFinancialCapability(request.capabilityId)) return
  if (request.idempotencyKey.trim().length === 0) {
    throw new IdempotencyKeyRequiredError(request.capabilityId)
  }
}

/**
 * The M3 normalization: map a runtime's NATIVE response into the uniform
 * `CapabilityResult` envelope. A v0 reference runtime already returns the
 * envelope shape; a real native runtime returns its own shape and the membrane
 * maps it. Missing fields default conservatively (no jobId → minted-less empty;
 * the membrane is expected to have minted a jobId — see {@link invokeCapability}).
 *
 * Any native error text is REDACTED here (SEC-3 — the M3 boundary).
 *
 * M3 FAIL-CLOSED (Admiral ruling 2026-06-16, Phase-3): a MISSING or INVALID
 * native `status` defaults to `failed` (NOT `succeeded`). A membrane normalizing
 * an arbitrary native response — a truncated MCP/CLI payload, a malformed worker
 * reply — must NOT read the absence of a status as success; that would let a
 * half-written or corrupt response pass as a completed job. When the status is
 * defaulted to `failed`, `progress` follows (→ 0) and the membrane SYNTHESIZES a
 * `membrane`-domain error explaining the fail-closed normalization (so a `failed`
 * envelope is never contradictorily `error: null`).
 */
export function normalizeToEnvelope(
  native: NativeInvokeResponse,
  fallbackJobId: string,
  secrets?: readonly string[],
): CapabilityResult {
  const n = native as Record<string, unknown>

  const jobId = typeof n.jobId === 'string' && n.jobId.length > 0 ? n.jobId : fallbackJobId
  // M3 fail-closed: a missing/invalid status normalizes to `failed`, not `succeeded`.
  const statusValid = isJobStatus(n.status)
  const status: CapabilityResult['status'] = statusValid ? (n.status as CapabilityResult['status']) : 'failed'
  const progress =
    typeof n.progress === 'number' ? clamp01(n.progress) : status === 'succeeded' ? 1 : 0
  const artifacts = Array.isArray(n.artifacts) ? (n.artifacts as CapabilityResult['artifacts']) : []
  const usage = isUsage(n.usage)
    ? (n.usage as CapabilityResult['usage'])
    : { unit: 'call', quantity: 1, tier: 'local' as const }

  // If the native error is present, normalize it. Otherwise, when we fail-closed
  // on a missing/invalid status, synthesize a membrane-domain error so the failed
  // envelope is self-explaining (M3: a malformed native response is a membrane fault).
  let error = normalizeError(n.error, secrets)
  if (error === null && !statusValid) {
    error = {
      faultDomain: 'membrane',
      retryable: false,
      code: 'membrane.invalid_native_status',
      message: redact(
        `native response had a missing/invalid status (${describeStatus(n.status)}); fail-closed to 'failed'`,
        { secrets },
      ),
    }
  }

  return { jobId, status, progress, artifacts, usage, error }
}

/** Describe a native status value for the fail-closed diagnostic (no secrets — it's the raw status token). */
function describeStatus(raw: unknown): string {
  if (raw === undefined) return 'undefined'
  if (raw === null) return 'null'
  if (typeof raw === 'string') return `'${raw}'`
  return typeof raw
}

/** Normalize a native error half into the typed `CapabilityError`, redacting text. */
function normalizeError(
  raw: unknown,
  secrets?: readonly string[],
): CapabilityError | null {
  if (raw === null || raw === undefined) return null
  const e = raw as Record<string, unknown>
  const faultDomain: FaultDomain = isFaultDomain(e.faultDomain) ? e.faultDomain : 'provider'
  const retryable = typeof e.retryable === 'boolean' ? e.retryable : faultDomain === 'provider'
  const code = typeof e.code === 'string' ? e.code : 'provider.unknown'
  const message = redact(typeof e.message === 'string' ? e.message : 'unknown error', { secrets })
  const error: CapabilityError = { faultDomain, retryable, code, message }
  if (typeof e.retryAfter === 'number') error.retryAfter = e.retryAfter
  return error
}

/**
 * Invoke a capability through a runtime transport (the full M3 path). Mints a
 * membrane jobId (used if the native response omits one), enforces SEC-2, sends
 * the request, normalizes the response into the uniform envelope, and emits a
 * redacted Invoke log carrying the correlationId.
 *
 * On a transport/normalization fault the membrane SYNTHESIZES a uniform
 * `membrane`-domain error envelope (never throws a raw native error to the shell)
 * — the M3 promise holds even on failure.
 */
export async function invokeCapability(
  transport: RuntimeTransport,
  request: InvokeRequest,
  ctx: InvokeContext = {},
): Promise<CapabilityResult> {
  assertIdempotencyKey(request) // SEC-2 fail-closed — may throw before the wire

  const mintedJobId = mintJobId(transport.runtimeId, request.correlationId)

  ctx.logSink?.write({
    level: 'info',
    correlationId: request.correlationId,
    runtimeId: transport.runtimeId,
    capabilityId: request.capabilityId,
    // Carry WHO end-to-end (ADR 0134 Decision 3) — the Invoke log records the
    // host-stamped principal the Secure-face PEP authenticated, not just WHEN.
    message: `invoke ${request.capabilityId} transport=${request.transport} principal=${ctx.principal?.id ?? '<none>'}`,
  })

  let native: NativeInvokeResponse
  try {
    native = await transport.invoke(request)
  } catch (err) {
    const message = redact(err instanceof Error ? err.message : String(err), {
      secrets: ctx.secrets,
    })
    ctx.logSink?.write({
      level: 'error',
      correlationId: request.correlationId,
      runtimeId: transport.runtimeId,
      capabilityId: request.capabilityId,
      message: `invoke transport fault: ${message}`,
    })
    // M3: even a transport failure surfaces the uniform envelope.
    return {
      jobId: mintedJobId,
      status: 'failed',
      progress: 0,
      artifacts: [],
      usage: { unit: 'call', quantity: 0, tier: 'local' },
      error: {
        faultDomain: 'membrane',
        retryable: false,
        code: 'membrane.transport_fault',
        message,
      },
    }
  }

  return normalizeToEnvelope(native, mintedJobId, ctx.secrets)
}

/**
 * Cancel a `jobId`-returning Invoke (ADR 0124 Part IV.3). The correlationId rides
 * the cancellation like the originating Invoke. Failures are swallowed into a
 * redacted log (cancellation is best-effort; the membrane does not throw).
 */
export async function cancelInvoke(
  transport: RuntimeTransport,
  request: CancelRequest,
  ctx: InvokeContext = {},
): Promise<void> {
  try {
    await transport.cancel(request)
    ctx.logSink?.write({
      level: 'info',
      correlationId: request.correlationId,
      runtimeId: transport.runtimeId,
      message: `cancel job=${request.jobId}`,
    })
  } catch (err) {
    const message = redact(err instanceof Error ? err.message : String(err), {
      secrets: ctx.secrets,
    })
    ctx.logSink?.write({
      level: 'warn',
      correlationId: request.correlationId,
      runtimeId: transport.runtimeId,
      message: `cancel best-effort fault: ${message}`,
    })
  }
}

// --- helpers ---------------------------------------------------------------

/** Mint a membrane job id (used when the native runtime omits one). */
function mintJobId(runtimeId: string, correlationId: string): string {
  return `job:${runtimeId}:${correlationId}:${Date.now().toString(36)}`
}

function clamp01(n: number): number {
  if (Number.isNaN(n)) return 0
  return Math.min(1, Math.max(0, n))
}

function isJobStatus(v: unknown): v is CapabilityResult['status'] {
  return (
    v === 'accepted' ||
    v === 'running' ||
    v === 'succeeded' ||
    v === 'partial' ||
    v === 'failed'
  )
}

function isFaultDomain(v: unknown): v is FaultDomain {
  return v === 'input' || v === 'provider' || v === 'membrane'
}

function isUsage(v: unknown): v is CapabilityResult['usage'] {
  if (v === null || typeof v !== 'object') return false
  const u = v as Record<string, unknown>
  return typeof u.unit === 'string' && typeof u.quantity === 'number'
}
