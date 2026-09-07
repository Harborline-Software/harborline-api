/**
 * The tier-1 ONE-OWNER arch-test (ADR 0129 D4 / open-Q #4) — the generalization
 * of ADR 0111's "packs never add ledger primitives, you cannot have two GLs" to
 * EVERY tier-1 capability: across the catalog, no `capabilityId` is provided at
 * `realization: 'domain-block'` by more than one pack.
 *
 * This is an authoring-time invariant on the CATALOG (a namespace check, UPF-A3 /
 * open-Q #2), STRONGER than the per-edition collision check in `resolveEdition`
 * (D4): the resolver only catches a collision when both colliding packs are in
 * the SAME closure; this arch-test catches it across the WHOLE catalog, so a
 * collision surfaces in review even when the two packs are never selected
 * together. A new pack that claims an already-owned tier-1 capability fails CI.
 */

import { describe, it, expect } from 'vitest'

import type { PackManifest } from '@harborline-software/api-contracts'

import { REFERENCE_APP_CATALOG } from './harborline-catalog.js'

/**
 * Assert no tier-1 (`domain-block`) capability is owned by >1 pack across a
 * catalog. Returns the offending duplicates (empty = clean) so the assertion
 * message is actionable.
 */
function tier1Duplicates(catalog: PackManifest[]): Array<{ capabilityId: string; packs: string[] }> {
  const owners = new Map<string, string[]>()
  for (const pack of catalog) {
    for (const provided of pack.provides) {
      if (provided.realization !== 'domain-block') continue
      const list = owners.get(provided.capabilityId) ?? []
      list.push(pack.name)
      owners.set(provided.capabilityId, list)
    }
  }
  return [...owners.entries()]
    .filter(([, packs]) => packs.length > 1)
    .map(([capabilityId, packs]) => ({ capabilityId, packs }))
}

describe('tier-1 one-owner arch-test (ADR 0129 D4 — generalizes 0111 "no two GLs")', () => {
  it('the Harborline catalog has no tier-1 capability owned by more than one pack', () => {
    const dups = tier1Duplicates(REFERENCE_APP_CATALOG)
    expect(dups).toEqual([])
  })

  it('the invariant FIRES on a synthetic catalog with two packs claiming one domain-block', () => {
    const bad: PackManifest[] = [
      {
        name: 'pack-a',
        version: '1.0.0',
        scopeTier: 'horizontal',
        flavor: 'business-domain',
        provides: [{ capabilityId: 'general-ledger', realization: 'domain-block' }],
        composesOver: [],
      },
      {
        name: 'pack-b',
        version: '1.0.0',
        scopeTier: 'vertical',
        provides: [{ capabilityId: 'general-ledger', realization: 'domain-block' }],
        composesOver: [],
      },
    ]
    const dups = tier1Duplicates(bad)
    expect(dups).toEqual([{ capabilityId: 'general-ledger', packs: ['pack-a', 'pack-b'] }])
  })

  it('two packs sharing a tier-2/3 capability are NOT flagged (only domain-block is one-owner)', () => {
    const ok: PackManifest[] = [
      {
        name: 'pack-a',
        version: '1.0.0',
        scopeTier: 'horizontal',
        flavor: 'platform-service',
        provides: [{ capabilityId: 'inference', realization: 'capability-plugin' }],
        composesOver: [],
      },
      {
        name: 'pack-b',
        version: '1.0.0',
        scopeTier: 'vertical',
        provides: [{ capabilityId: 'inference', realization: 'category-provider' }],
        composesOver: [],
      },
    ]
    expect(tier1Duplicates(ok)).toEqual([])
  })
})

/**
 * Flavor-ordering arch-test (ADR 0129 D2 / UPF-A2) — no PLATFORM-SERVICE (1a)
 * pack may `composesOver` a BUSINESS-DOMAIN (1b) pack: that would invert the
 * flavor ordering (1a is meant to be LOW in the DAG, 1b composes over 1a) and
 * make the flavor tag meaningless. The flavor earns its keep precisely BY
 * carrying this enforced invariant (the independent-UPF affirmed this against
 * its own skepticism).
 */
function flavorOrderingViolations(
  catalog: PackManifest[],
): Array<{ pack: string; dependsOn: string }> {
  const byName = new Map(catalog.map((p) => [p.name, p]))
  const violations: Array<{ pack: string; dependsOn: string }> = []
  for (const pack of catalog) {
    if (pack.scopeTier !== 'horizontal' || pack.flavor !== 'platform-service') continue
    for (const dep of pack.composesOver) {
      const target = byName.get(dep.name)
      if (target?.scopeTier === 'horizontal' && target.flavor === 'business-domain') {
        violations.push({ pack: pack.name, dependsOn: dep.name })
      }
    }
  }
  return violations
}

describe('flavor-ordering arch-test (ADR 0129 D2/UPF-A2 — no 1a composes-over 1b)', () => {
  it('the Harborline catalog respects the 1a→1b flavor ordering', () => {
    expect(flavorOrderingViolations(REFERENCE_APP_CATALOG)).toEqual([])
  })

  it('the invariant FIRES when a platform-service pack composes over a business-domain pack', () => {
    const bad: PackManifest[] = [
      {
        name: 'accounting-core',
        version: '1.0.0',
        scopeTier: 'horizontal',
        flavor: 'business-domain',
        provides: [{ capabilityId: 'gl', realization: 'domain-block' }],
        composesOver: [],
      },
      {
        name: 'import-etl',
        version: '1.0.0',
        scopeTier: 'horizontal',
        flavor: 'platform-service',
        provides: [{ capabilityId: 'etl', realization: 'capability-plugin' }],
        // INVERTED: a 1a platform-service depending on a 1b business-domain.
        composesOver: [{ name: 'accounting-core', versionConstraint: '^1.0.0' }],
      },
    ]
    expect(flavorOrderingViolations(bad)).toEqual([
      { pack: 'import-etl', dependsOn: 'accounting-core' },
    ])
  })
})
