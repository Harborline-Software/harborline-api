/**
 * The verifiable-principal tests (ADR 0134 P1b-2). Prove the node-signed,
 * replay-protected principal:
 *   (a) a genuine node-signed principal VERIFIES + returns the trusted principal;
 *   (b) a TAMPERED principal (mutated id / displayName / kind) → signature-fail → reject;
 *   (c) an UNSIGNED / WRONG-KEY / wrong-length-sig principal → reject;
 *   (d) an UNTRUSTED issuer (valid signature, key not in the trusted set) → reject;
 *   (e) a REPLAYED principal (stale `issuedAt` OR reused nonce) → reject;
 *   (f) the seen-nonce set is bounded (prunes expired entries).
 *
 * Uses the host-side signer (`signPrincipal` / `NodeSigningKey`) to mint genuine
 * envelopes — the sign↔verify pair round-trips end-to-end on one host with `node:crypto`.
 */

import { localOsUserPrincipal } from '@harborline-software/api-contracts/principal'
import { describe, expect, it } from 'vitest'

import type { MembranePrincipal } from './pep.js'
import { mintMembranePrincipal } from './host-principal.js'
import {
  NodeSigningKey,
  signPrincipal,
} from './host-principal-signer.js'
import {
  ensureWellFormedUtf16,
  SeenNonceSet,
  serializeSignablePrincipal,
  verifySignedPrincipal,
  PRINCIPAL_TIMESTAMP_SKEW_SECONDS,
  type SignedPrincipalEnvelope,
} from './signed-principal.js'

const ALICE = mintMembranePrincipal(localOsUserPrincipal('alice'))

/**
 * A principal the host mint could NEVER produce, built by hand on purpose.
 *
 * `mintMembranePrincipal` emits a frozen literal in one fixed key order and rejects anything that
 * is not a well-formed `local-os-user`/`service` identity — so the key-ORDER fixture and the
 * ill-formed-UTF-16 vectors below have no minted equivalent. They are adversarial PAYLOADS handed
 * to the serializer, never credentials: nothing here reaches the PEP, and the brand is a
 * compile-time marker with no runtime footprint.
 */
function unmintedPrincipal(
  fields: { readonly id: string; readonly displayName?: string; readonly kind?: string },
): MembranePrincipal {
  return fields as MembranePrincipal
}

/** A fixed-seed node key so tests are deterministic (issuerId is stable). */
function nodeKey(fill = 7): NodeSigningKey {
  return NodeSigningKey.fromSeedHex(Buffer.alloc(32, fill).toString('hex'))
}

/** Mint a genuine envelope at a fixed clock so replay-window tests are deterministic. */
function mint(
  principal: MembranePrincipal,
  key: NodeSigningKey,
  nowMs: number,
  nonce?: string,
): SignedPrincipalEnvelope {
  return signPrincipal(principal, key, { now: () => nowMs, nonce })
}

describe('verifiable principal — genuine envelope verifies (P1b-2)', () => {
  it('a node-signed principal VERIFIES and returns the trusted principal', () => {
    const key = nodeKey()
    const now = Date.parse('2026-06-19T12:00:00.000Z')
    const envelope = mint(ALICE, key, now)

    const result = verifySignedPrincipal(envelope, {
      trustedIssuers: new Set([key.issuerId]),
      now: () => now,
    })

    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.principal.id).toBe('os:alice')
      expect(result.principal.displayName).toBe('alice')
      expect(result.principal.kind).toBe('local-os-user')
    }
  })

  it('verifies WITHOUT a trustedIssuers set (single-host default: any valid signature)', () => {
    const key = nodeKey()
    const now = Date.parse('2026-06-19T12:00:00.000Z')
    const envelope = mint(ALICE, key, now)
    expect(verifySignedPrincipal(envelope, { now: () => now }).ok).toBe(true)
  })
})

