/**
 * The host-side principal SIGNER (ADR 0134 P1b-2) — mints a node-SIGNED principal
 * envelope in the TRUSTED HOST. The verify side is keyless (public-key); THIS is the
 * side that holds the secret, so the key-custody rule is load-bearing here.
 *
 * ## Key custody (the load-bearing rule)
 *
 * The node Ed25519 SIGNING (private) key lives HOST-SIDE only — NEVER the renderer,
 * NEVER `DevKeyStore`, NEVER a renderer-side signer. The renderer receives an
 * already-signed envelope across the IPC bridge and only forwards it; it cannot mint
 * one. This module runs in the Node host process (the bridge / the SDK), which is the
 * trusted minting point on the Harborline App invoke path TODAY.
 *
 * ## The seam — where the node key comes from (P2 node-key reachability RESOLVED)
 *
 * The CANONICAL node identity is the .NET `local-node-host` root identity
 * (`LocalNodeOptions.RootSeedHex` → the Ed25519 keypair). RESOLVED (CIC/Admiral-ratified):
 * the canonical node key reaches the Harborline App via a BOUNDED LOOPBACK SIGNING ROUTE on the
 * `local-node-host` — `GET /api/local-node/current-principal-signature`
 * (`apps/local-node-host/Health/CurrentPrincipalSignatureRoutes.cs`). The route resolves the
 * host's current OS-user principal HOST-SIDE, signs it with the canonical node key over THIS
 * module's canonical form, and returns a signed-principal envelope `verifySignedPrincipal`
 * accepts with the node pubkey pinned. The PRIVATE KEY STAYS IN THE .NET HOST — it is never
 * copied to the Harborline App / renderer / this TS layer; the Harborline App reaches a SIGNATURE by
 * CALLING the route, not by holding the key.
 *
 * The PRODUCTION principal-signing path is therefore: the Harborline App (inc-4) CALLS that loopback
 * route and forwards the node-signed envelope. The `NodeSigningKey` paths in THIS module —
 * `fromSeedHex` and `generate` — are now the DEV / TEST path: a single-host, single-process
 * signer so the sign↔verify pair round-trips locally without the .NET node running.
 * `fromSeedHex` remains the deterministic bridge (the SAME `RootSeedHex` seed yields the SAME
 * key here as in the .NET node), but a real deployment's principal signature comes from the
 * route, not from a seed handed to this module.
 *
 * INC-4 wires the Harborline App→node call (the Harborline App↔node connection) + the cross-process CALLER
 * AUTH for that route (a session token the Harborline App presents — loopback-bind alone
 * authenticates the host, not the calling process). That auth is the inc-4 requirement
 * tracked on the .NET route (`CurrentPrincipalSignatureRoutes` INC-4 TODO); it is NOT yet
 * enforced, so the route is dev/single-host-trusted until inc-4.
 *
 * BOTH P2 items are now closed: (1) canonical-bytes reconciliation — the .NET and TS canonical
 * signable forms are byte-identical for the full input domain (RFC 8785 / JCS minimal escaping
 * + integer epoch-ms timestamps), proved by `signed-principal.interop.test.ts`; (2) node-key
 * reachability — the route above + `node-route-principal.interop.test.ts`, which pins a REAL
 * .NET-route-signed envelope and verifies it through this module's verifier. A .NET-route-signed
 * principal verifies HERE with NO format work.
 *
 * Uses only `node:crypto` (built-in Ed25519 — no @noble / tweetnacl). NODE-ONLY.
 */

import {
  createPrivateKey,
  createPublicKey,
  generateKeyPairSync,
  randomUUID,
  sign as edSign,
  type KeyObject,
} from 'node:crypto'

import type { MembranePrincipal } from './pep.js'
import {
  serializeSignablePrincipal,
  type SignedPrincipalEnvelope,
} from './signed-principal.js'
import { assertNoLegacyOperationalVariables } from '../runtime/operational-environment.js'

/** The fixed PKCS8 DER prefix for a raw 32-byte Ed25519 seed (RFC 8410 §7). */
const ED25519_PKCS8_PREFIX = Buffer.from('302e020100300506032b657004220420', 'hex')

/**
 * A node SIGNING key — the trusted-host secret that mints signed-principal envelopes.
 * Wraps a `node:crypto` Ed25519 private key + its public `issuerId` (base64url of the
 * raw 32-byte public key, byte-identical to .NET `PrincipalId.ToBase64Url()`).
 *
 * The SECRET (the private `KeyObject`) never leaves this object; callers get only the
 * public `issuerId` + the `sign` operation. This is the key-custody boundary.
 */
export class NodeSigningKey {
  private constructor(
    private readonly privateKey: KeyObject,
    /** The node PUBLIC key id — base64url-unpadded raw 32 bytes (the verifier's trust anchor). */
    readonly issuerId: string,
  ) {}

