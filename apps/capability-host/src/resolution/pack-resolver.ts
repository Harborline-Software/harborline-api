/**
 * The pack-DAG resolver (ADR 0129 D7 BUILD layer) — the composition layer ABOVE
 * the flat `CompositionManifest`. Takes a pack CATALOG + an edition SEED and
 * deterministically resolves to a `ResolvedEdition`:
 *
 *   PackManifest[] + seed
 *     → transitive composes-over closure   (cycle-detect → CompositionError, N7)
 *     → version-satisfiability             (one version per pack, D6)
 *     → tier-1 collision check             (domain-block unique per edition, D4)
 *     → AP override cascade                (specificity + keyed-merge, D5/N3)
 *     → emit CompositionManifest + ProvenanceMap   (D8)
 *
 * PURELY ADDITIVE: it OUTPUTS the existing `CompositionManifest` seam — the
 * membrane / sandbox / `ResolutionPipeline` are untouched (they consume the
 * emitted manifest exactly as before). This is the COMPOSE-TIME dev-diagnostic
 * surface, DISTINCT from the runtime `ResolutionState` enum (FE-1): a
 * `CompositionError` is a fleet/dev-facing build error (D8 "errors vs choices"),
 * never a customer-facing runtime state.
 *
 * The DEMAND layer (ADR 0129 D3.1 / N1) is `projectForTenant` below: a per-tenant
 * normal-path projection `composition ∩ tier.unlockedPacks` — a tenant lacking a
 * pack is the everyday SaaS case, NOT a CompositionError.
 *
 * No semver dependency (the package self-builds standalone, X-1) — a minimal
 * caret/range satisfier covers the v1 catalog (one version per pack name).
 */

import type { PackManifest, ProvidedCapability, ScopeTier } from '@harborline-software/api-contracts'

import type { CompositionEntry, CompositionManifest } from './composition.js'

// ---------------------------------------------------------------------------
// CompositionError — the closed compose-time diagnostic taxonomy (D8)
// ---------------------------------------------------------------------------

/**
 * The closed set of compose-time failure reasons (ADR 0129 D7/D8). DISTINCT from
 * the runtime `ResolutionState` (FE-1) — these are fleet/dev-facing build
 * errors, never surfaced to a customer:
 *  - `dependency-cycle`     — a `composesOver` cycle in the authored DAG (N7)
 *  - `version-unsatisfiable`— a `versionConstraint` no catalog version satisfies, or a diamond (D6)
 *  - `tier1-collision`      — a `domain-block` capability provided by >1 pack (D4)
 *  - `same-tier-conflict`   — two same-tier packs override the same AP default/key differently (D5)
 *  - `missing-dependency`   — a `composesOver` ref names a pack absent from the catalog
 */
export type CompositionErrorReason =
  | 'dependency-cycle'
  | 'version-unsatisfiable'
  | 'tier1-collision'
  | 'same-tier-conflict'
  | 'missing-dependency'

/**
 * A typed compose-time error (ADR 0129 N7/D8). Carries the closed `reason` (the
 * branch key) + a human-readable message + the packs/capabilities involved (for
 * the `capability compose --explain` diagnostic). Fail-closed: the resolver THROWS
 * this — composition never silently produces a degraded result.
 */
export class CompositionError extends Error {
  /** The closed reason-class (the diagnostic branch key, not the message). */
  readonly reason: CompositionErrorReason
  /** The pack names involved (for the dev diagnostic). */
  readonly packs: string[]
  /** The capability or default key involved, when applicable. */
  readonly detail?: string

  constructor(
    reason: CompositionErrorReason,
    message: string,
    opts?: { packs?: string[]; detail?: string },
  ) {
    super(message)
    this.name = 'CompositionError'
    this.reason = reason
    this.packs = opts?.packs ?? []
    this.detail = opts?.detail
    // Restore the prototype chain (TS target ES2020 + extends Error).
    Object.setPrototypeOf(this, CompositionError.prototype)
  }
}

// ---------------------------------------------------------------------------
// Provenance (ADR 0129 D8) — "which pack provided it + what it overrode"
// ---------------------------------------------------------------------------

