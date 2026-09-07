/**
 * The capability-resolution pipeline (ADR 0125 D7) — a deterministic,
 * short-circuiting pipeline that turns a capability into "available + bound to a
 * provider", in this DELIBERATE order (the order is load-bearing: membership
 * before entitlement so an unlicensed-but-core capability reads as `upsell`
 * while an `n-a` one never appears):
 *
 *   1. Membership      — is the capability in the active solution's composition?
 *   2. Entitlement     — is the tenant/edition licensed for it? (ADR 0009/0117)
 *   3. Hardware        — does the host profile support a provider? (ADR 0116)
 *   4. Provider-license— which supported providers pass the S4 gate? (ADR 0123)
 *   5. Configuration   — operator's choice among survivors + tier + flags.
 *
 * The output is the @harborline-software/api-contracts `ResolutionResult` with the FE-1 closed
 * `resolutionState` enum (the single source). v0 STUBS entitlement/hardware/
 * license to "available" — but the PIPELINE ORDER and the typed output are REAL,
 * and each stub is a named seam a later phase fills with ADR 0009/0116/0123 logic.
 */

import type {
  CapabilityId,
  DeviceCapabilityProfile,
  DeviceCapabilityTier,
  ProviderId,
  ResolutionResult,
  ResolutionState,
  SelectionReason,
  SpeedHint,
  UsageTier,
} from '@harborline-software/api-contracts'

import { admitsLocalAiTier } from '@harborline-software/api-contracts'

import {
  membershipOf,
  type CompositionManifest,
  type MembershipClass,
} from './composition.js'

/** A provider candidate the resolver chooses among (a thin v0 view of manifest-v2). */
export interface ProviderCandidate {
  providerId: ProviderId
  /** The tier the provider runs at (ADR 0116). */
  tier: UsageTier
  /** Whether this candidate is the bundleable local floor (D6). */
  isFloor: boolean
  /** Coarse speed expectation the UI surfaces. */
  speedHint: SpeedHint
  /** Declared local-AI capacity floor. Missing means unknown and fails closed. */
  minimumDeviceCapabilityTier?: DeviceCapabilityTier
}

/** Stage 2 — entitlement (ADR 0009/0117). Stubbed-available seam in v0. */
export interface EntitlementResolver {
  /** `true` = licensed; `false` = locked (→ `locked-entitlement`/`upsell`). */
  isEntitled(capabilityId: CapabilityId): boolean
}

/** Stage 3 — hardware fit (ADR 0116). Stubbed-available seam in v0. */
export interface HardwareResolver {
  /** Providers whose host-profile fit supports this hardware. Empty = no fit. */
  supportedProviders(capabilityId: CapabilityId): ProviderCandidate[]
}

/** Stage 4 — provider-license S4 gate (ADR 0123). Stubbed-pass seam in v0. */
export interface ProviderLicenseGate {
  /** `true` = the provider passes the S4 license AND-gate for the declared intent. */
  passes(provider: ProviderCandidate): boolean
}

/** Stage 5 — configuration: the operator's choice among survivors (default = auto-pick). */
export interface ConfigurationResolver {
  /**
   * Pick among the surviving providers (post-license-gate). Default v0 policy:
   * prefer a non-floor provider, else the floor. Returns the chosen candidate +
   * why (the closed `SelectionReason`). `null` = no survivor (caller maps to a
   * non-resolving state).
   */
  choose(
    capabilityId: CapabilityId,
    survivors: ProviderCandidate[],
  ): { candidate: ProviderCandidate; reason: SelectionReason } | null
}

/** The pipeline's wired stages (each a named seam; v0 supplies stub defaults). */
export interface ResolutionPipeline {
  composition: CompositionManifest
  entitlement: EntitlementResolver
  hardware: HardwareResolver
  license: ProviderLicenseGate
  configuration: ConfigurationResolver
}

/**
 * Run the D7 pipeline for one capability. SHORT-CIRCUITS at the first stage that
 * resolves a non-`available` state. Always returns a typed `ResolutionResult`
 * (FE-1) — the observable output of the WHOLE pipeline, not merely a provider id.
 */