  /**
   * Build a signing key from a raw 32-byte Ed25519 SEED (hex). This is the seam to the
   * .NET node identity: `LocalNodeOptions.RootSeedHex` is exactly a 64-char hex 32-byte
   * seed, so when the cross-node trust model lands (P2), the SAME seed produces the SAME
   * key here as in the .NET node — the signer becomes the node, no format change.
   *
   * @param seedHex 64-char hex (32 raw bytes). Throws on a malformed seed (fail-fast —
   *   a misconfigured key must not silently fall back to an ephemeral one).
   */
  static fromSeedHex(seedHex: string): NodeSigningKey {
    const seed = Buffer.from(seedHex, 'hex')
    if (seed.length !== 32) {
      throw new Error(
        `NodeSigningKey seed must be exactly 32 bytes (64 hex chars); got ${seed.length} bytes`,
      )
    }
    const der = Buffer.concat([ED25519_PKCS8_PREFIX, seed])
    const privateKey = createPrivateKey({ key: der, format: 'der', type: 'pkcs8' })
    const issuerId = NodeSigningKey.issuerIdOf(privateKey)
    return new NodeSigningKey(privateKey, issuerId)
  }

  /**
   * Generate a FRESH ephemeral signing key (single-process default). Used when no node
   * seed is provided — the host both signs and verifies with this same key, so the
   * sign↔verify pair round-trips on ONE host. NOT durable across restarts (a fresh key
   * each boot); a real deployment pins the seed via `fromSeedHex`. The seam note flags
   * promoting this to the .NET node key for cross-node trust (P2).
   */
  static generate(): NodeSigningKey {
    const { privateKey } = generateKeyPairSync('ed25519')
    const issuerId = NodeSigningKey.issuerIdOf(privateKey)
    return new NodeSigningKey(privateKey, issuerId)
  }

  /** Derive the base64url-unpadded raw-32-byte public key id from a private key. */
  private static issuerIdOf(privateKey: KeyObject): string {
    const jwk = createPublicKey(privateKey).export({ format: 'jwk' }) as { x?: string }
    if (!jwk.x) throw new Error('failed to export Ed25519 public key as JWK')
    // jwk.x is already base64url-unpadded raw 32 bytes.
    return jwk.x
  }

  /** Sign the canonical signable bytes with the node private key (raw 64-byte Ed25519). */
  signCanonical(signable: Buffer): Buffer {
    return edSign(null, signable, this.privateKey)
  }
}

/**
 * The single-host default node signing key, lazily resolved ONCE per process:
 *   - if `CAPABILITY_HOST_NODE_SEED_HEX` is set (a 64-char hex seed), derive the key from it (the
 *     pinnable path — the seam to the .NET node seed);
 *   - else generate an ephemeral key (the dev/single-process default — sign + verify
 *     on the same host still round-trips).
 *
 * Resolving once keeps the issuerId stable for the process lifetime (so the verifier's
 * `trustedIssuers` set — when it pins the host key — stays valid across invokes).
 */
let cachedDevKey: NodeSigningKey | null = null
export function devNodeSigningKey(env: NodeJS.ProcessEnv = process.env): NodeSigningKey {
  assertNoLegacyOperationalVariables(env)
  if (cachedDevKey != null) return cachedDevKey
  const seedHex = env.CAPABILITY_HOST_NODE_SEED_HEX
  cachedDevKey =
    seedHex != null && seedHex.trim().length > 0
      ? NodeSigningKey.fromSeedHex(seedHex.trim())
      : NodeSigningKey.generate()
  return cachedDevKey
}

/** Test-only: reset the cached dev key so a test can assert seed-vs-ephemeral selection. */
export function resetDevNodeSigningKeyForTests(): void {
  cachedDevKey = null
}

/** Options for `signPrincipal`. */
export interface SignPrincipalOptions {
  /** Injectable clock (ms epoch) for deterministic tests. Defaults to `Date.now`. */
  readonly now?: () => number
  /** Injectable nonce for deterministic tests. Defaults to a fresh `randomUUID()`. */
  readonly nonce?: string
}

/**
 * MINT a node-signed principal envelope (ADR 0134 P1b-2) — the trusted-host signing
 * operation. Stamps `issuedAt` (now, as integer epoch-ms) + a fresh `nonce`, builds the
 * canonical signable bytes (the cross-language-compatible `{issuedAt, issuerId, nonce, payload}`
 * form, byte-identical to .NET — RFC 8785/JCS + epoch-ms),
 * Ed25519-signs them with the node key, and returns the JSON-transportable envelope.
 *
 * The produced envelope verifies under `verifySignedPrincipal` with `trustedIssuers`
 * containing `key.issuerId`. The renderer NEVER calls this (key custody) — only the
 * host (the bridge / SDK) does.
 */
export function signPrincipal(
  principal: MembranePrincipal,
  key: NodeSigningKey,
  opts: SignPrincipalOptions = {},
): SignedPrincipalEnvelope {
  // issuedAt is the integer epoch-ms instant — the single canonical timestamp form shared
  // with .NET (`DateTimeOffset.ToUnixTimeMilliseconds()`). NOT an ISO string.
  const issuedAt = (opts.now ?? Date.now)()
  const nonce = opts.nonce ?? randomUUID()
  const signable = serializeSignablePrincipal(principal, key.issuerId, issuedAt, nonce)
  const signature = key.signCanonical(signable).toString('base64url')
  return {
    principal,
    issuerId: key.issuerId,
    issuedAt,
    nonce,
    signature,
  }
}