/** Provenance for one resolved capability: which pack provides it (D8). */
export interface CapabilityProvenance {
  capabilityId: string
  /** The pack that provides this capability. */
  providedBy: string
  /** The provider-realization tier it was provided at (D4). */
  realization: ProvidedCapability['realization']
}

/**
 * Provenance for one resolved default/config key (D8): which pack's value won
 * the cascade + which packs it overrode (the "computed styles" of composition).
 */
export interface DefaultProvenance {
  /** The default key (e.g. `voice`, `chartOfAccounts`). */
  key: string
  /** The pack whose value won the cascade. */
  providedBy: string
  /** Packs whose value for this key was overridden by the winner (lower specificity). */
  overrode: string[]
}

/**
 * The provenance map (ADR 0129 D8) — the shared primitive both the dev-diagnostic
 * and the admin-settings surfaces render. Maps each resolved capability + default
 * to the pack that provided it.
 */
export interface ProvenanceMap {
  /** Per-capability provenance (which pack provides each resolved capability). */
  capabilities: CapabilityProvenance[]
  /** Per-default provenance (which pack won the cascade for each default key). */
  defaults: DefaultProvenance[]
}

/**
 * The resolved edition (ADR 0129 D7 output) — the emitted `CompositionManifest`
 * (the existing membrane seam) + the provenance map (D8). The resolved
 * `defaults` are carried alongside the manifest as the merged AP config.
 */
export interface ResolvedEdition {
  /** The emitted composition manifest — the EXISTING seam, consumed unchanged. */
  composition: CompositionManifest
  /** The merged AP defaults/config after the D5 cascade. */
  resolvedDefaults: Record<string, unknown>
  /** The provenance map (D8). */
  provenance: ProvenanceMap
  /** The resolved pack names in the closure (the built-in pack set for D3.1). */
  packs: string[]
}

// ---------------------------------------------------------------------------
// D5 — override-cascade specificity ranking (vertical > 1b > 1a > kernel)
// ---------------------------------------------------------------------------

/**
 * The override-cascade specificity of a pack (ADR 0129 D5) — higher wins. The
 * 1a/1b flavor splits horizontals: `business-domain` (1b) is more specific than
 * `platform-service` (1a). Vertical is most specific; kernel least.
 */
function specificity(pack: PackManifest): number {
  switch (pack.scopeTier) {
    case 'vertical':
      return 3
    case 'horizontal':
      // business-domain (1b) outranks platform-service (1a) — D2 ordering.
      return pack.flavor === 'business-domain' ? 2 : 1
    case 'kernel':
      return 0
    default: {
      const _never: never = pack.scopeTier
      return _never
    }
  }
}

// ---------------------------------------------------------------------------
// Minimal semver satisfier (no dependency — X-1 standalone build)
// ---------------------------------------------------------------------------

interface SemVer {
  major: number
  minor: number
  patch: number
}

function parseSemVer(v: string): SemVer | null {
  const m = /^(\d+)\.(\d+)\.(\d+)$/.exec(v.trim())
  if (!m) return null
  return { major: Number(m[1]), minor: Number(m[2]), patch: Number(m[3]) }
}

/** Compare two semvers: <0 if a<b, 0 if equal, >0 if a>b. */
function cmp(a: SemVer, b: SemVer): number {
  if (a.major !== b.major) return a.major - b.major
  if (a.minor !== b.minor) return a.minor - b.minor
  return a.patch - b.patch
}

/**
 * Does `version` satisfy `constraint`? Supports the v1-needed forms: exact
 * (`1.2.3`), caret (`^1.2.3` — same major, ≥ the floor), tilde (`~1.2.3` — same
 * major+minor, ≥ the floor), and a `>=x.y.z <a.b.c` range. Anything else throws
 * `version-unsatisfiable` (an unrecognized constraint is fail-closed, not
 * silently true).
 */