describe('verifiable principal — tamper rejection (P1b-2)', () => {
  const key = nodeKey()
  const now = Date.parse('2026-06-19T12:00:00.000Z')

  it('REJECTS a principal whose id was mutated after signing (bad-signature)', () => {
    const envelope = mint(ALICE, key, now)
    const tampered: SignedPrincipalEnvelope = {
      ...envelope,
      principal: { ...envelope.principal, id: 'os:mallory' },
    }
    const result = verifySignedPrincipal(tampered, { now: () => now })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('bad-signature')
  })

  it('REJECTS a principal whose displayName was mutated (bad-signature)', () => {
    const envelope = mint(ALICE, key, now)
    const tampered: SignedPrincipalEnvelope = {
      ...envelope,
      principal: { ...envelope.principal, displayName: 'Mallory' },
    }
    expect(verifySignedPrincipal(tampered, { now: () => now }).ok).toBe(false)
  })

  it('REJECTS a principal whose kind was mutated (bad-signature)', () => {
    const envelope = mint(ALICE, key, now)
    const tampered: SignedPrincipalEnvelope = {
      ...envelope,
      principal: { ...envelope.principal, kind: 'authenticated' },
    }
    expect(verifySignedPrincipal(tampered, { now: () => now }).ok).toBe(false)
  })

  it('REJECTS a mutated issuedAt (the timestamp is covered by the signature)', () => {
    const envelope = mint(ALICE, key, now)
    // Move issuedAt within the window so it is the SIGNATURE, not the replay gate, that fails.
    // issuedAt is an integer epoch-ms; bump it 5s, still inside the ±30s window.
    const tampered: SignedPrincipalEnvelope = {
      ...envelope,
      issuedAt: now + 5_000,
    }
    const result = verifySignedPrincipal(tampered, { now: () => now })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('bad-signature')
  })

  it('REJECTS a mutated nonce (the nonce is covered by the signature)', () => {
    const envelope = mint(ALICE, key, now)
    const tampered: SignedPrincipalEnvelope = { ...envelope, nonce: 'a-different-nonce' }
    expect(verifySignedPrincipal(tampered, { now: () => now }).ok).toBe(false)
  })
})

describe('verifiable principal — unsigned / wrong-key / malformed (P1b-2)', () => {
  const key = nodeKey()
  const now = Date.parse('2026-06-19T12:00:00.000Z')

  it('REJECTS an UNSIGNED envelope (empty signature → malformed)', () => {
    const envelope = mint(ALICE, key, now)
    const result = verifySignedPrincipal({ ...envelope, signature: '' }, { now: () => now })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('malformed')
  })

  it('REJECTS a signature from a DIFFERENT key (bad-signature)', () => {
    // Sign with key A but verify expecting key A's issuerId, with B's signature swapped in.
    const keyB = nodeKey(9)
    const envA = mint(ALICE, key, now)
    const envB = mint(ALICE, keyB, now)
    // Keep A's issuerId but swap in B's signature → does not verify against A's key.
    const forged: SignedPrincipalEnvelope = { ...envA, signature: envB.signature }
    const result = verifySignedPrincipal(forged, { now: () => now })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('bad-signature')
  })

  it('REJECTS a valid signature from an UNTRUSTED issuer (key not in the trusted set)', () => {
    const trusted = nodeKey(1)
    const attacker = nodeKey(2)
    const envelope = mint(ALICE, attacker, now) // genuinely signed, but by the wrong node
    const result = verifySignedPrincipal(envelope, {
      trustedIssuers: new Set([trusted.issuerId]),
      now: () => now,
    })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('untrusted-issuer')
  })

  it('REJECTS a malformed issuerId (not a 32-byte key)', () => {
    const envelope = mint(ALICE, key, now)
    const result = verifySignedPrincipal({ ...envelope, issuerId: 'not-a-real-key' }, { now: () => now })
    expect(result.ok).toBe(false)
    // A non-32-byte issuerId fails the key reconstruction → bad-signature.
    if (!result.ok) expect(result.reason).toBe('bad-signature')
  })

  it('REJECTS a wrong-length signature (not 64 bytes)', () => {
    const envelope = mint(ALICE, key, now)
    const result = verifySignedPrincipal(
      { ...envelope, signature: Buffer.alloc(32, 1).toString('base64url') },
      { now: () => now },
    )
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('bad-signature')
  })

  it('REJECTS a null / anonymous-principal envelope (malformed)', () => {
    expect(verifySignedPrincipal(null).ok).toBe(false)
    const key2 = nodeKey()
    const envelope = mint({ id: '   ' } as MembranePrincipal, key2, now)
    const result = verifySignedPrincipal(envelope, { now: () => now })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('malformed')
  })
})

