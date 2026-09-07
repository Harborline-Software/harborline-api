/**
 * Pack-DAG resolver tests (ADR 0129 D3/D3.1/D4/D5/D7/N3/N7).
 *
 * Proves the BUILD layer (closure + cycle / version / collision / cascade) and
 * the DEMAND-layer projection on synthetic catalogs (and the reference edition's real
 * catalog), asserting each `CompositionError` reason fires fail-closed and the
 * provenance is correct.
 */

import { describe, it, expect, beforeAll } from 'vitest'

import type { PackManifest } from '@harborline-software/api-contracts'

import {
  resolveEdition,
  projectForTenant,
  CompositionError,
  type ResolvedEdition,
} from './pack-resolver.js'
import { REFERENCE_APP_CATALOG, REFERENCE_APP_SEED } from '../catalog/harborline-catalog.js'

// ---------------------------------------------------------------------------
// Test catalog builders
// ---------------------------------------------------------------------------

const kernel: PackManifest = {
  name: 'kernel',
  version: '1.0.0',
  scopeTier: 'kernel',
  provides: [{ capabilityId: 'audit', realization: 'domain-block' }],
  composesOver: [],
}

const platform: PackManifest = {
  name: 'capability-runtime',
  version: '1.0.0',
  scopeTier: 'horizontal',
  flavor: 'platform-service',
  provides: [{ capabilityId: 'inference', realization: 'capability-plugin' }],
  composesOver: [{ name: 'kernel', versionConstraint: '^1.0.0' }],
}

const tts: PackManifest = {
  name: 'tts',
  version: '1.0.0',
  scopeTier: 'horizontal',
  flavor: 'business-domain',
  provides: [{ capabilityId: 'tts', realization: 'capability-plugin' }],
  composesOver: [{ name: 'capability-runtime', versionConstraint: '^1.0.0' }],
}

describe('PackResolver — DAG closure + auto-pull (ADR 0129 D3)', () => {
  it('seed [tts] auto-pulls the transitive closure {tts, capability-runtime, kernel}', () => {
    const resolved = resolveEdition([kernel, platform, tts], ['tts'])
    expect(new Set(resolved.packs)).toEqual(new Set(['tts', 'capability-runtime', 'kernel']))
  })

  it('the real Harborline catalog: seed [tts] resolves the three-pack closure', () => {
    const resolved = resolveEdition(REFERENCE_APP_CATALOG, REFERENCE_APP_SEED, { solutionId: 'harborline' })
    expect(new Set(resolved.packs)).toEqual(new Set(['tts', 'capability-runtime', 'kernel']))
    // The emitted composition carries the resolved capabilities (the existing seam).
    const capIds = resolved.composition.capabilities.map((c) => c.capabilityId).sort()
    expect(capIds).toEqual(['audit', 'image', 'inference', 'tts'].sort())
    // every resolved capability is `core` to the edition
    expect(resolved.composition.capabilities.every((c) => c.membership === 'core')).toBe(true)
  })

  it('a composes-over ref absent from the catalog → missing-dependency', () => {
    const orphan: PackManifest = {
      name: 'orphan',
      version: '1.0.0',
      scopeTier: 'vertical',
      provides: [{ capabilityId: 'x', realization: 'capability-plugin' }],
      composesOver: [{ name: 'nonexistent', versionConstraint: '^1.0.0' }],
    }
    expect(() => resolveEdition([orphan], ['orphan'])).toThrowError(CompositionError)
    try {
      resolveEdition([orphan], ['orphan'])
    } catch (e) {
      expect((e as CompositionError).reason).toBe('missing-dependency')
    }
  })
})

describe('PackResolver — cycle detection (ADR 0129 N7)', () => {
  it('a composes-over cycle → CompositionError(dependency-cycle)', () => {
    const a: PackManifest = {
      name: 'a',
      version: '1.0.0',
      scopeTier: 'vertical',
      provides: [{ capabilityId: 'ca', realization: 'capability-plugin' }],
      composesOver: [{ name: 'b', versionConstraint: '^1.0.0' }],
    }
    const b: PackManifest = {
      name: 'b',
      version: '1.0.0',
      scopeTier: 'vertical',
      provides: [{ capabilityId: 'cb', realization: 'capability-plugin' }],
      composesOver: [{ name: 'a', versionConstraint: '^1.0.0' }],
    }
    let err: CompositionError | undefined
    try {
      resolveEdition([a, b], ['a'])
    } catch (e) {
      err = e as CompositionError
    }
    expect(err).toBeInstanceOf(CompositionError)
    expect(err?.reason).toBe('dependency-cycle')
  })
})

