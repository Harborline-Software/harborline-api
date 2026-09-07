/**
 * The verifiable principal — a node-SIGNED, replay-protected principal envelope
 * (ADR 0134 P1b-2 / Decision 5).
 *
 * ## What this closes
 *
 * Through P1b-1 the principal is the host-stamped `os:<user>` ATTRIBUTION string
 * (`MembranePrincipal`). It is un-forgeable only WITHIN one host — the Rust host
 * (`capability.rs`) stamps it and the renderer never supplies it, so a renderer cannot lie
 * about WHO it is. But that guarantee evaporates the instant the principal crosses a
 * TRUST BOUNDARY (a remote membrane node, a relayed Invoke, an agent host on another
 * machine): nothing on the bytes proves the principal was minted by a key the verifier
 * trusts. The `kind`/`MembranePrincipal` is ATTRIBUTION-grade, not enforcement-grade
 * (ADR 0134 Decision 3).
 *
 * P1b-2 gives the principal CRYPTOGRAPHIC TEETH that survive a trust boundary by
 * WIRING the existing Ed25519 stack (NOT new crypto): the TRUSTED HOST signs the
 * principal at minting with a node key; a verifier (anywhere, keyless — it needs only
 * the node PUBLIC key) re-checks the signature + a ±30s replay window + a seen-nonce
 * set. A tampered, unsigned, wrong-key, or replayed principal fails closed.
 *
 * ## Cross-language canonical format (the load-bearing compat contract)
 *
 * The SIGNED bytes are the canonical form of the envelope
 * `{ issuedAt, issuerId, nonce, payload }`, BYTE-IDENTICAL to the .NET
 * `Harborline.Api.Foundation.Crypto.CanonicalJson.SerializeSignable` shape for the FULL input
 * domain (envelope + payload keys sorted ordinal, no whitespace, UTF-8) so whatever
 * language SIGNS and whatever language VERIFIES agree on the bytes. The single canonical
 * form (P2 canonical-bytes reconciliation, RFC 8785 / JCS) is:
 *   - `issuedAt` is an INTEGER number of Unix epoch-MILLISECONDS — `Date.getTime()` here,
 *     `DateTimeOffset.ToUnixTimeMilliseconds()` in .NET. NOT an ISO/`"O"` string: that
 *     deleted every precision/offset/`+`-escaping delta a string timestamp introduced.
 *   - string escaping is JCS-MINIMAL (RFC 8785 §3.2.2.2) — escape ONLY `"`, `\`, and
 *     U+0000–U+001F (with the `\b \t \n \f \r` short escapes + lowercase `\u00xx`);
 *     `& < > +`, accented + astral-plane characters, U+2028/U+2029, and the BOM are all
 *     LITERAL UTF-8. JS `JSON.stringify` IS the JCS reference for this rule; the .NET
 *     side emits the same explicitly (its `Utf8JsonWriter` encoders do NOT — they
 *     uppercase the hex and over-escape the astral plane / U+2028 / BOM, so .NET hand-
 *     writes the JCS escaping to match here).
 *   - `nonce` is the lowercase-hyphenated UUID/GUID TEXT form (NOT base64 of GUID bytes —
 *     that has a mixed-endian layout); `issuerId` is base64url of the raw 32 pubkey bytes.
 *
 * `signed-principal.interop.test.ts` pins the EXACT .NET reference bytes for an
 * adversarial vector (`& < > " é 😀` + RTL) and ASSERTS byte equality + a round-trip
 * signature verify in BOTH directions, so a divergence cannot slip in silently.
 *
 * This module pulls in only `node:crypto` (Ed25519 is built in — no @noble, no
 * tweetnacl) and is therefore NODE-ONLY. It is imported by the membrane PEP + the
 * host-side signer (both Node), NEVER by a renderer (the renderer receives an
 * already-signed envelope host-side and only forwards it).
 */

import { createPublicKey, verify as edVerify } from 'node:crypto'

import type { MembranePrincipal } from './pep.js'
import { registerMembranePrincipal } from './credential-boundary.js'

/**
 * The replay-protection window, in SECONDS, a signed principal's `issuedAt` must fall
 * within (±) of the verifier's wall-clock. REUSES the `HandshakeProtocol`
 * `HelloTimestampSkewSeconds = 30` precedent (sync-daemon-protocol §8 "Replay
 * protection") — the fleet already settled ±30s as the right window for an
 * NTP-synchronized-peers assumption; we do NOT invent a new value.
 */
