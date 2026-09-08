/**
 * The `Principal` — WHO is acting (the actor an op is attributed to).
 *
 * The agent-client doctrine (CIC 2026-06-18) gives the Harborline App core ONE shared
 * authority gate (CP/AP, ADR 0128) that records WHO proposed and WHO confirmed a
 * CP op. Until now the broker recorded WHEN (`proposedAt`) but never WHO — the gate
 * ran anonymously, so the audit trail had no attribution (security-engineering
 * SPOT-CHECK 2026-06-18, forward-looking item A: "broker records no principal").
 *
 * A `Principal` is the minimal, SOURCE-EXPLICIT identity the gate attributes an op
 * to. CIC-confirmed posture: the local desktop uses the OS user as the PRIMARY
 * principal/claims source — no sign-in, no remote auth (those stay deferred to a
 * later trigger). The `kind` discriminator makes the source EXPLICIT and FLIPPABLE:
 * today every principal is a `local-os-user`; when the deferred auth work lands it
 * adds a new `kind` (e.g. `authenticated`) WITHOUT touching a single consumer — the
 * broker, the renderer avatar/profile UX, and the CLI all keep reading this one
 * interface; only the SOURCE that constructs it changes.
 *
 * TYPES ONLY — no runtime behaviour beyond a pure string-assembly builder; the file
 * pulls in NO Node API, so it is BROWSER-SAFE (the renderer imports it directly).
 * Framework-neutral. The single type source the renderer + SDK + broker all project
 * (the agent-client doctrine's "one typed source, every face a projection").
 */

/**
 * The SOURCE of a principal's identity + claims. The discriminator that keeps the
 * principal's origin explicit so the later auth work flips the source without
 * touching consumers.
 *
 * - `local-os-user` — the desktop's logged-in OS user (the CIC-confirmed primary
 *   source for the local desktop today; read host-side from the OS). A HUMAN kind.
 * - `service` — a non-interactive agent / service host (an agent-client acting on
 *   its own, ADR 0143). A NON-human kind. Load-bearing for the CP separation-of-duties
 *   gate (D-INV-5): a `service` principal may PROPOSE a CP op but can never CONFIRM one
 *   — a genuine CP confirmation requires a human (see `isHumanPrincipal`). This is the
 *   distinction the broker's self-approve fence turns on, so the boundary is a real,
 *   typed value rather than a vacuous check.
 *
 * Future kind (deferred to the later auth trigger; listed for intent only — NOT
 * implemented): an `authenticated` kind for a signed-in identity. Adding one is
 * additive — consumers read the shared `Principal` shape, not the `kind`.
 */
export type PrincipalKind = 'local-os-user' | 'service'

/**
 * The set of principal kinds that are HUMAN (accountable, interactive) — the allowlist
 * the CP separation-of-duties gate treats as eligible to CONFIRM a consequential op.
 * Fail-closed: any kind NOT in this set (a `service` agent host, or a malformed/unknown
 * kind) is treated as non-human. `local-os-user` is the one human kind today.
 */
const HUMAN_PRINCIPAL_KINDS: ReadonlySet<string> = new Set<PrincipalKind>(['local-os-user'])

/**
 * Is this principal a HUMAN (vs a non-interactive `service`/agent host)? The CP
 * separation-of-duties gate (ADR 0143 D-INV-5) requires the CONFIRMING principal of a
 * genuine CP op to be human — an agent may propose, only a human may confirm. Fail-closed:
 * an unknown/malformed kind is NOT human.
 */
export function isHumanPrincipal(principal: Principal): boolean {
  return HUMAN_PRINCIPAL_KINDS.has(principal.kind)
}

/**
 * WHO is acting — the actor an op is attributed to. The minimal identity the
 * authority gate records (`proposedBy` / `confirmedBy`) and the avatar/profile UX
 * renders. Never carries credentials or secrets (it is an attribution identity, not
 * an auth token) — SEC-6-aligned by construction.
 */
export interface Principal {
  /**
   * A STABLE identity string, namespaced by source so two kinds never collide:
   * a `local-os-user` is `os:<username>` (e.g. `os:<user>`). Stable across
   * a session for the same OS user; the namespace prefix mirrors `kind`.
   */
  id: string
  /** A human-readable name for display (the OS username today; a full name later). */
  displayName: string
  /** The SOURCE of this identity — explicit + flippable (see `PrincipalKind`). */
  kind: PrincipalKind
}

/**
 * A `Principal` sourced from the desktop's OS user (the CIC-confirmed primary
 * source today). Narrows `kind` to `local-os-user` for call sites that need the
 * source statically known (e.g. a test asserting the read came from the OS).
 */
export interface LocalOsUserPrincipal extends Principal {
  kind: 'local-os-user'
}

/** The id namespace prefix for an OS-user principal (`os:<username>`). */
export const OS_USER_ID_PREFIX = 'os:'

/**
 * Construct a `LocalOsUserPrincipal` from a raw OS username. PURE string assembly
 * — no Node API, browser-safe. The CALLER reads the username from its host
 * (Rust `std::env` host-side, `node:os` `userInfo()` in the SDK/CLI); this only
 * normalizes it into the shared shape so every source builds the SAME principal.
 *
 * An empty/whitespace username falls back to `unknown` so the principal is ALWAYS
 * non-null + attributable — the gate must never run anonymously (SPOT-CHECK A).
 *
 * @param username the raw OS username (`USER` / `USERNAME` / `os.userInfo().username`)
 * @param displayName optional human-readable name; defaults to the username
 */
export function localOsUserPrincipal(username: string, displayName?: string): LocalOsUserPrincipal {
  const name = username.trim().length > 0 ? username.trim() : 'unknown'
  return {
    id: `${OS_USER_ID_PREFIX}${name}`,
    displayName: displayName?.trim() || name,
    kind: 'local-os-user',
  }
}