describe('verifiable principal — replay rejection (P1b-2, ±30s window + seen-nonce)', () => {
  const key = nodeKey()

  it('uses the HandshakeProtocol ±30s precedent (not a new value)', () => {
    expect(PRINCIPAL_TIMESTAMP_SKEW_SECONDS).toBe(30)
  })

  it('REJECTS a STALE issuedAt (older than the ±window) — stale-timestamp', () => {
    const issuedAt = Date.parse('2026-06-19T12:00:00.000Z')
    const envelope = mint(ALICE, key, issuedAt)
    // Verify 31s later → outside the ±30s window.
    const result = verifySignedPrincipal(envelope, { now: () => issuedAt + 31_000 })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('stale-timestamp')
  })

  it('REJECTS a FUTURE-dated issuedAt (beyond +window) — stale-timestamp', () => {
    const issuedAt = Date.parse('2026-06-19T12:00:00.000Z')
    const envelope = mint(ALICE, key, issuedAt)
    const result = verifySignedPrincipal(envelope, { now: () => issuedAt - 31_000 })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('stale-timestamp')
  })

  it('ACCEPTS an issuedAt at the edge of the window (±exactly 30s)', () => {
    const issuedAt = Date.parse('2026-06-19T12:00:00.000Z')
    const envelope = mint(ALICE, key, issuedAt)
    expect(verifySignedPrincipal(envelope, { now: () => issuedAt + 30_000 }).ok).toBe(true)
  })

  it('REJECTS a REUSED nonce within the window — replayed-nonce', () => {
    const now = Date.parse('2026-06-19T12:00:00.000Z')
    const seen = new SeenNonceSet()
    const envelope = mint(ALICE, key, now, 'fixed-nonce-1')

    // First verify: fresh nonce → ok.
    const first = verifySignedPrincipal(envelope, { seenNonces: seen, now: () => now })
    expect(first.ok).toBe(true)

    // Replay the SAME envelope (same nonce): rejected on the second verify.
    const second = verifySignedPrincipal(envelope, { seenNonces: seen, now: () => now + 1_000 })
    expect(second.ok).toBe(false)
    if (!second.ok) expect(second.reason).toBe('replayed-nonce')
  })

  it('does NOT poison the seen-nonce set on a FORGED (bad-signature) envelope', () => {
    const now = Date.parse('2026-06-19T12:00:00.000Z')
    const seen = new SeenNonceSet()
    const genuine = mint(ALICE, key, now, 'shared-nonce')
    // A forged envelope carrying the SAME nonce but a tampered principal.
    const forged: SignedPrincipalEnvelope = {
      ...genuine,
      principal: { ...genuine.principal, id: 'os:mallory' },
    }
    // The forged verify fails on signature → must NOT record the nonce as seen.
    expect(verifySignedPrincipal(forged, { seenNonces: seen, now: () => now }).ok).toBe(false)
    // The GENUINE envelope with that nonce still verifies (the nonce was not burned).
    expect(verifySignedPrincipal(genuine, { seenNonces: seen, now: () => now }).ok).toBe(true)
  })

  it('the seen-nonce set prunes expired entries (memory-bound)', () => {
    const seen = new SeenNonceSet(PRINCIPAL_TIMESTAMP_SKEW_SECONDS)
    const t0 = Date.parse('2026-06-19T12:00:00.000Z')
    seen.record('n1', t0, t0)
    expect(seen.size).toBe(1)
    // A record far in the future prunes the old entry first.
    seen.record('n2', t0 + 120_000, t0 + 120_000)
    expect(seen.size).toBe(1) // n1 pruned (outside ±30s of the new now)
  })
})