export const PRINCIPAL_TIMESTAMP_SKEW_SECONDS = 30

/**
 * A node-SIGNED principal envelope (ADR 0134 P1b-2). `SignedOperation`-compatible:
 * the `signature` covers the canonical form of `{ issuedAt, issuerId, nonce, payload }`
 * (see `serializeSignablePrincipal`). The verifier needs ONLY the public `issuerId`
 * (the node pubkey) — verification is keyless in the sense that it holds no secret.
 *
 * Wire-shape note: all binary fields are base64url-unpadded strings so the envelope is
 * JSON-transportable across the Rust→Node STDIN bridge + any future remote transport,
 * and so `issuerId` is byte-identical to the .NET `PrincipalId.ToBase64Url()` form.
 */
export interface SignedPrincipalEnvelope {
  /** The principal being attested (the actor the Invoke is attributed to). */
  readonly principal: MembranePrincipal
  /**
   * WHO signed it — the NODE public key, base64url-unpadded of the raw 32 Ed25519
   * bytes (identical to .NET `PrincipalId.ToBase64Url()`). The verifier trusts a
   * signature ONLY if this matches a node key it is configured to trust.
   */
  readonly issuerId: string
  /**
   * The Unix epoch-MILLISECONDS instant the envelope was minted (the replay-window
   * anchor). An INTEGER, byte-identical to the .NET `DateTimeOffset.ToUnixTimeMilliseconds()`
   * — NOT an ISO string (that was the worst cross-language delta; see the module header).
   */
  readonly issuedAt: number
  /** A unique per-issuance nonce (UUID) — defeats replay WITHIN the window. */
  readonly nonce: string
  /**
   * The Ed25519 signature over the canonical signable bytes, base64url-unpadded of
   * the raw 64 bytes (identical to .NET `Signature.ToBase64Url()`).
   */
  readonly signature: string
}

/** Why a signed-principal verification failed (carried into the PEP's failed envelope). */
export type SignedPrincipalRejectionReason =
  | 'malformed' // missing/blank required field or a field of the wrong type
  | 'bad-signature' // the signature did not verify against `issuerId`
  | 'untrusted-issuer' // the issuer key is not in the verifier's trusted set
  | 'stale-timestamp' // `issuedAt` is outside the ±window of the verifier's clock
  | 'replayed-nonce' // a nonce already seen within the window (a replay)

/** The outcome of verifying a signed principal — `ok` carries the trusted principal. */
export type SignedPrincipalVerification =
  | { readonly ok: true; readonly principal: MembranePrincipal }
  | { readonly ok: false; readonly reason: SignedPrincipalRejectionReason }

/**
 * Throw if `s` is ill-formed UTF-16 — i.e. it contains an UNPAIRED surrogate: a high
 * surrogate (U+D800–U+DBFF) not immediately followed by a low surrogate (U+DC00–U+DFFF),
 * or a low surrogate not preceded by a high one. A correctly-paired surrogate (an astral
 * character such as 😀) is WELL-FORMED and passes.
 *
 * This is the TS mirror of .NET `CanonicalJson.EnsureWellFormedUtf16`: a signing canonical
 * form must be deterministic + unambiguous, and ill-formed UTF-16 has no well-defined UTF-8
 * encoding — so we REJECT it (fail-closed) rather than let `JSON.stringify` escape a lone
 * surrogate to a `\udXXX` sequence into a signed envelope. The error shape matches the .NET
 * side ("ill-formed UTF-16 (unpaired surrogate at index N)").
 *
 * Implemented by index (`charCodeAt`) — NOT a regex or string scan that would itself
 * normalise — so the reported index is the exact UTF-16 code-unit position of the violation.
 */
export function ensureWellFormedUtf16(s: string): void {
  for (let i = 0; i < s.length; i++) {
    const code = s.charCodeAt(i)
    if (code >= 0xd800 && code <= 0xdbff) {
      // High surrogate — must be followed by a low surrogate.
      const next = i + 1 < s.length ? s.charCodeAt(i + 1) : 0
      if (next < 0xdc00 || next > 0xdfff) {
        throw new TypeError(
          `CanonicalJson cannot sign ill-formed UTF-16 (unpaired surrogate at index ${i}) — reject, do not sign.`,
        )
      }
      i++ // consume the valid low surrogate of the pair
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      // A low surrogate here is unpaired (a paired one is consumed by the branch above).
      throw new TypeError(
        `CanonicalJson cannot sign ill-formed UTF-16 (unpaired surrogate at index ${i}) — reject, do not sign.`,
      )
    }
  }
}

