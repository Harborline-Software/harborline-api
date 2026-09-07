/**
 * The Secure face — the membrane Policy-Enforcement-Point (ADR 0134 P0 / ADR 0124
 * Part II "the Secure face", deferred in v0).
 *
 * This is the single CHOKEPOINT every Invoke face MUST funnel through. Before P0
 * the live invoke path bypassed authority three ways (`ProtocolClient.invoke` →
 * `invokeInProcess` → `shell.invoke` directly; the renderer's `capability_invoke` carried
 * no principal; the membrane had no place to check one). The structural bypass this
 * closes: the principal was STAMPED at the CP broker but NEVER CHECKED on an invoke,
 * because the membrane had no enforcement point to check it at (ADR 0134 Context).
 *
 * The PEP enforces, IN ORDER (ADR 0134 Decision 4):
 *   1. **authenticate** — the principal is host-stamped + non-anonymous (never
 *      accepted from the caller; see the per-face wiring in `runtime-host` / the
 *      `capability-invoker.mjs` bridge / `capability.rs`). A blank/anonymous principal is
 *      REJECTED fail-closed. **P1b-2:** when a `verifyPrincipal` verifier is injected
 *      (a face that mints node-SIGNED principals), authenticate ALSO requires the
 *      principal to carry a node-signed envelope that VERIFIES — a tampered, unsigned,
 *      wrong-key, stale, or replayed principal is rejected (`membrane.principal_unverified`).
 *      The signature gives the principal cryptographic teeth that survive a trust
 *      boundary, not just host-stamping (ADR 0134 Decision 5).
 *   2. **authorize** — classify the command via the immutable policy store's
 *      evaluator (CP/AP per ADR 0128), then ENFORCE
 *      (ADR 0134 P1a/P1b-1 — the rejection path): an **AP** command ALWAYS-ALLOWS; a
 *      **CP** command ALWAYS-ALLOWS only if it carries a VALID confirmation whose
 *      broker SINGLE-USE TOKEN is verified + CONSUMED here, otherwise it is
 *      REJECTED before SEC-2/execute
 *      (`AuthorityRejectedError`, `membrane.authority_rejected`). The TOKEN is the
 *      load-bearing check: it is minted by the broker's `propose`, bound to THIS
 *      command, single-use — so a fabricated confirmation (no real token) is refused
 *      and a replay (same token twice) fails on the second invoke. (Before P1b-1 the
 *      gate validated only the confirmation's SHAPE — command-match + a non-anonymous
 *      `confirmedBy` — which an in-process caller could forge + replay without the
 *      broker; the token-consume closes that.) The token is still attribution-grade
 *      WITHIN this process; P1b-2 binds it to an Ed25519 signed envelope so it cannot
 *      be forged/replayed ACROSS a process boundary (NOT built here). The authority is
 *      structurally IN THE PATH.
 *   3. **SEC-2 (idempotency)** + **SEC-3 (redaction)** + **execute** — delegated to
 *      `shell.invoke` → `invokeCapability`, which already owns the SEC-2 fail-closed
 *      gate, the M3 normalization, and the SEC-3 redaction at the boundary.
 *
 * PEP = membrane (this); its evaluator is built once from Capability's direct-read policy store.
 * No caller supplies policy behavior to the security layer.
 *
 * Decision 3: the `kind`/`MembranePrincipal` is ATTRIBUTION-grade, NOT
 * enforcement-grade — no authz keys on it before P1's signature binding. The PEP
 * records WHO; P1 makes WHO cryptographically verifiable.
 */

import type { CapabilityId, CapabilityResult, InvokeRequest } from '@harborline-software/api-contracts'

import type {
  SignedPrincipalEnvelope,
  SignedPrincipalRejectionReason,
} from './signed-principal.js'
import {
  consumeTrustedConfirmation,
  isRegisteredConfirmationEvidence,
  isRegisteredMembranePrincipal,
  principalAttribution,
} from './credential-boundary.js'
import { createPolicyEvaluator, loadPolicyStore } from './authority-registry.js'

declare const membranePrincipalBrand: unique symbol

const policyEvaluator = createPolicyEvaluator(loadPolicyStore())