describe('verifiable principal — canonical signable form (P1b-2)', () => {
  it('is deterministic regardless of principal key insertion order', () => {
    // Hand-built: the two literals differ ONLY in key insertion order, which is the whole subject
    // of this test — the mint would normalize both to its own fixed order and prove nothing.
    const a = unmintedPrincipal({ id: 'os:alice', displayName: 'alice', kind: 'local-os-user' })
    const b = unmintedPrincipal({ kind: 'local-os-user', id: 'os:alice', displayName: 'alice' })
    const issuerId = 'AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE'
    const issuedAt = Date.parse('2026-06-19T12:00:00.000Z') // integer epoch-ms
    const nonce = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
    expect(serializeSignablePrincipal(a, issuerId, issuedAt, nonce).toString('utf8')).toBe(
      serializeSignablePrincipal(b, issuerId, issuedAt, nonce).toString('utf8'),
    )
  })

  it('emits issuedAt as a BARE integer (no quotes) — matches .NET epoch-ms', () => {
    const issuerId = 'AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE'
    const issuedAt = Date.parse('2026-06-19T12:00:00.000Z')
    const json = serializeSignablePrincipal(
      ALICE,
      issuerId,
      issuedAt,
      'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
    ).toString('utf8')
    expect(json).toContain(`"issuedAt":${issuedAt},`) // unquoted number
    expect(json).not.toContain(`"issuedAt":"`) // NOT a quoted string
  })

  it('rejects a non-integer issuedAt (would not round-trip the .NET integer form)', () => {
    const issuerId = 'AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE'
    expect(() =>
      serializeSignablePrincipal(ALICE, issuerId, 1.5, 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'),
    ).toThrow(/integer epoch-ms/)
  })

  it('sorts envelope + payload keys ordinal, no whitespace', () => {
    const issuerId = 'AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE'
    const bytes = serializeSignablePrincipal(
      ALICE,
      issuerId,
      Date.parse('2026-06-19T12:00:00.000Z'),
      'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
    )
    const json = bytes.toString('utf8')
    // Envelope order: issuedAt < issuerId < nonce < payload.
    expect(json.indexOf('"issuedAt"')).toBeLessThan(json.indexOf('"issuerId"'))
    expect(json.indexOf('"issuerId"')).toBeLessThan(json.indexOf('"nonce"'))
    expect(json.indexOf('"nonce"')).toBeLessThan(json.indexOf('"payload"'))
    // Payload order: displayName < id < kind.
    expect(json.indexOf('"displayName"')).toBeLessThan(json.indexOf('"id"'))
    expect(json.indexOf('"id"')).toBeLessThan(json.indexOf('"kind"'))
    expect(json).not.toContain(' ')
  })
})

describe('verifiable principal — rejects ill-formed UTF-16 at the signing boundary (Tier-0)', () => {
  const ISSUER_ID = 'AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE'
  const ISSUED_AT = Date.parse('2026-06-19T12:00:00.000Z')
  const NONCE = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'

  // ── the bare guard ──────────────────────────────────────────────────────────────────────
  it('ensureWellFormedUtf16 THROWS on a lone HIGH surrogate (U+D800)', () => {
    expect(() => ensureWellFormedUtf16('ab\uD800cd')).toThrow(/ill-formed UTF-16/)
    expect(() => ensureWellFormedUtf16('ab\uD800cd')).toThrow(/index 2/) // exact UTF-16 position
  })

  it('ensureWellFormedUtf16 THROWS on a lone LOW surrogate (U+DC00)', () => {
    expect(() => ensureWellFormedUtf16('\uDC00xy')).toThrow(/ill-formed UTF-16/)
    expect(() => ensureWellFormedUtf16('\uDC00xy')).toThrow(/index 0/)
  })

  it('ensureWellFormedUtf16 THROWS on a high surrogate at the very end (no follower)', () => {
    expect(() => ensureWellFormedUtf16('trailing\uDBFF')).toThrow(/unpaired surrogate at index 8/)
  })

  it('ensureWellFormedUtf16 PASSES a correctly-paired astral character (😀 = U+1F600)', () => {
    expect(() => ensureWellFormedUtf16('a\u{1F600}b')).not.toThrow()
    expect(() => ensureWellFormedUtf16('plain ascii only')).not.toThrow()
    expect(() => ensureWellFormedUtf16('')).not.toThrow()
    // The adversarial interop vector (`& < > " é 😀` + RTL) is well-formed and must pass.
    expect(() => ensureWellFormedUtf16('Ada & "Lovelace" <é\u{1F600}> ‫مرحبا‬')).not.toThrow()
  })

  // ── the systemic boundary (every signer inherits this) ──────────────────────────────────
  it('serializeSignablePrincipal REJECTS a lone surrogate in displayName (fail-closed)', () => {
    // The mint accepts this (it validates shape, not UTF-16), so a GENUINE host principal is what
    // reaches the serializer — the guard has to hold for a real credential, not just a hand-built one.
    const principal = mintMembranePrincipal({
      id: 'os:alice',
      displayName: 'Mallory\uD800', // lone high surrogate — JSON.stringify would escape to \udXXX
      kind: 'local-os-user',
    })
    expect(() =>
      serializeSignablePrincipal(principal, ISSUER_ID, ISSUED_AT, NONCE),
    ).toThrow(/ill-formed UTF-16/)
  })

  it('serializeSignablePrincipal REJECTS a lone surrogate in id', () => {
    const principal = unmintedPrincipal({ id: 'os:\uDC00bad' })
    expect(() =>
      serializeSignablePrincipal(principal, ISSUER_ID, ISSUED_AT, NONCE),
    ).toThrow(/ill-formed UTF-16/)
  })

  it('serializeSignablePrincipal REJECTS a lone surrogate in kind', () => {
    const principal = unmintedPrincipal({ id: 'os:alice', kind: 'k\uDFFF' })
    expect(() =>
      serializeSignablePrincipal(principal, ISSUER_ID, ISSUED_AT, NONCE),
    ).toThrow(/ill-formed UTF-16/)
  })

  it('serializeSignablePrincipal still SIGNS a well-formed astral displayName (no regression)', () => {
    const principal = mintMembranePrincipal({
      id: 'os:alice',
      displayName: 'Ada 😀',
      kind: 'local-os-user',
    })
    const bytes = serializeSignablePrincipal(principal, ISSUER_ID, ISSUED_AT, NONCE)
    // 😀 stays literal 4-byte UTF-8 (f0 9f 98 80), exactly as the JCS-minimal escaping requires.
    expect(bytes.toString('hex')).toContain('f09f9880')
  })
})