describe('PackResolver — version satisfiability (ADR 0129 D6)', () => {
  it('a constraint the catalog version does not satisfy → version-unsatisfiable', () => {
    const needsV2: PackManifest = {
      ...tts,
      composesOver: [{ name: 'capability-runtime', versionConstraint: '^2.0.0' }],
    }
    let err: CompositionError | undefined
    try {
      resolveEdition([kernel, platform, needsV2], ['tts'])
    } catch (e) {
      err = e as CompositionError
    }
    expect(err?.reason).toBe('version-unsatisfiable')
  })

  it('a diamond requiring two incompatible majors of one pack → version-unsatisfiable', () => {
    // Two packs both depend on `capability-runtime` but at incompatible majors;
    // with one catalog version, one constraint cannot be satisfied.
    const consumerA: PackManifest = {
      name: 'consumer-a',
      version: '1.0.0',
      scopeTier: 'vertical',
      provides: [{ capabilityId: 'a', realization: 'capability-plugin' }],
      composesOver: [{ name: 'capability-runtime', versionConstraint: '^1.0.0' }],
    }
    const consumerB: PackManifest = {
      name: 'consumer-b',
      version: '1.0.0',
      scopeTier: 'vertical',
      provides: [{ capabilityId: 'b', realization: 'capability-plugin' }],
      composesOver: [{ name: 'capability-runtime', versionConstraint: '^2.0.0' }],
    }
    const top: PackManifest = {
      name: 'top',
      version: '1.0.0',
      scopeTier: 'vertical',
      provides: [{ capabilityId: 'top', realization: 'capability-plugin' }],
      composesOver: [
        { name: 'consumer-a', versionConstraint: '^1.0.0' },
        { name: 'consumer-b', versionConstraint: '^1.0.0' },
      ],
    }
    let err: CompositionError | undefined
    try {
      resolveEdition([kernel, platform, consumerA, consumerB, top], ['top'])
    } catch (e) {
      err = e as CompositionError
    }
    expect(err?.reason).toBe('version-unsatisfiable')
  })

  it('caret/tilde/range constraints satisfy as expected', () => {
    const tilde: PackManifest = {
      ...tts,
      composesOver: [{ name: 'capability-runtime', versionConstraint: '~1.0.0' }],
    }
    expect(() => resolveEdition([kernel, platform, tilde], ['tts'])).not.toThrow()
    const range: PackManifest = {
      ...tts,
      composesOver: [{ name: 'capability-runtime', versionConstraint: '>=1.0.0 <2.0.0' }],
    }
    expect(() => resolveEdition([kernel, platform, range], ['tts'])).not.toThrow()
  })
})

describe('PackResolver — tier-1 collision (ADR 0129 D4)', () => {
  it('one domain-block capability provided by two packs → tier1-collision', () => {
    const ledgerA: PackManifest = {
      name: 'ledger-a',
      version: '1.0.0',
      scopeTier: 'horizontal',
      flavor: 'business-domain',
      provides: [{ capabilityId: 'general-ledger', realization: 'domain-block' }],
      composesOver: [{ name: 'kernel', versionConstraint: '^1.0.0' }],
    }
    const ledgerB: PackManifest = {
      name: 'ledger-b',
      version: '1.0.0',
      scopeTier: 'vertical',
      provides: [{ capabilityId: 'general-ledger', realization: 'domain-block' }],
      composesOver: [{ name: 'ledger-a', versionConstraint: '^1.0.0' }],
    }
    let err: CompositionError | undefined
    try {
      resolveEdition([kernel, ledgerA, ledgerB], ['ledger-b'])
    } catch (e) {
      err = e as CompositionError
    }
    expect(err?.reason).toBe('tier1-collision')
    expect(err?.detail).toBe('general-ledger')
  })

  it('the SAME capability provided by two packs at tier-2/3 is allowed (no collision)', () => {
    const pluginA: PackManifest = {
      name: 'plugin-a',
      version: '1.0.0',
      scopeTier: 'horizontal',
      flavor: 'platform-service',
      provides: [{ capabilityId: 'inference', realization: 'capability-plugin' }],
      composesOver: [{ name: 'kernel', versionConstraint: '^1.0.0' }],
    }
    const pluginB: PackManifest = {
      name: 'plugin-b',
      version: '1.0.0',
      scopeTier: 'vertical',
      provides: [{ capabilityId: 'inference', realization: 'capability-plugin' }],
      composesOver: [{ name: 'plugin-a', versionConstraint: '^1.0.0' }],
    }
    expect(() => resolveEdition([kernel, pluginA, pluginB], ['plugin-b'])).not.toThrow()
  })
})