/**
 * WHO is invoking — the actor an Invoke is attributed to AT THE MEMBRANE. A
 * structural mirror of `@harborline-software/api-contracts/principal` `Principal`, kept minimal +
 * decoupled here so the membrane (a layer BELOW harborline-sdk) does not depend on the
 * SDK. The face host-stamps it (the OS user today); it is NEVER accepted from the
 * untrusted caller (ADR 0134 Decision 3).
 *
 * Attribution-grade only at P0 — not cryptographically verified until P1 binds it
 * to an Ed25519 signed-invoke-envelope (ADR 0134 Decision 5).
 */
export interface MembranePrincipal {
  readonly [membranePrincipalBrand]: true
  /** Stable identity, namespaced by source (`os:<username>` for the OS user). */
  readonly id: string
  /** Human-readable name (the OS username today). */
  readonly displayName?: string
  /** The SOURCE of this identity (`local-os-user` today; flippable later). */
  readonly kind?: string
}

/**
 * INERT attribution for a principal — identity WITHOUT authority.
 *
 * Structurally a `MembranePrincipal` minus the brand, and never registered in the private
 * credential registry, so the PEP refuses it exactly like any other shape-only object.
 *
 * This is the type of every principal that crosses back OUT of the membrane. The PEP authenticates
 * by object identity, which makes a registered principal a bearer token good for any command — so
 * a consumer that merely needs to record or display WHO acted must never receive the credential.
 * Build one with `principalAttribution()`.
 */
export interface PrincipalAttribution {
  /** Stable identity, namespaced by source (`os:<username>` for the OS user). */
  readonly id: string
  /** Human-readable name (the OS username today). */
  readonly displayName?: string
  /** The SOURCE of this identity (`local-os-user` today; flippable later). */
  readonly kind?: string
}

/** The authority class of a command (ADR 0128 — mirrored; the SDK owns the registry). */
export type MembraneAuthorityClass = 'AP' | 'CP'

/** The authority decision the PDP returns for a command (classify + describe). */
export interface AuthorityDecision {
  /** AP = autonomous; CP = confirmation-required. */
  readonly authority: MembraneAuthorityClass
  /** One-line human-readable description (carried into the recorded decision). */
  readonly summary: string
}

/** The outcome of verifying a node-signed principal envelope (ADR 0134 P1b-2). */
export type PrincipalVerification =
  | { readonly ok: true; readonly principal: MembranePrincipal }
  | { readonly ok: false; readonly reason: SignedPrincipalRejectionReason }

/**
 * VERIFY a node-SIGNED principal envelope (ADR 0134 P1b-2) — pluggable + injected
 * alongside the trusted broker. Returns the TRUSTED
 * principal when the envelope's Ed25519 signature verifies against a trusted node key,
 * its `issuedAt` is within the ±replay window, and its nonce is fresh; a typed
 * rejection reason otherwise. The membrane (a layer BELOW harborline-sdk + a layer that
 * must not pull `node:crypto` into its type-only contract surface unconditionally)
 * receives this as an injected function so the FACE owns the crypto + the trusted-key
 * set + the seen-nonce state. Backed by `verifySignedPrincipal` in `signed-principal`.
 *
 * When NO verifier is injected, authenticate falls back to the P0/P1b-1 behaviour
 * (non-anonymous host-stamped principal only — attribution-grade, single-host). The
 * verifier is what upgrades a face from host-stamped attribution to a
 * cross-trust-boundary cryptographic guarantee.
 */
export type PrincipalVerifier = (
  envelope: SignedPrincipalEnvelope,
) => PrincipalVerification

/**
 * The CONFIRMATION evidence the PEP authorize step requires for a CP command (ADR
 * 0134 P1a + P1b-1). A CP command executes ONLY if it carries a valid confirmation —
 * the propose-then-confirm artifact the `ProposalBroker` minted. The LOAD-BEARING
 * field is `token`: the broker's single-use, command-bound token, which the PEP
 * verifies + CONSUMES via the trusted broker. The `command` + `confirmedBy`
 * fields are defense-in-depth (shape checks) — they are forgeable in-process on their
 * own (a bare `localOsUserPrincipal(name)` reproduces `confirmedBy`); the token is
 * what cannot be fabricated or replayed because only the broker mints it and it is
 * consumed exactly once.
 *
 * Token-bound + attribution-grade WITHIN this process at P1b-1. P1b-2 binds it to an
 * Ed25519 signed envelope so a confirmation cannot be forged/replayed ACROSS a process
 * boundary (ADR 0134 Decision 5 — out of scope HERE; the principal still rides as the
 * `os:<user>` attribution value, NOT a signed claim).
 */