/**
 * Build the CANONICAL signable bytes for a principal envelope — the bytes an Ed25519
 * signature covers, BYTE-IDENTICAL to .NET `CanonicalJson.SerializeSignable`
 * (`{ issuedAt, issuerId, nonce, payload }`, all keys sorted ORDINAL, no whitespace,
 * UTF-8). The `payload` is the `MembranePrincipal` with its keys sorted too.
 *
 * The single canonical form (P2 reconciliation): `issuedAt` is an INTEGER epoch-ms
 * emitted as a bare JSON number (matching .NET `ToUnixTimeMilliseconds()`); strings use
 * `JSON.stringify`, which is the RFC 8785 / JCS-minimal escaping the .NET side mirrors
 * exactly (only `"`, `\`, U+0000–U+001F escaped; `& < > +`, accented/astral/U+2028/BOM
 * literal). So an adversarial `displayName` (`& < > " é 😀` + RTL) signs to identical
 * bytes on both sides — proved by `signed-principal.interop.test.ts`.
 *
 * Determinism: keys are emitted in a fixed ordinal order (NOT relying on JS object
 * insertion order for correctness — we sort explicitly), and only the principal's
 * three known string fields are serialized (a principal carries no nested objects, so
 * no recursion is needed). `undefined` optional fields (`displayName`/`kind`) are
 * OMITTED, matching how the renderer/host construct the principal.
 *
 * @param issuedAt Unix epoch-MILLISECONDS (integer). A non-integer/non-finite value is a
 *   programming error — it would not round-trip through the .NET integer form — so it
 *   throws rather than silently producing divergent bytes.
 */
export function serializeSignablePrincipal(
  principal: MembranePrincipal,
  issuerId: string,
  issuedAt: number,
  nonce: string,
): Buffer {
  if (!Number.isInteger(issuedAt)) {
    throw new TypeError(
      `serializeSignablePrincipal: issuedAt must be an integer epoch-ms (got ${issuedAt})`,
    )
  }

  // Fail-closed on ill-formed UTF-16 (a lone/unpaired surrogate) BEFORE JSON.stringify — which would
  // otherwise escape a lone surrogate to a `\udXXX` sequence and SIGN ambiguous/malformed data. This
  // MIRRORS the .NET `CanonicalJson.EnsureWellFormedUtf16` guard: both languages REJECT ill-formed
  // UTF-16 (neither silently diverges), because a signing canonical form must be deterministic +
  // unambiguous and ill-formed UTF-16 has no well-defined UTF-8 encoding. A correctly-paired surrogate
  // (an astral character like 😀) is well-formed and passes. issuerId/nonce are machine-minted +
  // always well-formed; the payload's principal fields are the caller-supplied surface.
  if (principal.displayName !== undefined) ensureWellFormedUtf16(principal.displayName)
  ensureWellFormedUtf16(principal.id)
  if (principal.kind !== undefined) ensureWellFormedUtf16(principal.kind)

  // The payload object — keys sorted ordinal (displayName < id < kind), omitting any
  // undefined optional. `JSON.stringify(value)` of a plain string applies JCS-minimal
  // escaping (the .NET side hand-writes the same rule to match).
  const payloadParts: string[] = []
  if (principal.displayName !== undefined) {
    payloadParts.push(`${JSON.stringify('displayName')}:${JSON.stringify(principal.displayName)}`)
  }
  payloadParts.push(`${JSON.stringify('id')}:${JSON.stringify(principal.id)}`)
  if (principal.kind !== undefined) {
    payloadParts.push(`${JSON.stringify('kind')}:${JSON.stringify(principal.kind)}`)
  }
  const payload = `{${payloadParts.join(',')}}`

  // The envelope — keys already alphabetical (issuedAt < issuerId < nonce < payload).
  // issuedAt is a BARE NUMBER (no quotes), matching the .NET integer literal.
  const envelope =
    `{` +
    `${JSON.stringify('issuedAt')}:${String(issuedAt)},` +
    `${JSON.stringify('issuerId')}:${JSON.stringify(issuerId)},` +
    `${JSON.stringify('nonce')}:${JSON.stringify(nonce)},` +
    `${JSON.stringify('payload')}:${payload}` +
    `}`
  return Buffer.from(envelope, 'utf8')
}

