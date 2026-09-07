/**
 * Fixture-roundtrip test for BusinessCaseBundleManifest TypeScript mirror.
 *
 * Loads each of the 5 canonical *.bundle.json files from
 * foundation-catalog/Manifests/Bundles/ and verifies that they parse cleanly
 * to the BusinessCaseBundleManifest TypeScript interface.
 *
 * Purpose: drift detector between the C# canonical record and this TS mirror.
 * If the C# record gains a required field, the on-disk JSON fixture should also
 * gain it; if it doesn't, this test catches the divergence at CI time.
 *
 * Resolution: test paths from packages/contracts/src/__tests__/ to
 * packages/foundation-catalog/Manifests/Bundles/ are relative (../../...).
 */

import { describe, it, expect } from 'vitest'
import { readFileSync, readdirSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

import type {
  BusinessCaseBundleManifest,
  BundleCategory,
  BundleStatus,
  DeploymentMode,
  ProviderCategory,
} from '../bundles.js'

const BUNDLE_CATEGORIES: BundleCategory[] = ['Operations', 'Diligence', 'Finance', 'Platform']
const BUNDLE_STATUSES: BundleStatus[] = ['Draft', 'Preview', 'GA', 'Deprecated']
const DEPLOYMENT_MODES: DeploymentMode[] = ['Lite', 'SelfHosted', 'HostedSaaS']
const PROVIDER_CATEGORIES: ProviderCategory[] = [
  'Billing',
  'Payments',
  'BankingFeed',
  'FeatureFlags',
  'ChannelManager',
  'Messaging',
  'Storage',
  'IdentityProvider',
  'Other',
]

const SEMVER_PATTERN = /^\d+\.\d+\.\d+$/

// Resolve the canonical bundle fixtures directory relative to this test file.
// Path: packages/contracts/src/__tests__/ -> (3 ups) -> packages/ -> foundation-catalog/Manifests/Bundles/
const thisDir = fileURLToPath(new URL('.', import.meta.url))
const bundleManifestsDir = resolve(thisDir, '../../../foundation-catalog/Manifests/Bundles')

function loadBundleManifests(): Array<{ filename: string; manifest: BusinessCaseBundleManifest }> {
  const entries = readdirSync(bundleManifestsDir)
  const jsonFiles = entries.filter(f => f.endsWith('.bundle.json'))
  expect(jsonFiles.length).toBeGreaterThan(0) // guard: at least one fixture must exist

  return jsonFiles.map(filename => {
    const fullPath = join(bundleManifestsDir, filename)
    const raw = readFileSync(fullPath, 'utf-8')
    const manifest = JSON.parse(raw) as BusinessCaseBundleManifest
    return { filename, manifest }
  })
}

describe('@harborline-software/api-contracts — bundles namespace (ADR 0007 + A1)', () => {
  it('all 5 canonical bundle manifests are present in the fixture corpus', () => {
    const entries = readdirSync(bundleManifestsDir)
    const jsonFiles = entries.filter(f => f.endsWith('.bundle.json'))
    // Exact 5 manifests as of Q6 spec; this assertion catches accidental omission.
    expect(jsonFiles).toHaveLength(5)
    expect(jsonFiles.sort()).toEqual([
      'acquisition-underwriting.bundle.json',
      'asset-management.bundle.json',
      'facility-operations.bundle.json',
      'project-management.bundle.json',
      'property-management.bundle.json',
    ])
  })

  describe('each fixture parses cleanly to BusinessCaseBundleManifest', () => {
    const fixtures = loadBundleManifests()

    for (const { filename, manifest } of fixtures) {
      describe(filename, () => {
        it('has a non-empty key', () => {
          expect(manifest.key).toBeTruthy()
          expect(typeof manifest.key).toBe('string')
          expect(manifest.key.length).toBeGreaterThan(0)
        })

        it('has a non-empty name', () => {
          expect(manifest.name).toBeTruthy()
          expect(typeof manifest.name).toBe('string')
        })

        it('version matches semver pattern', () => {
          expect(manifest.version).toMatch(SEMVER_PATTERN)
        })

        it('category is a valid BundleCategory literal', () => {
          expect(BUNDLE_CATEGORIES).toContain(manifest.category)
        })

        it('status is a valid BundleStatus literal', () => {
          expect(BUNDLE_STATUSES).toContain(manifest.status)
        })

        it('requiredModules is an array', () => {
          expect(Array.isArray(manifest.requiredModules)).toBe(true)
        })

        it('optionalModules is an array', () => {
          expect(Array.isArray(manifest.optionalModules)).toBe(true)
        })

        it('featureDefaults is a string->string record', () => {
          expect(typeof manifest.featureDefaults).toBe('object')
          expect(manifest.featureDefaults).not.toBeNull()
          for (const [k, v] of Object.entries(manifest.featureDefaults)) {
            expect(typeof k).toBe('string')
            expect(typeof v).toBe('string')
          }
        })

        it('editionMappings is a string->string[] record', () => {
          expect(typeof manifest.editionMappings).toBe('object')
          expect(manifest.editionMappings).not.toBeNull()
          for (const [k, v] of Object.entries(manifest.editionMappings)) {
            expect(typeof k).toBe('string')
            expect(Array.isArray(v)).toBe(true)
          }
        })

        it('deploymentModesSupported contains only valid DeploymentMode literals', () => {
          expect(Array.isArray(manifest.deploymentModesSupported)).toBe(true)
          for (const mode of manifest.deploymentModesSupported) {
            expect(DEPLOYMENT_MODES).toContain(mode)
          }
        })

        it('providerRequirements contains only valid entries', () => {
          expect(Array.isArray(manifest.providerRequirements)).toBe(true)
          for (const req of manifest.providerRequirements) {
            expect(PROVIDER_CATEGORIES).toContain(req.category)
            expect(typeof req.required).toBe('boolean')
            if (req.purpose !== undefined && req.purpose !== null) {
              expect(typeof req.purpose).toBe('string')
            }
          }
        })

        it('integrationProfiles is an array of strings', () => {
          expect(Array.isArray(manifest.integrationProfiles)).toBe(true)
          for (const p of manifest.integrationProfiles) {
            expect(typeof p).toBe('string')
          }
        })

        it('seedWorkspaces is an array of strings', () => {
          expect(Array.isArray(manifest.seedWorkspaces)).toBe(true)
          for (const ws of manifest.seedWorkspaces) {
            expect(typeof ws).toBe('string')
          }
        })

        it('personas is an array of strings', () => {
          expect(Array.isArray(manifest.personas)).toBe(true)
          for (const p of manifest.personas) {
            expect(typeof p).toBe('string')
          }
        })

        it('requirements field is absent or a valid MinimumSpec object (forward-compat with ADR 0007-A1)', () => {
          // Pre-A1 manifests do not include requirements; absence is acceptable.
          if (manifest.requirements !== undefined && manifest.requirements !== null) {
            expect(typeof manifest.requirements).toBe('object')
            // policy is optional; if present must be one of three values
            if (manifest.requirements.policy !== undefined) {
              expect(['Required', 'Recommended', 'Informational']).toContain(
                manifest.requirements.policy,
              )
            }
          } else {
            // absence is the expected state for today's 5 pre-A1 fixtures
            expect(manifest.requirements == null).toBe(true)
          }
        })
      })
    }
  })
})