export interface ConfirmationEvidence {
  /**
   * The broker's SINGLE-USE confirmation TOKEN (minted by `ProposalBroker.propose`).
   * The load-bearing check: the PEP verifies + consumes it via the injected
   * trusted broker. A confirmation with no token / a consumed token / a token bound
   * to a different command does NOT authorize the invoke.
   */
  readonly token: string
  /**
   * The COMMAND this confirmation authorizes. The PEP requires it to MATCH the command
   * being invoked — a confirmation for `demo-cp-op` does not authorize a different CP
   * command (the broker binds a token to one command + payload). Defense-in-depth; the
   * broker ALSO binds the token to the command server-side.
   */
  readonly command: string
  /** WHO approved the op (the broker's `confirmedBy`; non-null on a confirmed proposal). */
  readonly confirmedBy: MembranePrincipal
}

/** The PEP's authorize outcome — `always-allow` (AP / confirmed CP) or `reject` (unconfirmed CP). */
export type InvokeOutcome = 'always-allow' | 'reject'

/** What the PEP recorded for one Invoke (the durable-layer attribution feed). */
export interface InvokeDecision {
  /** The COMMAND the PDP classified (`invoke` for the invoke faces). */
  readonly command: string
  /** The capability the Invoke TARGETED (e.g. `tts` / `image`). */
  readonly capabilityId: CapabilityId
  /**
   * WHO invoked (host-stamped, non-anonymous) — as INERT attribution, never the credential.
   * A sink is caller-supplied, so handing it the registered principal would hand a bearer token
   * to untrusted code.
   */
  readonly principal: PrincipalAttribution
  /** The PDP's CP/AP classification + summary. */
  readonly decision: AuthorityDecision
  /**
   * The authorize outcome (ADR 0134 P1a). `'always-allow'` for an AP command or a CP
   * command carrying a valid confirmation; `'reject'` for a CP command WITHOUT one
   * (the rejection path — refused before SEC-2/execute).
   */
  readonly outcome: InvokeOutcome
  /** Correlation id of the Invoke this decision belongs to. */
  readonly correlationId: string
  /** ISO timestamp the decision was recorded. */
  readonly recordedAt: string
}

/**
 * A sink the PEP records its authorize decision to. Generalizing the broker's
 * `proposedBy`/`confirmedBy` attribution from the `demo-cp-op` path to EVERY Invoke
 * multiplies volume — so the durable home is the durable mutation layer, NOT inline
 * emission (the financial-cluster audit-envelope lesson, ADR 0134 "Audit durability").
 * At P0 the sink is in-memory / optional; a later phase wires the durable layer.
 */
export interface InvokeDecisionSink {
  record(decision: InvokeDecision): void
}

/** An in-memory decision sink (tests + the P0 local-first default; no off-host export). */
export class InMemoryDecisionSink implements InvokeDecisionSink {
  readonly decisions: InvokeDecision[] = []
  record(decision: InvokeDecision): void {
    this.decisions.push(decision)
  }
}

/** Raised when authenticate rejects an anonymous/blank principal (fail-closed). */
export class AnonymousPrincipalError extends Error {
  readonly code = 'membrane.anonymous_principal'
  constructor(capabilityId: string) {
    super(
      `Secure face: invoke of '${capabilityId}' was not attributed to a principal (the principal is host-stamped + non-anonymous — fail-closed)`,
    )
    this.name = 'AnonymousPrincipalError'
  }
}

/**
 * Raised when authenticate REJECTS a principal whose node-signed envelope does NOT
 * verify (ADR 0134 P1b-2). When a face injects a `verifyPrincipal` verifier, every
 * invoke MUST carry a `signedPrincipal` envelope that Ed25519-verifies against a
 * trusted node key, is within the ±replay window, and has a fresh nonce. A tampered,
 * unsigned, wrong-key, stale, or replayed principal is refused BEFORE authorize/execute
 * — the cryptographic teeth that survive a trust boundary. The `reason` (from
 * `SignedPrincipalRejectionReason`) is carried into the message for diagnostics; the
 * surfaced code is the uniform `membrane.principal_unverified`.
 */