describe('PackResolver — AP override keyed-merge cascade (ADR 0129 D5/N3)', () => {
  // A base business-horizontal CoA + a vertical that ADDS an account.
  const accountingCore: PackManifest = {
    name: 'accounting-core',
    version: '1.0.0',
    scopeTier: 'horizontal',
    flavor: 'business-domain',
    provides: [{ capabilityId: 'general-ledger', realization: 'domain-block' }],
    composesOver: [{ name: 'kernel', versionConstraint: '^1.0.0' }],
    defaults: {
      chartOfAccounts: [
        { code: '1000', name: 'Cash' },
        { code: '2000', name: 'Accounts Payable' },
      ],
    },
    defaultMergeKeys: { chartOfAccounts: 'code' },
  }
  const propertyVertical: PackManifest = {
    name: 'accounting-property',
    version: '1.0.0',
    scopeTier: 'vertical',
    provides: [{ capabilityId: 'ar-aging', realization: 'capability-plugin' }],
    composesOver: [{ name: 'accounting-core', versionConstraint: '^1.0.0' }],
    defaults: {
      chartOfAccounts: [{ code: '2300', name: 'Security Deposits' }],
    },
    defaultMergeKeys: { chartOfAccounts: 'code' },
  }

  it('base CoA entries survive + the vertical ADDS its entry (keyed-merge, not array-replace)', () => {
    const resolved = resolveEdition(
      [kernel, accountingCore, propertyVertical],
      ['accounting-property'],
    )
    const coa = resolved.resolvedDefaults['chartOfAccounts'] as Array<{ code: string; name: string }>
    const codes = coa.map((e) => e.code).sort()
    // base 1000/2000 SURVIVE + the vertical's 2300 is ADDED (RFC-7396 would have
    // discarded the base — N3 keyed-merge keeps it)
    expect(codes).toEqual(['1000', '2000', '2300'])
  })

  it('a vertical entry OVERRIDES a base entry by key (cross-tier override wins by specificity)', () => {
    const overrider: PackManifest = {
      ...propertyVertical,
      defaults: { chartOfAccounts: [{ code: '1000', name: 'Operating Cash' }] },
    }
    const resolved = resolveEdition([kernel, accountingCore, overrider], ['accounting-property'])
    const coa = resolved.resolvedDefaults['chartOfAccounts'] as Array<{ code: string; name: string }>
    const cash = coa.find((e) => e.code === '1000')
    expect(cash?.name).toBe('Operating Cash') // the vertical (higher specificity) won
  })

  it('two SAME-TIER verticals defining account 2300 differently → same-tier-conflict', () => {
    const vertA: PackManifest = {
      name: 'vertical-a',
      version: '1.0.0',
      scopeTier: 'vertical',
      provides: [{ capabilityId: 'va', realization: 'capability-plugin' }],
      composesOver: [{ name: 'accounting-core', versionConstraint: '^1.0.0' }],
      defaults: { chartOfAccounts: [{ code: '2300', name: 'Security Deposits' }] },
      defaultMergeKeys: { chartOfAccounts: 'code' },
    }
    const vertB: PackManifest = {
      name: 'vertical-b',
      version: '1.0.0',
      scopeTier: 'vertical',
      provides: [{ capabilityId: 'vb', realization: 'capability-plugin' }],
      composesOver: [
        { name: 'accounting-core', versionConstraint: '^1.0.0' },
        { name: 'vertical-a', versionConstraint: '^1.0.0' },
      ],
      defaults: { chartOfAccounts: [{ code: '2300', name: 'Damage Reserve' }] },
      defaultMergeKeys: { chartOfAccounts: 'code' },
    }
    let err: CompositionError | undefined
    try {
      resolveEdition([kernel, accountingCore, vertA, vertB], ['vertical-b'])
    } catch (e) {
      err = e as CompositionError
    }
    expect(err?.reason).toBe('same-tier-conflict')
    expect(err?.detail).toContain('2300')
  })

  it('two SAME-TIER packs setting a SCALAR default differently → same-tier-conflict', () => {
    const a: PackManifest = {
      name: 'svc-a',
      version: '1.0.0',
      scopeTier: 'horizontal',
      flavor: 'platform-service',
      provides: [{ capabilityId: 'sa', realization: 'capability-plugin' }],
      composesOver: [{ name: 'kernel', versionConstraint: '^1.0.0' }],
      defaults: { timeoutMs: 30_000 },
    }
    const b: PackManifest = {
      name: 'svc-b',
      version: '1.0.0',
      scopeTier: 'horizontal',
      flavor: 'platform-service',
      provides: [{ capabilityId: 'sb', realization: 'capability-plugin' }],
      composesOver: [
        { name: 'kernel', versionConstraint: '^1.0.0' },
        { name: 'svc-a', versionConstraint: '^1.0.0' },
      ],
      defaults: { timeoutMs: 60_000 },
    }
    let err: CompositionError | undefined
    try {
      resolveEdition([kernel, a, b], ['svc-b'])
    } catch (e) {
      err = e as CompositionError
    }
    expect(err?.reason).toBe('same-tier-conflict')
    expect(err?.detail).toBe('timeoutMs')
  })

  it('IDENTICAL same-tier values are NOT a conflict (only differing values error)', () => {
    const a: PackManifest = {
      name: 'svc-a',
      version: '1.0.0',
      scopeTier: 'horizontal',
      flavor: 'platform-service',
      provides: [{ capabilityId: 'sa', realization: 'capability-plugin' }],
      composesOver: [{ name: 'kernel', versionConstraint: '^1.0.0' }],
      defaults: { timeoutMs: 30_000 },
    }
    const b: PackManifest = {
      name: 'svc-b',
      version: '1.0.0',
      scopeTier: 'horizontal',
      flavor: 'platform-service',
      provides: [{ capabilityId: 'sb', realization: 'capability-plugin' }],
      composesOver: [
        { name: 'kernel', versionConstraint: '^1.0.0' },
        { name: 'svc-a', versionConstraint: '^1.0.0' },
      ],
      defaults: { timeoutMs: 30_000 },
    }
    expect(() => resolveEdition([kernel, a, b], ['svc-b'])).not.toThrow()
  })
})