/**
 * Reconstruct a `node:crypto` Ed25519 public key from the base64url-unpadded raw
 * 32-byte form (the `issuerId` wire shape, identical to .NET `PrincipalId`). Returns
 * `null` on malformed key bytes (treated as a verification failure, never thrown).
 */
function publicKeyFromIssuerId(issuerId: string): ReturnType<typeof createPublicKey> | null {
  try {
    const raw = Buffer.from(issuerId, 'base64url')
    if (raw.length !== 32) return null
    return createPublicKey({
      key: { kty: 'OKP', crv: 'Ed25519', x: raw.toString('base64url') },
      format: 'jwk',
    })
  } catch {
    return null
  }
}

/** Is a non-blank string? (Guards the wire fields before any crypto work.) */
function nonBlank(v: unknown): v is string {
  return typeof v === 'string' && v.trim().length > 0
}

/**
 * A bounded seen-nonce set for replay rejection WITHIN the window. Mirrors the
 * `HandshakeProtocol` discipline: a nonce already observed (within the freshness
 * window) is a replay. Entries are pruned once they fall outside ±window of the
 * current clock, so the set stays bounded to at most the number of distinct envelopes
 * minted in a 2×window span. NOT cross-process durable (a single-host, single-process
 * membrane today); a future remote tier persists it.
 */
export class SeenNonceSet {
  /** nonce -> the issuedAt (ms epoch) it was first recorded at. */
  private readonly seen = new Map<string, number>()

  constructor(private readonly skewSeconds: number = PRINCIPAL_TIMESTAMP_SKEW_SECONDS) {}

  /**
   * Record a nonce as seen at `issuedAtMs`. Returns `false` if it was ALREADY seen
   * (a replay), `true` if this is the first sighting. Prunes expired entries first so
   * a nonce whose window has fully passed does not block a legitimately-distinct
   * later envelope (nonces are unique per issuance regardless, so this only bounds
   * memory).
   */
  record(nonce: string, issuedAtMs: number, nowMs: number = Date.now()): boolean {
    this.prune(nowMs)
    if (this.seen.has(nonce)) return false
    this.seen.set(nonce, issuedAtMs)
    return true
  }

  private prune(nowMs: number): void {
    const horizon = nowMs - this.skewSeconds * 1000
    for (const [nonce, ts] of this.seen) {
      if (ts < horizon) this.seen.delete(nonce)
    }
  }

  /** Test-only: current tracked-nonce count (memory-bound assertion). */
  get size(): number {
    return this.seen.size
  }
}

/** Options for `verifySignedPrincipal`. */
export interface VerifySignedPrincipalOptions {
  /**
   * The node PUBLIC keys the verifier TRUSTS, as base64url `issuerId` strings. A
   * signature that verifies but whose issuer is not in this set is `untrusted-issuer`
   * — a valid signature from the WRONG key must NOT authenticate. When omitted, ANY
   * structurally-valid signature is accepted (single-host, single-key default: the
   * host both signs and verifies with the same node key, so there is nothing to pin
   * against — the seam note documents promoting this to a configured trust set when a
   * remote node becomes a signer).
   */
  readonly trustedIssuers?: ReadonlySet<string>
  /** The seen-nonce set for replay rejection (shared across verifies on one verifier). */
  readonly seenNonces?: SeenNonceSet
  /** The replay window (±seconds). Defaults to `PRINCIPAL_TIMESTAMP_SKEW_SECONDS`. */
  readonly skewSeconds?: number
  /** Injectable clock (ms epoch) for deterministic tests. Defaults to `Date.now`. */
  readonly now?: () => number
}

/**
 * VERIFY a signed-principal envelope (ADR 0134 P1b-2). Keyless in the secret sense —
 * it holds only public keys. Returns the TRUSTED principal on success; a typed
 * rejection reason otherwise. NEVER throws (a malformed field / bad key / crypto fault
 * is a `false` outcome, surfaced by the PEP as a uniform failed envelope).
 *
 * The checks, IN ORDER (cheapest + most-structural first, so a malformed envelope
 * never burns a crypto verify or a nonce slot):
 *   1. **shape** — every required field present + non-blank, principal non-anonymous;
 *   2. **trusted issuer** — `issuerId` is in `trustedIssuers` (when configured);
 *   3. **replay window** — `issuedAt` (epoch-ms integer) is within ±skew of `now`
 *      (REUSES the HandshakeProtocol ±30s precedent);
 *   4. **signature** — Ed25519-verifies against `issuerId` over the canonical bytes
 *      (the load-bearing cryptographic check — a tampered principal/id/kind/nonce/ts
 *      changes the bytes → fails here);
 *   5. **nonce freshness** — the nonce has not been seen within the window (recorded
 *      ONLY after the signature passes, so a forged envelope cannot poison the set).
 */