export class UnverifiedPrincipalError extends Error {
  readonly code = 'membrane.principal_unverified'
  constructor(capabilityId: string, reason: SignedPrincipalRejectionReason) {
    super(
      `Secure face: invoke of '${capabilityId}' carried a principal whose node signature did not verify (${reason}) — refused before authorize (the principal must be node-signed + replay-fresh; fail-closed)`,
    )
    this.name = 'UnverifiedPrincipalError'
  }
}

/**
 * Raised when authorize REJECTS a CP command that carries no valid confirmation (ADR
 * 0134 P1a — the rejection path). A CP-classified command executes ONLY through
 * propose-then-confirm; an attempt to run one WITHOUT a matching broker confirmation is
 * refused BEFORE SEC-2/execute. The error is surfaced as a uniform `membrane`-domain
 * failed envelope (never thrown to the UI), `code = 'membrane.authority_rejected'`.
 */
export class AuthorityRejectedError extends Error {
  readonly code = 'membrane.authority_rejected'
  constructor(command: string) {
    super(
      `Secure face: '${command}' is a CP (confirmation-required) command and carries no valid confirmation — refused before execution (propose-then-confirm: a human must approve a CP op)`,
    )
    this.name = 'AuthorityRejectedError'
  }
}

/** Is a principal non-anonymous (has a non-blank, namespaced id)? */
function isAttributed(principal: MembranePrincipal | null | undefined): principal is MembranePrincipal {
  return principal != null && typeof principal.id === 'string' && principal.id.trim().length > 0
}

/** A capability core paired with a capability id — the minimal Invoke target. */
export interface InvokeTarget {
  /**
   * The COMMAND being authorized (ADR 0128 — the op the PDP classifies). For the
   * invoke faces this is `'invoke'` (an AP command in the Harborline App registry); the
   * registry classifies the OPERATION, not the capability it targets. Defaults to
   * `'invoke'` when omitted (the only command the invoke chokepoint runs).
   */
  readonly command?: string
  /** The capability the Invoke TARGETS (recorded for audit; e.g. `tts` / `image`). */
  readonly capabilityId: CapabilityId
  /** The full uniform Invoke request (built by the face; the PEP does not mutate it). */
  readonly request: InvokeRequest
  /**
   * The CONFIRMATION evidence for a CP command (ADR 0134 P1a + P1b-1). REQUIRED for a
   * command the PDP classifies CP — without it (or with one whose `token` is unknown /
   * consumed / bound to a different command), the PEP REJECTS the invoke before execute.
   * Ignored for an AP command (AP executes directly; it is never proposed). The face
   * threads the broker's proposal token here (with the `command` + `confirmedBy`); the
   * PEP verifies + CONSUMES the token via the trusted broker.
   */
  readonly confirmation?: ConfirmationEvidence
  /**
   * The node-SIGNED principal envelope (ADR 0134 P1b-2). REQUIRED when the face injects
   * a `verifyPrincipal` verifier (`SecureInvokeOptions.verifyPrincipal`): authenticate
   * then verifies this envelope's Ed25519 signature + replay-window + nonce-freshness
   * and, on success, AUTHENTICATES the principal IT carries (the signed principal is the
   * trusted one). When no verifier is injected this is ignored (P0/P1b-1 host-stamped
   * attribution only). The face mints it in the TRUSTED HOST (see `host-principal-signer`)
   * — the renderer never signs it.
   */
  readonly signedPrincipal?: SignedPrincipalEnvelope
}

/** The execution callback the PEP delegates to AFTER authenticate + authorize pass. */
export type InvokeExecutor = (request: InvokeRequest) => Promise<CapabilityResult>

