/**
 * Cross-language proof for the canonical-node-key signing ROUTE (ADR 0134 P2 — node-key
 * reachability RESOLVED). This is the end-to-end evidence that the resolution claims:
 *
 *   the .NET `local-node-host` `/api/local-node/current-principal-signature` route —
 *   signing with the node's CANONICAL identity (`RootSeedHex`→Ed25519 keypair, NSec)
 *   over the `CanonicalJson.SerializeSignable` form (#1254) — produces an envelope that
 *   the REAL TS `verifySignedPrincipal` ACCEPTS, with the node public key pinned as the
 *   trusted issuer.
 *
 * Where `signed-principal.interop.test.ts` proves the canonical BYTES agree and a
 * node:crypto signature stands in for the .NET one, THIS test pins an envelope produced
 * by the ACTUAL .NET route signer (NSec over the foundation `Ed25519Signer` built from a
 * fixed `RootSeedHex` seed) and runs it through the production verifier unchanged. The
 * canonical-node-key → TS-verify path is therefore proven with a real .NET signature, not
 * a TS stand-in.
 *
 * ## The pinned vector (captured from a real .NET test run)
 *
 * Produced by `CurrentPrincipalSignatureRouteTests.EmitDeterministicVectorForTsPin`
 * (apps/local-node-host/tests/Health) for the FIXED inputs:
 *   - node seed   = 32 × 0x07  → node public key `6kpsY-KcUgq-9VB7Ey7F-ZVHdq6-vnuSQh7qaRRG0iw`
 *   - principal   = `{ id: os:alice, displayName: Alice Example, kind: local-os-user }`
 *   - issuedAt    = 1781827200000 (epoch-ms integer — the canonical timestamp form)
 *   - nonce       = 11111111-2222-3333-4444-555555555555
 *
 * The signature is the EXACT base64url the .NET NSec signer emitted over the canonical
 * bytes. If the canonical form, the route's envelope shape, or `FromSeed` ever regress,
 * the signature stops verifying here — this test is the tripwire. Re-run the .NET emit
 * fact (with `CPSR_VECTOR_CAPTURE_PATH`) and re-pin only if the change is intentional.
 */

import { describe, expect, it } from 'vitest'

import type { MembranePrincipal } from './pep.js'
import {
  type SignedPrincipalEnvelope,
  verifySignedPrincipal,
} from './signed-principal.js'

/**
 * The EXACT envelope the .NET route signer produced for the fixed vector — copied
 * verbatim from the captured `/api/local-node/current-principal-signature` shape
 * (camelCase, `nodePublicKey` is the trust anchor === `issuerId`).
 */
const DOTNET_ROUTE_ENVELOPE = {
  principal: {
    id: 'os:alice',
    displayName: 'Alice Example',
    kind: 'local-os-user',
  },
  issuerId: '6kpsY-KcUgq-9VB7Ey7F-ZVHdq6-vnuSQh7qaRRG0iw',
  issuedAt: 1781827200000,
  nonce: '11111111-2222-3333-4444-555555555555',
  signature:
    '2sdcP_-YnQWp0egbhAyK5kyAzjYDJ2b2TrdJsWnsCUs6QCsA_9jtL6A7fqFzyTC3i8m2tg3A44QYOEFm0Zp5DA',
  nodePublicKey: '6kpsY-KcUgq-9VB7Ey7F-ZVHdq6-vnuSQh7qaRRG0iw',
} as const

/** The signed-principal envelope shape the verifier consumes (drop the extra nodePublicKey). */
const ENVELOPE: SignedPrincipalEnvelope = {
  // A principal that arrived OVER THE WIRE — deserialized from the .NET route's JSON, never minted
  // here. No wire value can carry the compile-time brand the host mint stamps, and verifying this
  // envelope is exactly the step that decides whether it may be trusted at all.
  principal: DOTNET_ROUTE_ENVELOPE.principal as MembranePrincipal,
  issuerId: DOTNET_ROUTE_ENVELOPE.issuerId,
  issuedAt: DOTNET_ROUTE_ENVELOPE.issuedAt,
  nonce: DOTNET_ROUTE_ENVELOPE.nonce,
  signature: DOTNET_ROUTE_ENVELOPE.signature,
}

// Clock anchored AT the pinned issuedAt so the ±30s replay window is satisfied for a
// fixed historical vector (the signature is timeless; the window check is not).
const ATvector = () => DOTNET_ROUTE_ENVELOPE.issuedAt

describe('canonical-node-key signing ROUTE → TS verify (ADR 0134 P2 reachability RESOLVED)', () => {
  it('the .NET route signature VERIFIES with the node public key pinned as the trusted issuer', () => {
    const result = verifySignedPrincipal(ENVELOPE, {
      // PIN the node public key as the sole trusted issuer — a real verifier deployment
      // configures exactly this (the node pubkey the route returned).
      trustedIssuers: new Set([DOTNET_ROUTE_ENVELOPE.nodePublicKey]),
      now: ATvector,
    })
    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.principal.id).toBe('os:alice')
      expect(result.principal.kind).toBe('local-os-user')
    }
  })

  it('nodePublicKey IS the issuerId (the trust anchor the route returns alongside the envelope)', () => {
    expect(DOTNET_ROUTE_ENVELOPE.nodePublicKey).toBe(DOTNET_ROUTE_ENVELOPE.issuerId)
    // base64url of raw 32 pubkey bytes → 43 chars unpadded.
    expect(DOTNET_ROUTE_ENVELOPE.nodePublicKey).toHaveLength(43)
  })

  it('a DIFFERENT trusted issuer is rejected — a valid signature from the wrong key is not auth', () => {
    const result = verifySignedPrincipal(ENVELOPE, {
      trustedIssuers: new Set(['AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE']), // 32×0x01, not the node
      now: ATvector,
    })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('untrusted-issuer')
  })

  it('a TAMPERED principal id breaks the .NET signature (the load-bearing crypto check)', () => {
    const tampered: SignedPrincipalEnvelope = {
      ...ENVELOPE,
      principal: { ...ENVELOPE.principal, id: 'os:root' },
    }
    const result = verifySignedPrincipal(tampered, {
      trustedIssuers: new Set([DOTNET_ROUTE_ENVELOPE.nodePublicKey]),
      now: ATvector,
    })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('bad-signature')
  })

  it('a TAMPERED displayName breaks the .NET signature (full-payload coverage)', () => {
    const tampered: SignedPrincipalEnvelope = {
      ...ENVELOPE,
      principal: { ...ENVELOPE.principal, displayName: 'Mallory' },
    }
    const result = verifySignedPrincipal(tampered, {
      trustedIssuers: new Set([DOTNET_ROUTE_ENVELOPE.nodePublicKey]),
      now: ATvector,
    })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('bad-signature')
  })

  it('a stale timestamp (outside the ±window) is rejected even with a valid .NET signature', () => {
    const result = verifySignedPrincipal(ENVELOPE, {
      trustedIssuers: new Set([DOTNET_ROUTE_ENVELOPE.nodePublicKey]),
      now: () => DOTNET_ROUTE_ENVELOPE.issuedAt + 60_000, // +60s, outside ±30s
    })
    expect(result.ok).toBe(false)
    if (!result.ok) expect(result.reason).toBe('stale-timestamp')
  })
})