function satisfies(version: string, constraint: string): boolean {
  const ver = parseSemVer(version)
  if (!ver) return false
  const c = constraint.trim()

  // Range: ">=x.y.z <a.b.c"
  const range = /^>=\s*(\d+\.\d+\.\d+)\s+<\s*(\d+\.\d+\.\d+)$/.exec(c)
  if (range) {
    const lo = parseSemVer(range[1]!)!
    const hi = parseSemVer(range[2]!)!
    return cmp(ver, lo) >= 0 && cmp(ver, hi) < 0
  }

  // Caret: "^1.2.3" — >= floor, < next major (for major>=1; v0.x is not in v1 catalog)
  if (c.startsWith('^')) {
    const floor = parseSemVer(c.slice(1))
    if (!floor) throw unsatisfiable(constraint)
    if (cmp(ver, floor) < 0) return false
    return ver.major === floor.major
  }

  // Tilde: "~1.2.3" — >= floor, < next minor
  if (c.startsWith('~')) {
    const floor = parseSemVer(c.slice(1))
    if (!floor) throw unsatisfiable(constraint)
    if (cmp(ver, floor) < 0) return false
    return ver.major === floor.major && ver.minor === floor.minor
  }

  // Exact
  const exact = parseSemVer(c)
  if (exact) return cmp(ver, exact) === 0

  // Unrecognized form — fail closed (D6: never a silent compose-time surprise).
  throw unsatisfiable(constraint)
}

function unsatisfiable(constraint: string): CompositionError {
  return new CompositionError(
    'version-unsatisfiable',
    `unrecognized or unparseable version constraint '${constraint}'`,
  )
}

// ---------------------------------------------------------------------------
// D7 — the resolution algorithm (deterministic, fail-closed)
// ---------------------------------------------------------------------------

/**
 * Resolve an edition from a pack catalog + a seed (ADR 0129 D7 BUILD layer).
 * Deterministic + compose-time fail-closed: any defect throws a typed
 * `CompositionError`. Returns the emitted `CompositionManifest` (the existing
 * seam) + provenance.
 *
 * @param catalog the pack set to resolve over (D2)
 * @param seed the selected pack names (D3); the closure is auto-pulled
 * @param opts.solutionId / opts.name override the emitted manifest's edition id/name
 */
export function resolveEdition(
  catalog: PackManifest[],
  seed: string[],
  opts?: { solutionId?: string; name?: string },
): ResolvedEdition {
  const byName = new Map<string, PackManifest>()
  for (const pack of catalog) {
    // A duplicate pack NAME in the catalog is a one-version-per-pack violation
    // (D6) — fail closed rather than silently last-writer.
    if (byName.has(pack.name)) {
      throw new CompositionError(
        'version-unsatisfiable',
        `pack '${pack.name}' appears more than once in the catalog (one version per pack — D6)`,
        { packs: [pack.name] },
      )
    }
    byName.set(pack.name, pack)
  }

  // --- Step 1: transitive composes-over closure + cycle detect (N7) --------
  const closure = computeClosure(byName, seed)

  // --- Step 2: version-satisfiability across the closure (D6) --------------
  checkVersions(byName, closure)

  // --- Step 3: tier-1 (domain-block) collision (D4) ------------------------
  const capabilityProvenance = checkTier1Collisions(closure)

  // --- Step 4: AP override cascade — specificity + keyed-merge (D5/N3) ------
  const { resolvedDefaults, defaultProvenance } = mergeDefaults(closure)

  // --- Step 5: emit the CompositionManifest (the existing seam) + provenance
  const capabilities: CompositionEntry[] = capabilityProvenance.map((p) => ({
    capabilityId: p.capabilityId,
    // Every resolved capability provided by a selected pack is `core` to the
    // edition (it is IN the composition). Membership grain finer than core/n-a
    // (pack/extension) is the per-tenant Layer-2 concern, not the build layer.
    membership: 'core',
  }))

  const composition: CompositionManifest = {
    solutionId: opts?.solutionId ?? 'carrier',
    name: opts?.name ?? 'Harborline (pack-resolved edition)',
    capabilities,
  }

  return {
    composition,
    resolvedDefaults,
    provenance: { capabilities: capabilityProvenance, defaults: defaultProvenance },
    packs: closure.map((p) => p.name),
  }
}

/**
 * Compute the transitive `composesOver` closure of a seed (ADR 0129 D3) with
 * cycle detection (N7). Returns the closure packs in a stable order (kernel-floor
 * first → most-specific last, via specificity then name — deterministic).
 * Throws `dependency-cycle` on an authored cycle, `missing-dependency` on a
 * `composesOver` ref absent from the catalog.
 */