/** Options for the PEP chokepoint. */
export interface SecureInvokeOptions {
  /** Where the PEP records its authorize decision (durable-layer feed). */
  decisionSink?: InvokeDecisionSink
  /**
   * VERIFY a node-signed principal envelope (ADR 0134 P1b-2) — injected by a face that
   * mints SIGNED principals (the trusted host). When supplied, authenticate REQUIRES a
   * `target.signedPrincipal` that verifies (Ed25519 + replay-window + fresh nonce) and
   * authenticates the principal IT carries; a tampered/unsigned/stale/replayed envelope
   * is rejected fail-closed (`membrane.principal_unverified`). When OMITTED, authenticate
   * keeps the P0/P1b-1 behaviour (a non-anonymous host-stamped principal is accepted as
   * attribution-grade) — the signed path is additive, so existing AP/CP wiring is intact.
   */
  verifyPrincipal?: PrincipalVerifier
}

/**
 * THE CHOKEPOINT. Every Invoke face MUST call this; the raw shell/transport invoke
 * is reachable ONLY from here (enforced structurally by `no-direct-invoke.arch.test.ts`).
 *
 * Enforcement order (ADR 0134 Decision 4): authenticate → authorize → (SEC-2 →
 * SEC-3 → execute, owned by `execute`/`invokeCapability`).
 *
 * authenticate REJECTS an anonymous principal (fail-closed). authorize CLASSIFIES the
 * command (CP/AP per the immutable policy evaluator), then ENFORCES (ADR 0134 P1a + P1b-1 — the
 * rejection path): an **AP** command ALWAYS-ALLOWS; a **CP** command ALWAYS-ALLOWS only
 * if it carries a VALID confirmation whose broker SINGLE-USE TOKEN is verified +
 * CONSUMED via the trusted broker (and whose `command` matches), otherwise it
 * is REJECTED before SEC-2/execute (`AuthorityRejectedError`). The token-consume makes
 * the gate non-forgeable + non-replayable IN-PROCESS: a fabricated confirmation has no
 * real token, and a replay of a real token fails because it was consumed. Every outcome
 * (`always-allow` / `reject`) is RECORDED to the sink.
 *
 * NEVER throws on an execution fault — that stays the uniform failed envelope (the M3
 * promise). The two refusals — an anonymous-principal authenticate failure and a CP
 * authorize rejection — are surfaced as uniform `membrane`-domain failed envelopes,
 * never thrown to the UI as a crash.
 */
export async function secureInvoke(
  target: InvokeTarget,
  principal: MembranePrincipal,
  execute: InvokeExecutor,
  opts: SecureInvokeOptions = {},
  broker?: unknown,
): Promise<CapabilityResult> {
  // 1. authenticate — non-anonymous, host-stamped principal (fail-closed).
  if (!isRegisteredMembranePrincipal(principal) || !isAttributed(principal)) {
    return secureFailure(
      target.request.correlationId,
      'membrane.anonymous_principal',
      new AnonymousPrincipalError(target.capabilityId).message,
    )
  }

  // 1b. authenticate (P1b-2) — when a face injects a principal verifier, the principal
  //     must ALSO carry a node-SIGNED envelope that verifies (Ed25519 + ±replay window +
  //     fresh nonce). This is the cryptographic teeth that survive a trust boundary: a
  //     tampered/unsigned/wrong-key/stale/replayed principal is refused BEFORE authorize.
  //     The VERIFIED principal (the one inside the signed envelope) becomes the
  //     authenticated actor downstream. When no verifier is injected, the P0/P1b-1
  //     host-stamped attribution stands (additive — existing wiring is unaffected).
  let authenticated: MembranePrincipal = principal
  if (opts.verifyPrincipal != null) {
    if (target.signedPrincipal == null) {
      return secureFailure(
        target.request.correlationId,
        'membrane.principal_unverified',
        new UnverifiedPrincipalError(target.capabilityId, 'malformed').message,
      )
    }
    const verification = opts.verifyPrincipal(target.signedPrincipal)
    if (!verification.ok || !isRegisteredMembranePrincipal(verification.principal)) {
      return secureFailure(
        target.request.correlationId,
        'membrane.principal_unverified',
        new UnverifiedPrincipalError(
          target.capabilityId,
          verification.ok ? 'malformed' : verification.reason,
        ).message,
      )
    }
    authenticated = verification.principal
  }

  // 2. authorize — classify (CP/AP per the immutable policy evaluator) + ENFORCE + RECORD. The evaluator
  //    classifies the COMMAND (`invoke`), NOT the capability it targets (ADR 0128 —
  //    the registry tags operations). The authority is structurally IN the path.
  const command = target.command ?? 'invoke'
  const decision = policyEvaluator(command)

  // The rejection path (ADR 0134 P1a + P1b-1). A CP command executes ONLY through
  // propose-then-confirm: it must carry a VALID confirmation whose broker SINGLE-USE
  // TOKEN verifies + consumes for THIS command (the load-bearing check; consuming it
  // here is what defeats replay). An AP command needs none. Anything else → reject.
  // NOTE: `hasValidConfirmation` has the SIDE EFFECT of consuming the token, so it is
  // evaluated ONLY for a CP command (AP short-circuits before it — a token is never
  // burned on an AP invoke).
  const outcome: InvokeOutcome =
    decision.authority === 'CP' &&
    !hasValidConfirmation(command, target.confirmation, broker)
      ? 'reject'
      : 'always-allow'

  opts.decisionSink?.record({
    command,
    capabilityId: target.capabilityId,
    // The AUTHENTICATED principal — the cryptographically-verified one when a
    // `verifyPrincipal` verifier is wired (P1b-2), else the host-stamped principal.
    // Projected to inert attribution: the sink is caller-supplied and the credential is a
    // bearer token, so what leaves here carries identity but no authority.
    principal: principalAttribution(authenticated),
    decision,
    outcome,
    correlationId: target.request.correlationId,
    recordedAt: new Date().toISOString(),
  })

  if (outcome === 'reject') {
    // Refused BEFORE SEC-2/execute — a uniform failed envelope, not a thrown crash.
    return secureFailure(
      target.request.correlationId,
      'membrane.authority_rejected',
      new AuthorityRejectedError(command).message,
    )
  }

  // 3. SEC-2 (idempotency) → SEC-3 (redaction) → execute — owned by the executor
  //    (`shell.invoke` → `invokeCapability`). The executor NEVER throws (M3 promise).
  return execute(target.request)
}

