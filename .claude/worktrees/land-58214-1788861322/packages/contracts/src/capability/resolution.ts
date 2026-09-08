/**
 * The resolution-result contract + closed `resolutionState` enum
 * (ADR 0124 council FE-1; shared with ADR 0125 D7 / ADR 0123 §S6).
 *
 * The Resolve face's output: HOW the membrane chose a provider for a capability
 * and WHY, so the UI can explain "why the slow CPU image" / "why this bank
 * parser" / "why this is locked." The UI switches on the CLOSED `resolutionState`
 * enum; `reason` is the human display string, NOT the branch key (FE-1).
 *
 * SEC-6 (load-bearing): the resolution-result surface FORBIDS credential-bearing
 * fields. The types below are shaped so they cannot carry a secret — there is no
 * free-form `Record<string, unknown>` bag and no token/key/secret field. See the
 * `// SEC-6` note. A load-time manifest-validation test (membrane side) enforces
 * the same on the manifest; the type system enforces it here.
 */

import type { ProviderId } from './common.js'
import type { UsageTier } from './result.js'

/**
 * The CLOSED resolution-state enum the UI switches on (FE-1). Resolution-time
 * fallback-slow is separated from runtime throttled-slow:
 *  - `available`           — resolved to a usable provider, no caveat
 *  - `locked-entitlement`  — capability exists but the edition/entitlement gate blocks it
 *  - `upsell`              — not in this edition; an upgrade would unlock it
 *  - `unavailable-hardware`— no provider fits the host hardware profile (ADR 0116)
 *  - `degraded`            — resolved, but to a fallback/slow floor (resolution-time, not runtime)
 *  - `not-in-edition`      — the capability is not part of this edition's composition at all
 */
export type ResolutionState =
  | 'available'
  | 'locked-entitlement'
  | 'upsell'
  | 'unavailable-hardware'
  | 'degraded'
  | 'not-in-edition'

/** Why a provider was selected, as a closed reason-class (drives `speedHint`, not the human string). */
export type SelectionReason =
  | 'only-candidate'
  | 'preferred-by-policy'
  | 'hardware-fit'
  | 'fallback-floor'
  | 'entitlement-gated'

/** A coarse speed expectation the UI can surface (the slow-CPU-image banner). */
export type SpeedHint = 'fast' | 'moderate' | 'slow'

/**
 * The resolution-result contract (FE-1). Returned by the Resolve face.
 *
 * SEC-6: NO credential-bearing field. There is intentionally no `token`,
 * `secret`, `apiKey`, `connectionString`, or open `Record<string, unknown>`
 * here — the resolution surface cannot carry a secret by construction.
 */
export interface ResolutionResult {
  /** The closed state the UI switches on (FE-1). */
  resolutionState: ResolutionState
  /**
   * The provider chosen, when one was. Null when `resolutionState` is a
   * non-resolving state (`locked-entitlement`/`upsell`/`unavailable-hardware`/
   * `not-in-edition`).
   */
  chosenProviderId: ProviderId | null
  /** Why this provider was selected (closed reason-class). */
  selectionReason: SelectionReason
  /** True when the membrane fell back to a floor/slower provider (resolution-time). */
  isFallback: boolean
  /** The tier the chosen provider runs at (ADR 0116). */
  tier: UsageTier
  /** Coarse speed expectation for the chosen provider. */
  speedHint: SpeedHint
  /**
   * Human-readable explanation for display (FE-1: `reason` is the human string,
   * NOT the branch key). Safe to surface to the operator — carries no secret.
   */
  reason: string
}