export function resolveCapability(
  pipeline: ResolutionPipeline,
  capabilityId: CapabilityId,
): ResolutionResult {
  // --- Stage 1: Membership (D7.1) -----------------------------------------
  const membership: MembershipClass = membershipOf(pipeline.composition, capabilityId)
  if (membership === 'n-a') {
    return nonResolving(
      'not-in-edition',
      `capability '${capabilityId}' is not part of edition '${pipeline.composition.solutionId}'`,
    )
  }

  // --- Stage 2: Entitlement (D7.2) ----------------------------------------
  // Order matters: a core capability that is unlicensed reads as `upsell`
  // (worth unlocking) vs a pack/extension which reads as `locked-entitlement`.
  if (!pipeline.entitlement.isEntitled(capabilityId)) {
    const state: ResolutionState = membership === 'core' ? 'upsell' : 'locked-entitlement'
    return nonResolving(
      state,
      `capability '${capabilityId}' is not entitled for this edition (${membership})`,
    )
  }

  // --- Stage 3: Hardware (D7.3) -------------------------------------------
  const hwSupported = pipeline.hardware.supportedProviders(capabilityId)
  if (hwSupported.length === 0) {
    return nonResolving(
      'unavailable-hardware',
      `no provider for '${capabilityId}' fits the host hardware profile`,
    )
  }

  // --- Stage 4: Provider license (D7.4) -----------------------------------
  const survivors = hwSupported.filter((p) => pipeline.license.passes(p))
  if (survivors.length === 0) {
    // Hardware-fit providers exist but the S4 license gate blocks all of them.
    // `degraded` is wrong (it implies a fallback provider WAS chosen);
    // `unavailable-hardware` is wrong (hardware fit, not the issue). The license
    // gate is an entitlement-class block on the providers → `locked-entitlement`.
    return nonResolving(
      'locked-entitlement',
      `every provider for '${capabilityId}' is blocked by the provider-license gate`,
    )
  }

  // --- Stage 5: Configuration (D7.5) --------------------------------------
  const choice = pipeline.configuration.choose(capabilityId, survivors)
  if (choice === null) {
    return nonResolving(
      'locked-entitlement',
      `no configured provider chosen for '${capabilityId}'`,
    )
  }

  const { candidate, reason } = choice
  // `degraded` (ADR 0124 FE-1) = resolved, but to a fallback/SLOW floor at
  // resolution time (the "why the slow CPU image" banner) — distinct from runtime
  // throttling. It fires when the only usable provider is the slow local floor.
  // (If a faster provider survived, config would have picked it → `available`.)
  const isFallback = candidate.isFloor && candidate.speedHint === 'slow'
  const resolutionState: ResolutionState = isFallback ? 'degraded' : 'available'

  return {
    resolutionState,
    chosenProviderId: candidate.providerId,
    selectionReason: reason,
    isFallback,
    tier: candidate.tier,
    speedHint: candidate.speedHint,
    reason: isFallback
      ? `resolved '${capabilityId}' to the local floor '${candidate.providerId}' (a faster provider exists but did not win)`
      : `resolved '${capabilityId}' to '${candidate.providerId}'`,
  }
}

/** Build a non-resolving `ResolutionResult` (no chosen provider). */
function nonResolving(state: ResolutionState, reason: string): ResolutionResult {
  return {
    resolutionState: state,
    chosenProviderId: null,
    selectionReason: 'entitlement-gated',
    isFallback: false,
    tier: 'local',
    speedHint: 'moderate',
    reason,
  }
}

// --- v0 stub resolvers (named seams — "available", per the Phase-2 scope) -----

/** v0 entitlement stub: everything entitled (ADR 0009/0117 seam, filled later). */
export const stubEntitledAll: EntitlementResolver = {
  isEntitled: () => true,
}

/** v0 license-gate stub: every provider passes (ADR 0123 S4 seam, filled later). */
export const stubLicensePassAll: ProviderLicenseGate = {
  passes: () => true,
}

/**
 * v0 configuration default policy: prefer the fastest non-floor provider; if only
 * the floor survives, pick it (a fallback). Mirrors the D7.5 "default = auto-pick".
 */
export const defaultConfiguration: ConfigurationResolver = {
  choose(_capabilityId, survivors) {
    if (survivors.length === 0) return null
    const nonFloor = survivors.filter((p) => !p.isFloor)
    if (nonFloor.length > 0) {
      const best = pickFastest(nonFloor)
      const reason: SelectionReason =
        survivors.length === 1 ? 'only-candidate' : 'preferred-by-policy'
      return { candidate: best, reason }
    }
    const floor = pickFastest(survivors)
    return { candidate: floor, reason: 'fallback-floor' }
  },
}

/** A hardware resolver built from a static candidate map (v0 — fits everything). */
export function staticHardwareResolver(
  byCapability: Record<CapabilityId, ProviderCandidate[]>,
): HardwareResolver {
  return {
    supportedProviders: (capabilityId) => byCapability[capabilityId] ?? [],
  }
}

/**
 * ADR 0132 local-model admission over the host-owned capability profile.
 *
 * Only on-device candidates are filtered. Remote/cloud execution has its own
 * residency, governance, entitlement, budget, health, and availability gates;
 * the local device's memory must not exclude those candidates. A local model
 * without a declared floor is excluded rather than optimistically guessed.
 */
export function deviceCapabilityHardwareResolver(
  profile: DeviceCapabilityProfile,
  byCapability: Record<CapabilityId, ProviderCandidate[]>,
): HardwareResolver {
  return {
    supportedProviders: (capabilityId) =>
      (byCapability[capabilityId] ?? []).filter((candidate) => {
        if (candidate.tier !== 'local') return true
        if (candidate.minimumDeviceCapabilityTier === undefined) return false
        return admitsLocalAiTier(profile, candidate.minimumDeviceCapabilityTier)
      }),
  }
}

function pickFastest(candidates: ProviderCandidate[]): ProviderCandidate {
  const rank: Record<SpeedHint, number> = { fast: 0, moderate: 1, slow: 2 }
  return [...candidates].sort((a, b) => rank[a.speedHint] - rank[b.speedHint])[0]!
}