function computeClosure(byName: Map<string, PackManifest>, seed: string[]): PackManifest[] {
  const resolved = new Map<string, PackManifest>()
  // DFS colors: 'visiting' = on the current stack (a back-edge to it = cycle).
  const state = new Map<string, 'visiting' | 'done'>()

  const visit = (name: string, path: string[]): void => {
    const color = state.get(name)
    if (color === 'done') return
    if (color === 'visiting') {
      const cyclePath = [...path.slice(path.indexOf(name)), name]
      throw new CompositionError(
        'dependency-cycle',
        `composes-over cycle detected: ${cyclePath.join(' → ')}`,
        { packs: cyclePath },
      )
    }
    const pack = byName.get(name)
    if (!pack) {
      throw new CompositionError(
        'missing-dependency',
        `pack '${name}' is referenced (seed or composes-over) but absent from the catalog`,
        { packs: [name] },
      )
    }
    state.set(name, 'visiting')
    for (const dep of pack.composesOver) {
      visit(dep.name, [...path, name])
    }
    state.set(name, 'done')
    resolved.set(name, pack)
  }

  for (const name of seed) visit(name, [])

  // Stable deterministic order: ascending specificity (kernel first), then name.
  return [...resolved.values()].sort(
    (a, b) => specificity(a) - specificity(b) || a.name.localeCompare(b.name),
  )
}

/**
 * Check every `composesOver` constraint across the closure is satisfiable
 * against the resolved (single) version of each pack (ADR 0129 D6). With one
 * version per pack name in the catalog, a diamond requiring an incompatible
 * version surfaces here as an unsatisfied constraint.
 */
function checkVersions(byName: Map<string, PackManifest>, closure: PackManifest[]): void {
  for (const pack of closure) {
    for (const dep of pack.composesOver) {
      const target = byName.get(dep.name)
      // (missing-dependency already caught in the closure walk; defensive here.)
      if (!target) continue
      if (!satisfies(target.version, dep.versionConstraint)) {
        throw new CompositionError(
          'version-unsatisfiable',
          `pack '${pack.name}' requires '${dep.name}' ${dep.versionConstraint}, ` +
            `but the catalog has '${dep.name}' ${target.version}`,
          { packs: [pack.name, dep.name], detail: dep.versionConstraint },
        )
      }
    }
  }
}

/**
 * Tier-1 collision check (ADR 0129 D4) — each `capabilityId` provided at
 * `realization: 'domain-block'` must be owned by EXACTLY ONE pack in the
 * closure. Two packs claiming it is a `tier1-collision` (the generalization of
 * 0111's "no two GLs"). Returns per-capability provenance (every provided
 * capability, tier-1 or not) for D8.
 */
function checkTier1Collisions(closure: PackManifest[]): CapabilityProvenance[] {
  const domainBlockOwner = new Map<string, string>()
  const provenance: CapabilityProvenance[] = []
  // Track which (capability, pack) pairs we have already recorded, so a single
  // capability provided by tier-2/3 by multiple packs is recorded once per pack
  // but not double-counted.
  for (const pack of closure) {
    for (const provided of pack.provides) {
      if (provided.realization === 'domain-block') {
        const existing = domainBlockOwner.get(provided.capabilityId)
        if (existing && existing !== pack.name) {
          throw new CompositionError(
            'tier1-collision',
            `tier-1 capability '${provided.capabilityId}' (domain-block) is provided by ` +
              `more than one pack: '${existing}' and '${pack.name}' — a tier-1 capability ` +
              `must have exactly one owner per edition (D4)`,
            { packs: [existing, pack.name], detail: provided.capabilityId },
          )
        }
        domainBlockOwner.set(provided.capabilityId, pack.name)
      }
      provenance.push({
        capabilityId: provided.capabilityId,
        providedBy: pack.name,
        realization: provided.realization,
      })
    }
  }
  return provenance
}

/**
 * The AP override cascade (ADR 0129 D5/N3). Merges `defaults` across the closure
 * by SPECIFICITY (`vertical > business-horizontal > platform-service > kernel`),
 * most-specific wins. COLLECTION-valued defaults (arrays of objects with a
 * declared merge key) KEYED-MERGE: base entries survive, downstream packs add or
 * override entries by key. A SAME-TIER collision (two packs at the same
 * specificity overriding the same default key, or the same collection entry key)
 * → `same-tier-conflict` (never silent last-writer).
 *
 * Returns the merged defaults + per-key provenance (D8).
 */
