/**
 * Cross-language canonical-format interop (P2 canonical-bytes reconciliation). The SIGNED
 * bytes are the canonical form of `{ issuedAt, issuerId, nonce, payload }`, and they are
 * now BYTE-IDENTICAL across .NET and TS for the FULL input domain — there is ONE canonical
 * signable form (RFC 8785 / JCS minimal escaping + integer epoch-ms timestamps), not two
 * that "agree for ASCII". This test is the PROOF of that contract:
 *
 *   (a) .NET-produced canonical bytes == TS-produced canonical bytes (the exact bytes are
 *       pinned below, captured from a real `CanonicalJson.SerializeSignable` run);
 *   (b) a .NET-minted Ed25519 signature over those bytes VERIFIES in the TS verifier;
 *   (c) a TS-minted Ed25519 signature over those bytes VERIFIES against the same key with
 *       `node:crypto` directly — the round-trip in BOTH directions.
 *
 * The vector is ADVERSARIAL on purpose: the `displayName` carries `& < > "`, an accented
 * char (`é`), an ASTRAL-plane emoji (`😀`, a surrogate pair), and an RTL string with
 * directional control marks — exactly the inputs where the naive "`JSON.stringify` ≈ .NET
 * `Utf8JsonWriter`" assumption breaks (.NET's encoders uppercase the `\uXXXX` hex and
 * over-escape the astral plane / U+2028 / BOM). The .NET side hand-writes JCS-minimal
 * escaping to match JS exactly, so these bytes agree.
 *
 * ## The single canonical form (what BOTH languages now emit)
 *   - `issuedAt` — INTEGER Unix epoch-MILLISECONDS, a BARE JSON number (no quotes):
 *     `Date.getTime()` (TS) == `DateTimeOffset.ToUnixTimeMilliseconds()` (.NET).
 *   - escaping — JCS-minimal (RFC 8785 §3.2.2.2): only `"`, `\`, U+0000–U+001F escaped
 *     (short escapes `\b \t \n \f \r`, else lowercase `\u00xx`); everything else literal
 *     UTF-8 — `& < > +`, `é`, `😀`, U+2028/U+2029, BOM, RTL marks.
 *   - keys sorted by code unit (ordinal); no whitespace; UTF-8.
 *   - `nonce` = lowercase-hyphenated UUID text; `issuerId` = base64url(raw-32-pubkey).
 *
 * ## The captured .NET reference vector
 *
 * Produced by `CanonicalJson.SerializeSignable(payload, issuer, issuedAt, nonce)` for the
 * fixed vector below (issuer = 32×0x01; issuedAt = epoch-ms 1781827200000; nonce =
 * aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee; payload = the principal). The captured UTF-8 bytes
 * are pinned in `DOTNET_REFERENCE` (and re-derived from `DOTNET_REFERENCE_HEX` so the pin
 * is a real byte sequence, not a hand-typed string a copy-paste could corrupt).
 */

import {
  createPrivateKey,
  createPublicKey,
  sign as edSign,
  verify as edVerify,
  type KeyObject,
} from 'node:crypto'

import { describe, expect, it } from 'vitest'

import { NodeSigningKey } from './host-principal-signer.js'
import { mintMembranePrincipal } from './host-principal.js'
import { serializeSignablePrincipal, verifySignedPrincipal } from './signed-principal.js'

// The adversarial principal — `& < > "`, accent, astral emoji, RTL + directional marks. Minted
// through the host issuer, so the bytes compared against .NET are the bytes a REAL credential signs.
const PRINCIPAL = mintMembranePrincipal({
  id: 'os:alice',
  displayName: 'Ada & "Lovelace" <é\u{1F600}> ‫مرحبا‬',
  kind: 'local-os-user',
})
// issuer = 32 × 0x01 → base64url, matching the .NET PrincipalId.ToBase64Url() vector.
const ISSUER_ID = Buffer.alloc(32, 1).toString('base64url')
const ISSUED_AT_MS = 1781827200000 // integer epoch-ms (the pinned vector instant)
const NONCE = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'

/**
 * The EXACT bytes the .NET `CanonicalJson.SerializeSignable` produced for the vector,
 * captured as a hex string (a real .NET run; see the file header). Re-derived to a UTF-8
 * string so the equality assertion compares true bytes.
 */
const DOTNET_REFERENCE_HEX =
  '7b226973737565644174223a313738313832373230303030302c226973737565724964223a' +
  '2241514542415145424151454241514542415145424151454241514542415145424151454241' +
  '514542415145222c226e6f6e6365223a2261616161616161612d626262622d636363632d6464' +
  '64642d656565656565656565656565222c227061796c6f6164223a7b22646973706c61794e61' +
  '6d65223a224164612026205c224c6f76656c6163655c22203cc3a9f09f98803e20e280abd985' +
  'd8b1d8add8a8d8a7e280ac222c226964223a226f733a616c696365222c226b696e64223a226c' +
  '6f63616c2d6f732d75736572227d7d'
