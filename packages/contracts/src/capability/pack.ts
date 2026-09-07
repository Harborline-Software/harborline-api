/**
 * Pack manifest — the canned-domain composition unit (ADR 0129 D1).
 *
 * A "canned domain" is a `pack` (ADR 0125 membership-class). This namespace is
 * the X-1 single source for the pack-DAG composition layer that sits ABOVE the
 * Capability membrane's flat `CompositionManifest` (ADR 0125 D2/D3). The resolver
 * (`apps/capability-host/src/resolution/pack-resolver.ts`) reads `PackManifest[]` + an
 * edition seed and EMITS a `CompositionManifest` (the existing seam, unchanged)
 * plus a provenance map — so this layer is purely additive.
 *
 * TYPES ONLY — no runtime behaviour. Framework-neutral (no React, no .NET, no
 * transport-library imports). Like the rest of the `capability` namespace these
 * are TS-canonical (ADR 0123 OQ-1): `@harborline-software/api-contracts` is the authority; the
 * .NET local-node + Python workers mirror these shapes.
 *
 * Why a pure-type pack manifest (not a class/registry): ADR 0129 is a
 * COMPOSITION DOCTRINE — it names the pack unit, the DAG edges, and the
 * deterministic conflict/override semantics. The registry mechanism + the
 * machine-readable manifest grammar are open-Q #1/#2, pinned at build, not here.
 * v1 ships the TYPES + the incremental resolver proven on the reference edition (UPF-A5,
 * "doctrine now, build incrementally").
 */

import type { CapabilityId } from './common.js'

/**
 * The provider-realization tier of a `provides` entry (ADR 0123 / 0125 →
 * ADR 0129 D4 collision resolution). This is the SAME three-tier slotting
 * vocabulary the fleet ratified (2026-05-25):
 *  - `domain-block`     — concrete DI, NEVER swapped. **Exactly one** provider
 *                         per capability per edition; two packs claiming it is a
 *                         compose-time ERROR (D4 / the tier-1 one-owner invariant).
 *  - `category-provider`— a bounded vendor swap; multiple providers allowed, the
 *                         resolution pipeline selects one (D4 tier-2).
 *  - `capability-plugin`— a runtime/config-swappable plugin; multiple allowed
 *                         (D4 tier-3).
 *
 * NOTE (ADR 0129 N4): a capability's realization tier can be COMPOSITION-RELATIVE
 * (auth is tier-2 in hosted multi-tenant but effectively tier-1 in single-user
 * self-hosted). v1 declares one tier per `ProvidedCapability`; a future revision
 * may make the tier a function of deployment mode. The D4 collision rule is
 * evaluated per-composition, so a composition-relative tier slots in later
 * without changing the resolver's contract.
 */
export type ProviderRealization = 'domain-block' | 'category-provider' | 'capability-plugin'

/**
 * The scope tier a pack sits at (ADR 0128 → ADR 0129 D2). `kernel` is the
 * always-on floor (not a selectable pack in its own right, but modeled as a pack
 * so the DAG has a concrete root the closure terminates at). `horizontal` packs
 * are shared by ≥2 verticals; `vertical` packs are one business area each.
 */
export type ScopeTier = 'kernel' | 'horizontal' | 'vertical'

/**
 * The flavor of a HORIZONTAL pack (ADR 0129 D2 1a/1b) — a flavor tag + DAG-depth
 * ordering, NOT a new tier (both are `horizontal` per ADR 0128):
 *  - `platform-service` (1a) — the meta-framework, infra-flavored, low in the DAG
 *    (identity, files, search, import-ETL, capability-runtime, ...).
 *  - `business-domain` (1b) — heavy CP; composes OVER platform services
 *    (accounting-core, payments, tax/compliance, parties/CRM).
 *
 * Catalog invariant (D2 / UPF-A2): no `platform-service` pack may `composesOver`
 * a `business-domain` pack — that would invert the flavor ordering. The
 * resolver's arch-test enforces it.
 *
 * Only meaningful on `horizontal` packs; absent on `kernel`/`vertical`.
 */