function mergeDefaults(closure: PackManifest[]): {
  resolvedDefaults: Record<string, unknown>
  defaultProvenance: DefaultProvenance[]
} {
  // Group packs by specificity tier so we can detect SAME-TIER conflicts.
  const tiers = new Map<number, PackManifest[]>()
  for (const pack of closure) {
    const s = specificity(pack)
    const list = tiers.get(s) ?? []
    list.push(pack)
    tiers.set(s, list)
  }
  // Process tiers least-specific → most-specific so higher tiers override lower.
  const sortedTiers = [...tiers.keys()].sort((a, b) => a - b)

  const merged: Record<string, unknown> = {}
  // For each default key, which pack currently owns the winning value + the
  // packs it overrode (lower specificity).
  const owner = new Map<string, { pack: string; overrode: string[] }>()
  // For keyed collections: track which pack contributed each entry key, so a
  // same-tier entry-key collision is caught (N3 at entry grain).
  const collectionEntryOwner = new Map<string, Map<string, { pack: string; tier: number }>>()

  for (const tier of sortedTiers) {
    const packsInTier = tiers.get(tier)!
    // Within a tier, accumulate this tier's contributions FIRST (so we can
    // detect two same-tier packs conflicting BEFORE applying to `merged`).
    const tierScalarContrib = new Map<string, string>() // defaultKey → pack (first contributor this tier)

    for (const pack of packsInTier) {
      const defaults = pack.defaults
      if (!defaults) continue
      const mergeKeys = pack.defaultMergeKeys ?? {}

      for (const [key, value] of Object.entries(defaults)) {
        const declaredMergeKey = mergeKeys[key]
        const isKeyedCollection =
          declaredMergeKey !== undefined && Array.isArray(value)

        if (isKeyedCollection) {
          mergeKeyedCollection({
            key,
            mergeKey: declaredMergeKey,
            entries: value as Array<Record<string, unknown>>,
            pack: pack.name,
            tier,
            merged,
            collectionEntryOwner,
            owner,
          })
        } else {
          // Scalar / whole-value default (RFC-7396 last-writer at default grain).
          // SAME-TIER conflict: two packs at THIS specificity both set `key` to
          // DIFFERENT values → same-tier-conflict (the edition manifest must name
          // the winner — D5; v1 has no manifest override, so it is an error).
          const priorThisTier = tierScalarContrib.get(key)
          if (priorThisTier !== undefined && priorThisTier !== pack.name) {
            const a = merged[key]
            if (!deepEqual(a, value)) {
              throw new CompositionError(
                'same-tier-conflict',
                `packs '${priorThisTier}' and '${pack.name}' (same specificity tier) both ` +
                  `define default '${key}' with different values — the edition manifest must ` +
                  `name the winner (D5)`,
                { packs: [priorThisTier, pack.name], detail: key },
              )
            }
          }
          tierScalarContrib.set(key, pack.name)
          const prevOwner = owner.get(key)
          merged[key] = value
          owner.set(key, {
            pack: pack.name,
            overrode: prevOwner ? [...prevOwner.overrode, prevOwner.pack] : [],
          })
        }
      }
    }
  }

  const defaultProvenance: DefaultProvenance[] = [...owner.entries()].map(([key, v]) => ({
    key,
    providedBy: v.pack,
    overrode: v.overrode,
  }))

  return { resolvedDefaults: merged, defaultProvenance }
}

/**
 * Keyed-merge one collection-valued default (ADR 0129 N3). Base entries survive;
 * a downstream (higher-tier) pack ADDS new entries or OVERRIDES by key. A
 * SAME-TIER entry-key collision (two packs at the same specificity contributing
 * the same entry key) → `same-tier-conflict`.
 */