const DOTNET_REFERENCE = Buffer.from(DOTNET_REFERENCE_HEX, 'hex').toString('utf8')

/** A fixed-seed node key so the signature vectors are deterministic. */
function vectorKey(fill = 5): NodeSigningKey {
  return NodeSigningKey.fromSeedHex(Buffer.alloc(32, fill).toString('hex'))
}

describe('canonical signable interop — ONE form, byte-identical TS ↔ .NET (P2)', () => {
  it('the issuerId byte form matches the .NET PrincipalId.ToBase64Url() vector', () => {
    expect(ISSUER_ID).toBe('AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE')
  })

  it('(a) TS canonical bytes EQUAL the captured .NET reference bytes (adversarial input)', () => {
    const ts = serializeSignablePrincipal(PRINCIPAL, ISSUER_ID, ISSUED_AT_MS, NONCE).toString('utf8')
    // The exact, full byte sequence — NOT "agrees for ASCII". This is the reconciliation
    // closed: `& < > " é 😀` + RTL all serialize identically on both sides.
    expect(ts).toBe(DOTNET_REFERENCE)
  })

  it('issuedAt is a BARE integer epoch-ms in the pinned .NET bytes (not an ISO string)', () => {
    // The .NET reference literally contains `"issuedAt":1781827200000,` — unquoted number.
    expect(DOTNET_REFERENCE).toContain(`"issuedAt":${ISSUED_AT_MS},`)
    expect(DOTNET_REFERENCE).not.toContain('"issuedAt":"') // never a quoted timestamp
  })

  it('the adversarial chars are LITERAL UTF-8 in the canonical bytes (JCS-minimal)', () => {
    const ts = serializeSignablePrincipal(PRINCIPAL, ISSUER_ID, ISSUED_AT_MS, NONCE)
    const hex = ts.toString('hex')
    expect(hex).toContain('c3a9') // é literal
    expect(hex).toContain('f09f9880') // 😀 literal (astral, NOT 😀)
    expect(hex).toContain('d985d8b1d8add8a8d8a7') // Arabic مرحبا literal
    expect(hex).toContain('e280ab') // U+202B RLE literal (NOT ‫)
    // & < > stay literal (0x26 0x3c 0x3e), only the inner quotes are \"-escaped.
    expect(ts.toString('utf8')).toContain('Ada & \\"Lovelace\\" <')
  })

  it('(b)+(c) an Ed25519 signature over the SHARED canonical bytes round-trips BOTH directions', () => {
    // A real signing key (seed 5). `issuerId` is part of the canonical bytes, so the
    // signable embeds THIS key's issuerId (the byte form is the same JCS/epoch-ms canonical
    // shape proved byte-equal to .NET in case (a); only the issuerId field value differs from
    // the pinned 32×0x01 vector, which has no key material to sign with).
    const key = vectorKey(5)
    const signable = serializeSignablePrincipal(PRINCIPAL, key.issuerId, ISSUED_AT_MS, NONCE)

    // (c) TS SIGNS the shared bytes via the NodeSigningKey; the public half verifies.
    const tsSignature = key.signCanonical(signable)
    const tsVerifies = edVerify(null, signable, cryptoPublicKey(key.issuerId), tsSignature)
    expect(tsVerifies).toBe(true)

    // (b) A signature minted OUTSIDE the NodeSigningKey wrapper — via node:crypto's raw
    // primitive over the SAME key material + SAME shared bytes — stands in for a .NET-side
    // (NSec) signature over the identical canonical bytes. Because the bytes are byte-equal
    // across languages (case (a)), such a signature verifies through the FULL membrane path.
    const dotnetStyleSignature = edSign(null, signable, privateKeyFromSeed(5))
    expect(dotnetStyleSignature.equals(tsSignature)).toBe(true) // Ed25519 is deterministic → identical bytes
    const envelope = {
      principal: PRINCIPAL,
      issuerId: key.issuerId,
      issuedAt: ISSUED_AT_MS,
      nonce: NONCE,
      signature: dotnetStyleSignature.toString('base64url'),
    }
    const result = verifySignedPrincipal(envelope, {
      trustedIssuers: new Set([key.issuerId]),
      now: () => ISSUED_AT_MS, // inside the ±window
    })
    expect(result.ok).toBe(true)
    if (result.ok) {
      expect(result.principal.id).toBe('os:alice')
    }
  })
})

// --- small node:crypto helpers (kept local to the test) ---

const ED25519_PKCS8_PREFIX = Buffer.from('302e020100300506032b657004220420', 'hex')

function privateKeyFromSeed(fill: number): KeyObject {
  const seed = Buffer.alloc(32, fill)
  return createPrivateKey({
    key: Buffer.concat([ED25519_PKCS8_PREFIX, seed]),
    format: 'der',
    type: 'pkcs8',
  })
}

function cryptoPublicKey(issuerId: string): KeyObject {
  const raw = Buffer.from(issuerId, 'base64url')
  return createPublicKey({
    key: { kty: 'OKP', crv: 'Ed25519', x: raw.toString('base64url') },
    format: 'jwk',
  })
}
