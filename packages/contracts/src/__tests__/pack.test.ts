/**
 * Contract test for the `PackManifest` namespace (ADR 0129 D1).
 *
 * Like the rest of the `capability` namespace these types are TS-CANONICAL
 * (ADR 0123 OQ-1) — no upstream fixture to round-trip against. The drift guard
 * is a STRUCTURAL contract test: it pins the closed enums (ProviderRealization,
 * ScopeTier, HorizontalFlavor) and the manifest field shape so a future edit
 * that widens an enum or drops a field FAILS at CI.
 *
 * The pack manifest is a PURE TYPE (no runtime behaviour); the resolver that
 * consumes it lives in `apps/capability-host`. This test pins the contract surface only.
 */

import { describe, it, expect } from 'vitest'

import type {
  PackManifest,
  ProvidedCapability,
  PackRef,
  ProviderRealization,
  ScopeTier,
  HorizontalFlavor,
} from '../capability.js'

describe('@harborline-software/api-contracts — pack manifest namespace (ADR 0129 D1)', () => {
  // -------------------------------------------------------------------------
  // Closed enums — provider-realization, scope-tier, flavor (D2/D4)
  // -------------------------------------------------------------------------
  describe('ProviderRealization is the closed three-tier slotting set (ADR 0123/0125 → D4)', () => {
    it('exactly domain-block|category-provider|capability-plugin', () => {
      const all: ProviderRealization[] = [
        'domain-block',
        'category-provider',
        'capability-plugin',
      ]
      const check = (r: ProviderRealization): string => {
        switch (r) {
          case 'domain-block':
          case 'category-provider':
          case 'capability-plugin':
            return r
          default: {
            const _never: never = r
            return _never
          }
        }
      }
      expect(all.map(check)).toHaveLength(3)
    })
  })

  describe('ScopeTier is the closed kernel|horizontal|vertical set (ADR 0128 → D2)', () => {
    it('exactly the three tiers', () => {
      const all: ScopeTier[] = ['kernel', 'horizontal', 'vertical']
      const check = (s: ScopeTier): string => {
        switch (s) {
          case 'kernel':
          case 'horizontal':
          case 'vertical':
            return s
          default: {
            const _never: never = s
            return _never
          }
        }
      }
      expect(all.map(check)).toHaveLength(3)
    })
  })

  describe('HorizontalFlavor is the closed platform-service|business-domain set (D2 1a/1b)', () => {
    it('exactly the two flavors', () => {
      const all: HorizontalFlavor[] = ['platform-service', 'business-domain']
      const check = (f: HorizontalFlavor): string => {
        switch (f) {
          case 'platform-service':
          case 'business-domain':
            return f
          default: {
            const _never: never = f
            return _never
          }
        }
      }
      expect(all.map(check)).toHaveLength(2)
    })
  })

  // -------------------------------------------------------------------------
  // D1 — the manifest field shape (provides owns / composesOver consumes)
  // -------------------------------------------------------------------------
  describe('D1 — PackManifest shape: provides (owns) vs composesOver (consumes)', () => {
    it('a kernel pack carries no flavor and composes over nothing', () => {
      const kernel = {
        name: 'kernel',
        version: '1.0.0',
        scopeTier: 'kernel',
        provides: [{ capabilityId: 'audit', realization: 'domain-block' }],
        composesOver: [],
      } satisfies PackManifest
      expect(kernel.scopeTier).toBe('kernel')
      expect(kernel.composesOver).toHaveLength(0)
      // a kernel pack has no flavor (flavor is horizontal-only)
      expect(Object.keys(kernel)).not.toContain('flavor')
    })

    it('a horizontal platform-service pack carries flavor + a PackRef edge to kernel', () => {
      const ref: PackRef = { name: 'kernel', versionConstraint: '^1.0.0' }
      const horizontal = {
        name: 'capability-runtime',
        version: '1.0.0',
        scopeTier: 'horizontal',
        flavor: 'platform-service',
        provides: [{ capabilityId: 'inference', realization: 'capability-plugin' }],
        composesOver: [ref],
      } satisfies PackManifest
      expect(horizontal.flavor).toBe('platform-service')
      expect(horizontal.composesOver[0]?.name).toBe('kernel')
      expect(horizontal.composesOver[0]?.versionConstraint).toBe('^1.0.0')
    })

    it('a vertical pack declares keyed-merge defaults (N3 — key is part of the schema)', () => {
      const vertical = {
        name: 'accounting-property',
        version: '1.0.0',
        scopeTier: 'vertical',
        provides: [{ capabilityId: 'ar-aging', realization: 'domain-block' }],
        composesOver: [{ name: 'accounting-core', versionConstraint: '^1.0.0' }],
        defaults: {
          chartOfAccounts: [{ code: '2300', name: 'Security Deposits' }],
        },
        defaultMergeKeys: { chartOfAccounts: 'code' },
        entitlement: { tiers: ['pro'] },
      } satisfies PackManifest
      // N3: the merge key for a collection-valued default is declared in the schema
      expect(vertical.defaultMergeKeys?.chartOfAccounts).toBe('code')
      expect(vertical.entitlement?.tiers).toEqual(['pro'])
    })

    it('ProvidedCapability tags each owned capability with its realization tier (D4)', () => {
      const provided: ProvidedCapability = {
        capabilityId: 'tts',
        realization: 'capability-plugin',
      }
      expect(provided.realization).toBe('capability-plugin')
      // the realization tier is what D4 collision resolution keys on
      const keys = Object.keys(provided).sort()
      expect(keys).toEqual(['capabilityId', 'realization'].sort())
    })
  })
})