describe('PackResolver — provenance correctness (ADR 0129 D8)', () => {
  it('each resolved capability maps to the right providing pack', () => {
    const resolved = resolveEdition(REFERENCE_APP_CATALOG, REFERENCE_APP_SEED)
    const provOf = (capId: string) =>
      resolved.provenance.capabilities.find((c) => c.capabilityId === capId)
    expect(provOf('audit')?.providedBy).toBe('kernel')
    expect(provOf('audit')?.realization).toBe('domain-block')
    expect(provOf('inference')?.providedBy).toBe('capability-runtime')
    expect(provOf('tts')?.providedBy).toBe('tts')
    expect(provOf('tts')?.realization).toBe('capability-plugin')
  })

  it('default provenance records the winning pack + what it overrode', () => {
    const base: PackManifest = {
      name: 'base',
      version: '1.0.0',
      scopeTier: 'horizontal',
      flavor: 'platform-service',
      provides: [{ capabilityId: 'b', realization: 'capability-plugin' }],
      composesOver: [{ name: 'kernel', versionConstraint: '^1.0.0' }],
      defaults: { voice: 'Alex' },
    }
    const over: PackManifest = {
      name: 'over',
      version: '1.0.0',
      scopeTier: 'vertical',
      provides: [{ capabilityId: 'o', realization: 'capability-plugin' }],
      composesOver: [{ name: 'base', versionConstraint: '^1.0.0' }],
      defaults: { voice: 'Samantha' },
    }
    const resolved = resolveEdition([kernel, base, over], ['over'])
    expect(resolved.resolvedDefaults['voice']).toBe('Samantha') // vertical won
    const voiceProv = resolved.provenance.defaults.find((d) => d.key === 'voice')
    expect(voiceProv?.providedBy).toBe('over')
    expect(voiceProv?.overrode).toContain('base')
  })
})

describe('PackResolver — per-tenant projection (ADR 0129 D3.1/N1, normal-path)', () => {
  let edition: ResolvedEdition
  beforeAll(() => {
    edition = resolveEdition(REFERENCE_APP_CATALOG, REFERENCE_APP_SEED)
  })

  it('a tier unlocking ALL packs yields the full composition', () => {
    const full = projectForTenant(edition, {
      unlockedPacks: ['kernel', 'capability-runtime', 'tts'],
    })
    expect(full.capabilities.map((c) => c.capabilityId).sort()).toEqual(
      ['audit', 'image', 'inference', 'tts'].sort(),
    )
  })

  it('a tier unlocking a SUBSET yields the subset (a tenant lacking a pack is normal-path)', () => {
    // Unlock only kernel + capability-runtime; the tenant's tier does NOT include tts.
    const subset = projectForTenant(edition, {
      unlockedPacks: ['kernel', 'capability-runtime'],
    })
    const capIds = subset.capabilities.map((c) => c.capabilityId).sort()
    // tts is dropped; audit/image/inference (from the unlocked packs) survive.
    expect(capIds).not.toContain('tts')
    expect(capIds).toEqual(['audit', 'image', 'inference'].sort())
  })

  it('a tier unlocking NO packs yields an empty composition (NOT a CompositionError)', () => {
    const empty = projectForTenant(edition, { unlockedPacks: [] })
    expect(empty.capabilities).toHaveLength(0)
    // the projection NEVER throws — the demand layer is normal-path
  })
})
