/**
 * The composition manifest (ADR 0125 D2/D3) — a solution (an EDITION) is a
 * market-curated composition of capabilities, each carrying a per-(capability,
 * solution) MEMBERSHIP CLASS (`core` | `pack` | `extension` | `n-a`, D3).
 *
 * Harborline and Flight-Deck, together with the v0 reference edition, are
 * editions: compositions over the one Capability shell. Membership is per-solution
 * metadata, NOT baked into the capability (D2). The resolution pipeline (D7)
 * reads this to decide whether a capability is even in the active solution.
 */

import type { CapabilityId } from '@harborline-software/api-contracts'

/** The membership class of a (capability, solution) pair (ADR 0125 D3). */
export type MembershipClass = 'core' | 'pack' | 'extension' | 'n-a'

/** One capability's membership within a composition. */
export interface CompositionEntry {
  /** The capability. */
  capabilityId: CapabilityId
  /** Its essentiality to this solution (D3). */
  membership: MembershipClass
}

/**
 * A composition manifest: an edition's curated capability set. The shell boots
 * one of these (ADR 0125 D8 — "Capability realizes this model"). v0 ships the
 * reference edition's manifest (see `editions/`).
 */
export interface CompositionManifest {
  /** The edition/solution id (e.g. `harborline`, `flight-deck`). */
  solutionId: string
  /** Human-readable edition name. */
  name: string
  /** The capabilities in this composition, each with its membership class. */
  capabilities: CompositionEntry[]
}

/** Look up a capability's membership in a composition (`n-a` if absent). */
export function membershipOf(
  manifest: CompositionManifest,
  capabilityId: CapabilityId,
): MembershipClass {
  const entry = manifest.capabilities.find((c) => c.capabilityId === capabilityId)
  return entry?.membership ?? 'n-a'
}