export function verifySignedPrincipal(
  envelope: SignedPrincipalEnvelope | null | undefined,
  opts: VerifySignedPrincipalOptions = {},
): SignedPrincipalVerification {
  // 1. shape — reject malformed BEFORE any crypto or nonce side effect. `issuedAt` is an
  //    integer epoch-ms (not a string) — a non-integer/non-finite value is malformed.
  if (
    envelope == null ||
    !nonBlank(envelope.issuerId) ||
    !Number.isInteger(envelope.issuedAt) ||
    !nonBlank(envelope.nonce) ||
    !nonBlank(envelope.signature) ||
    envelope.principal == null ||
    !nonBlank(envelope.principal.id)
  ) {
    return { ok: false, reason: 'malformed' }
  }

  // 2. trusted issuer — a valid signature from the WRONG key is not authentication.
  if (opts.trustedIssuers && !opts.trustedIssuers.has(envelope.issuerId)) {
    return { ok: false, reason: 'untrusted-issuer' }
  }

  // 3. replay window — stale/future-dated envelopes are rejected before the crypto
  //    verify (a replayed-but-validly-signed envelope cannot burn a CPU verify),
  //    mirroring HandshakeProtocol's "window gate before signature" order. `issuedAt`
  //    is already the epoch-ms integer — no parse needed.
  const issuedAtMs = envelope.issuedAt
  const now = (opts.now ?? Date.now)()
  const skewMs = (opts.skewSeconds ?? PRINCIPAL_TIMESTAMP_SKEW_SECONDS) * 1000
  if (Math.abs(issuedAtMs - now) > skewMs) {
    return { ok: false, reason: 'stale-timestamp' }
  }

  // 4. signature — the load-bearing check. Any tamper to principal/issuerId/issuedAt/
  //    nonce changes the canonical bytes and fails here.
  const publicKey = publicKeyFromIssuerId(envelope.issuerId)
  if (publicKey == null) {
    return { ok: false, reason: 'bad-signature' } // malformed key bytes
  }
  const signable = serializeSignablePrincipal(
    envelope.principal,
    envelope.issuerId,
    envelope.issuedAt,
    envelope.nonce,
  )
  let signatureBytes: Buffer
  try {
    signatureBytes = Buffer.from(envelope.signature, 'base64url')
  } catch {
    return { ok: false, reason: 'bad-signature' }
  }
  if (signatureBytes.length !== 64) {
    return { ok: false, reason: 'bad-signature' }
  }
  let verified = false
  try {
    verified = edVerify(null, signable, publicKey, signatureBytes)
  } catch {
    return { ok: false, reason: 'bad-signature' }
  }
  if (!verified) {
    return { ok: false, reason: 'bad-signature' }
  }

  // 5. nonce freshness — ONLY after the signature passes (a forged envelope must not
  //    be able to poison the seen set with an attacker-chosen nonce).
  if (opts.seenNonces && !opts.seenNonces.record(envelope.nonce, issuedAtMs, now)) {
    return { ok: false, reason: 'replayed-nonce' }
  }

  // PROMOTE to a real credential only when the signer was PINNED.
  //
  // A structurally-valid signature proves the envelope was not tampered with. It does not say the
  // signer is anyone we trust — that is what `trustedIssuers` is for, and it is optional with a
  // documented "accept ANY structurally-valid signature" default suited to the single-host case
  // where the same node signs and verifies.
  //
  // That default was harmless while a verification result was inert data. It is not harmless now
  // that verifying REGISTERS the principal, because the PEP authenticates by object identity: an
  // unpinned verifier would turn any self-signing caller into full authority, and the PEP's own
  // `isRegisteredMembranePrincipal` re-check could not catch it, having been satisfied here first.
  //
  // So the promotion is gated on a non-empty trust set. Without one the envelope still verifies —
  // callers reading `ok` are unaffected — but the principal stays unregistered, and the PEP refuses
  // it exactly like any other shape-only value. Fail-closed, and the check that follows is real.
  if (opts.trustedIssuers !== undefined && opts.trustedIssuers.size > 0) {
    registerMembranePrincipal(envelope.principal)
  }
  return { ok: true, principal: envelope.principal }
}