function mergeKeyedCollection(args: {
  key: string
  mergeKey: string
  entries: Array<Record<string, unknown>>
  pack: string
  tier: number
  merged: Record<string, unknown>
  collectionEntryOwner: Map<string, Map<string, { pack: string; tier: number }>>
  owner: Map<string, { pack: string; overrode: string[] }>
}): void {
  const { key, mergeKey, entries, pack, tier, merged, collectionEntryOwner, owner } = args

  // The accumulated entries for this collection, keyed by the merge key.
  const accum = (merged[key] as Array<Record<string, unknown>> | undefined) ?? []
  const byKey = new Map<string, Record<string, unknown>>()
  for (const e of accum) byKey.set(String(e[mergeKey]), e)

  let entryOwners = collectionEntryOwner.get(key)
  if (!entryOwners) {
    entryOwners = new Map()
    collectionEntryOwner.set(key, entryOwners)
  }

  for (const entry of entries) {
    const entryKey = String(entry[mergeKey])
    const prior = entryOwners.get(entryKey)
    if (prior && prior.tier === tier && prior.pack !== pack) {
      // Two SAME-TIER packs both define this entry key → not auto-resolvable.
      if (!deepEqual(byKey.get(entryKey), entry)) {
        throw new CompositionError(
          'same-tier-conflict',
          `packs '${prior.pack}' and '${pack}' (same specificity tier) both define ` +
            `entry '${entryKey}' in collection '${key}' with different values — the edition ` +
            `manifest must name the winner (D5/N3 at entry grain)`,
          { packs: [prior.pack, pack], detail: `${key}[${entryKey}]` },
        )
      }
    }
    // Add or override (higher tier wins by specificity — a cross-tier override).
    byKey.set(entryKey, entry)
    entryOwners.set(entryKey, { pack, tier })
  }

  merged[key] = [...byKey.values()]
  // Provenance for the collection as a whole: the latest contributing pack owns
  // it; record the prior owner as overridden.
  const prevOwner = owner.get(key)
  owner.set(key, {
    pack,
    overrode: prevOwner && prevOwner.pack !== pack ? [...prevOwner.overrode, prevOwner.pack] : prevOwner?.overrode ?? [],
  })
}

/** Structural equality for same-value same-tier default checks (small values). */
function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true
  if (typeof a !== typeof b) return false
  if (a === null || b === null) return a === b
  if (typeof a !== 'object') return false
  return JSON.stringify(a) === JSON.stringify(b)
}

// ---------------------------------------------------------------------------
// D3.1 / N1 — the DEMAND-layer per-tenant projection (normal-path)
// ---------------------------------------------------------------------------

/**
 * A tenant tier's runtime view (ADR 0129 D3.1 Layer-2). v1 stub for the single-tenant
 * reference edition; the REAL seam defers to ADR 0007/0009 (`tierMappings` /
 * `Foundation.FeatureManagement`). `unlockedPacks` = the built-in packs this
 * tenant's subscription unlocks.
 */
export interface TenantTier {
  /** The pack names this tenant's tier unlocks. */
  unlockedPacks: string[]
}

/**
 * Project a resolved edition for one tenant (ADR 0129 D3.1 / N1):
 * `effective(tenant) = edition.composition ∩ tier.unlockedPacks`.
 *
 * This is the DEMAND layer — **normal-path** runtime (NOT fail-closed): a tenant
 * lacking a pack is the everyday SaaS case, NOT a `CompositionError`. The
 * projection keeps only the capabilities whose providing pack is unlocked for
 * the tenant. Returns a per-tenant `CompositionManifest` the shell consumes.
 *
 * The projection is REAL even though the tier is stubbed: it intersects the
 * resolved capability provenance against `unlockedPacks`, so a tier unlocking a
 * SUBSET yields the subset (the build proves the seam, not just the type).
 */
export function projectForTenant(
  edition: ResolvedEdition,
  tier: TenantTier,
): CompositionManifest {
  const unlocked = new Set(tier.unlockedPacks)
  // A capability survives iff the pack that provides it is unlocked for the tenant.
  const allowedCapabilities = new Set(
    edition.provenance.capabilities
      .filter((c) => unlocked.has(c.providedBy))
      .map((c) => c.capabilityId),
  )

  const capabilities: CompositionEntry[] = edition.composition.capabilities.filter((c) =>
    allowedCapabilities.has(c.capabilityId),
  )

  return {
    solutionId: edition.composition.solutionId,
    name: edition.composition.name,
    capabilities,
  }
}

// Re-export the scope-tier specificity for tests / `--explain` tooling.
export { specificity as packSpecificity }
export type { ScopeTier }