/**
 * Is the supplied confirmation VALID for this command (ADR 0134 P1a + P1b-1)? It must:
 *   (a) exist;
 *   (b) name the SAME command being invoked (defense-in-depth — the broker ALSO binds
 *       the token to the command server-side);
 *   (c) carry a non-anonymous `confirmedBy` (defense-in-depth — the broker stamps WHO);
 *   (d) **LOAD-BEARING:** its single-use `token` VERIFY + CONSUME for THIS command via
 *       trusted broker. Without a broker wired, a CP
 *       command can NEVER pass — fail-closed. Consuming the token here is what makes the
 *       confirmation single-use: a replay of the same token fails on the second invoke.
 *
 * The shape checks (a)–(c) are evaluated FIRST so a malformed confirmation does NOT burn
 * a real token (no consume on a shape failure). Only when the shape holds is the token
 * consumed — so a genuine token is spent exactly once, on a real attempt.
 *
 * Token-bound + non-forgeable / non-replayable WITHIN this process at P1b-1. P1b-2 adds
 * the Ed25519 cross-process binding (NOT here).
 */
function hasValidConfirmation(
  command: string,
  confirmation: ConfirmationEvidence | undefined,
  broker: unknown,
): boolean {
  // Shape (defense-in-depth) — cheap + side-effect-free; rejects before burning a token.
  if (
    confirmation == null ||
    confirmation.command !== command ||
    typeof confirmation.token !== 'string' ||
    confirmation.token.trim().length === 0 ||
    !isAttributed(confirmation.confirmedBy) ||
    !isRegisteredMembranePrincipal(confirmation.confirmedBy) ||
    !isRegisteredConfirmationEvidence(confirmation)
  ) {
    return false
  }
  // Fail-closed: no trusted broker wired ⇒ no way to verify a token ⇒ a CP command cannot pass.
  return consumeTrustedConfirmation(broker, confirmation.token, command)
}

/** A uniform `membrane`-domain failed envelope for a Secure-face fault (never thrown to the UI). */
function secureFailure(correlationId: string, code: string, message: string): CapabilityResult {
  return {
    jobId: `job:membrane-pep:${correlationId}`,
    status: 'failed',
    progress: 0,
    artifacts: [],
    usage: { unit: 'call', quantity: 0, tier: 'local' },
    error: {
      faultDomain: 'membrane',
      retryable: false,
      code,
      message,
    },
  }
}