export type HorizontalFlavor = 'platform-service' | 'business-domain'

/**
 * One capability a pack OWNS/implements (ADR 0129 D1 `provides`), tagged with its
 * provider-realization tier (D4). `provides` = what the pack implements;
 * `composesOver` = what it consumes (UPF-A4). A pack must not `provide` a
 * capability already provided at tier-1 (`domain-block`) by another pack in its
 * closure (the D4 collision).
 */
export interface ProvidedCapability {
  /** The capability this pack provides (resolved on, never on a provider name — ADR 0123 §S1). */
  capabilityId: CapabilityId
  /** Its provider-realization tier — drives D4 collision resolution. */
  realization: ProviderRealization
}

/**
 * A DAG edge: a dependency on another pack by name + a version constraint
 * (ADR 0129 D3 `composes-over`). The version constraint is a semver range
 * expression (e.g. `^1.0.0`, `>=2.1.0 <3.0.0`); the resolver checks all
 * constraints across the closure are mutually satisfiable (D6).
 */
export interface PackRef {
  /** The depended-on pack's `name`. */
  name: string
  /** A semver range the resolved pack version must satisfy (D6). */
  versionConstraint: string
}

/**
 * A pack manifest — a canned domain as a first-class composable unit (ADR 0129
 * D1). A pack is a FULL-STACK unit (council-F3): `provides` may span React /
 * .NET / capability-runtime stacks, released in lockstep under one pack
 * `version` (D6 / N6). v1 models the DOCTRINE; the three-temporal-phase
 * packaging mechanics (build/bundle/runtime) are deferred to the build (A5).
 */
export interface PackManifest {
  /** The pack's stable name (the DAG node id + the `composesOver` target). */
  name: string
  /**
   * The pack's semver version. One version per pack per edition (D6) — a diamond
   * needing two majors of the same pack is a compose-time error.
   */
  version: string
  /** The scope tier this pack sits at (D2). */
  scopeTier: ScopeTier
  /**
   * The 1a/1b flavor — ONLY on `horizontal` packs (D2). Absent on
   * `kernel`/`vertical`. The resolver's flavor-ordering arch-test reads this.
   */
  flavor?: HorizontalFlavor
  /** The capabilities this pack owns/implements, each tagged with its tier (D1 / D4). */
  provides: ProvidedCapability[]
  /** The kernel + horizontal packs this pack depends on — the DAG edges (D3). */
  composesOver: PackRef[]
  /**
   * Overridable config / templates / terminology / defaults (D5 AP cascade). A
   * cascade merges these by specificity (`vertical > business-horizontal >
   * platform-service > kernel`). COLLECTION-valued defaults keyed-merge (N3): a
   * collection carries a declared stable key (see `defaultMergeKeys`) so a
   * downstream pack ADDS/overrides entries by key and base entries survive,
   * rather than RFC-7396 wholesale array-replacement. A same-tier key/default
   * collision is a compose-time error (not silent last-writer).
   */
  defaults?: Record<string, unknown>
  /**
   * Declares the stable merge key for each COLLECTION-valued entry in `defaults`
   * (ADR 0129 N3 — "the merge key is part of the collection's schema"). Maps a
   * `defaults` key whose value is an array of objects → the property on each
   * element that identifies it (e.g. `{ chartOfAccounts: 'code' }` for a CoA
   * keyed by account `code`). A `defaults` collection WITHOUT a declared key is
   * treated as a scalar/whole-value default (RFC-7396 last-writer at default
   * grain), not keyed-merged.
   */
  defaultMergeKeys?: Record<string, string>
  /**
   * Per-tenant entitlement gating (ADR 0129 D3.1 Layer-2). The `tiers` a tenant
   * subscription must include to UNLOCK this pack at runtime. This is the
   * COMMERCIAL/availability seam (defers to ADR 0007/0009) — NOT a security
   * boundary (D6: that is the S7 sandbox). A tenant lacking a pack is the
   * everyday SaaS case, not a composition error.
   */
  entitlement?: { tiers?: string[] }
}
