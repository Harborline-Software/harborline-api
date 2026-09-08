/**
 * The reference edition as a PACK CATALOG (ADR 0129 D2/D3) — the pack-DAG re-expression of the
 * hand-written `editions/harborline.ts` flat composition.
 *
 * This is the proving ground for the pack-resolver: a `kernel` floor pack, a
 * `capability-runtime` PLATFORM-SERVICE horizontal (1a — the AI/inference Capability
 * membrane Invoke surface, realized as a `capability-plugin`), and a `tts` pack
 * that `composesOver` capability-runtime. The reference edition SEED is `[tts]`:
 * selecting it auto-pulls the transitive closure `{tts, capability-runtime,
 * kernel}` (D3 "selecting a pack auto-pulls its `composes-over` closure").
 *
 * Re-expression, NOT duplication: `editions/harborline.ts` keeps its hand-authored
 * `CompositionManifest` (the Phase-2/3 membrane proof). This catalog proves the
 * resolver PRODUCES an equivalent composition from packs (`tts` + `image` as the
 * resolved capabilities; the resolver maps `provides` → composition `core`
 * entries). The two coexist; the resolver path is the additive ADR-0129 layer.
 *
 * Catalog placement is a point-in-time snapshot (ADR 0129 "the catalog is a
 * placement snapshot; council confirms cells" — the 0128 Lease-0052 lesson).
 */

import type { PackManifest } from '@harborline-software/api-contracts'

/**
 * Tier 0 — the KERNEL floor (ADR 0129 D2). Always-on; modeled as a pack so the
 * DAG has a concrete root the closure terminates at. Provides the kernel-tier
 * primitives (here: `audit` as a representative `domain-block` — concrete DI,
 * never swapped, one owner). Composes over nothing.
 */
export const KERNEL_PACK: PackManifest = {
  name: 'kernel',
  version: '1.0.0',
  scopeTier: 'kernel',
  provides: [
    // `audit` is a kernel domain-block (ADR 0111 "never add ledger/audit
    // primitives" — the tier-1 one-owner invariant the arch-test enforces).
    { capabilityId: 'audit', realization: 'domain-block' },
  ],
  composesOver: [],
}

/**
 * Tier 1a — the `capability-runtime` PLATFORM-SERVICE horizontal (ADR 0129 D2).
 * The AI/inference Capability membrane Invoke surface. Realized as a
 * `capability-plugin` (tier-3, runtime/config-swappable — multiple inference
 * providers allowed). Composes over the kernel floor.
 */
export const CAPABILITY_RUNTIME_PACK: PackManifest = {
  name: 'capability-runtime',
  version: '1.0.0',
  scopeTier: 'horizontal',
  flavor: 'platform-service',
  provides: [
    // The inference/runtime surface — capability-plugin (tier-3): runtime-swap.
    { capabilityId: 'inference', realization: 'capability-plugin' },
    // `image` is the Phase-2 reference-image capability composed off the runtime
    // — also a capability-plugin (the resolved-image runtime is swappable).
    { capabilityId: 'image', realization: 'capability-plugin' },
  ],
  composesOver: [{ name: 'kernel', versionConstraint: '^1.0.0' }],
  defaults: {
    // A platform-service default the cascade can be observed overriding (D5).
    runtimeTimeoutMs: 30_000,
  },
}

/**
 * The `tts` pack (ADR 0129 D2) — speech, composed over `capability-runtime`.
 * `tts` is a FLIGHT-DECK-DOMAIN capability that the reference edition composes off the shared
 * substrate (cross-edition reuse — the "editions over one shell" claim). Its
 * `tts` capability is a `capability-plugin` (the say/Piper runtimes swap behind
 * it). The reference edition's SEED is `[tts]`.
 */
export const TTS_PACK: PackManifest = {
  name: 'tts',
  version: '1.0.0',
  // `tts` is a domain capability composed off the shared runtime; in the v1
  // catalog it sits as a horizontal business-domain pack over the platform
  // service (it consumes capability-runtime, provides the tts capability).
  scopeTier: 'horizontal',
  flavor: 'business-domain',
  provides: [{ capabilityId: 'tts', realization: 'capability-plugin' }],
  composesOver: [{ name: 'capability-runtime', versionConstraint: '^1.0.0' }],
  defaults: {
    // The default voice the cascade resolves (an AP scalar default — D5).
    voice: 'Samantha',
  },
}

/**
 * The reference edition's pack catalog (ADR 0129 D2). The set of packs the resolver
 * searches; an edition SEED selects from it and the resolver auto-pulls the
 * transitive closure.
 */
export const REFERENCE_APP_CATALOG: PackManifest[] = [KERNEL_PACK, CAPABILITY_RUNTIME_PACK, TTS_PACK]

/**
 * The reference edition SEED (ADR 0129 D3). Selecting `tts` auto-pulls
 * `{tts, capability-runtime, kernel}`.
 */
export const REFERENCE_APP_SEED: string[] = ['tts']
