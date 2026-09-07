import { describe, expect, expectTypeOf, it } from 'vitest'

import { collectLicenseComponents, evaluateLicenseGate } from './license-gate.js'
import type { LicenseComponent, ProviderManifest } from './manifest.js'

const engine = {
  role: 'engine',
  spdx: 'MIT',
  commercialUse: 'yes',
} satisfies LicenseComponent

const weights = {
  role: 'weights',
  spdx: 'Apache-2.0',
  commercialUse: 'yes',
} satisfies LicenseComponent

const sdk = {
  role: 'sdk',
  spdx: 'BSD-3-Clause',
  commercialUse: 'yes',
} satisfies LicenseComponent

const dataSource = {
  role: 'dataSource',
  spdx: 'proprietary',
  commercialUse: 'unknown',
} satisfies LicenseComponent

function manifest(overrides: Partial<ProviderManifest> = {}): ProviderManifest {
  return {
    manifestVersion: 2,
    id: 'license-test-provider',
    name: 'License test provider',
    version: '1.0.0',
    kind: 'local',
    capability: ['embeddings'],
    weightsLicense: [],
    hardware: {},
    tier: 'local',
    packaging: 'bundled',
    signing: 'n-a',
    ...overrides,
  }
}

describe('collectLicenseComponents', () => {
  it('collects every declared component in engine, weights, sdk, data-source order', () => {
    const secondWeights = {
      role: 'weights',
      spdx: 'MIT',
      commercialUse: 'yes',
    } satisfies LicenseComponent

    expect(
      collectLicenseComponents(
        manifest({
          engineLicense: engine,
          weightsLicense: [weights, secondWeights],
          sdkLicense: sdk,
          dataSourceTerms: dataSource,
        }),
      ),
    ).toEqual([engine, weights, secondWeights, sdk, dataSource])
  })

  it('returns an empty array when no license components are declared', () => {
    expect(
      collectLicenseComponents(
        manifest({
          engineLicense: null,
          weightsLicense: [],
          sdkLicense: null,
          dataSourceTerms: null,
        }),
      ),
    ).toEqual([])
  })
})

describe('evaluateLicenseGate', () => {
  it.each([
    ['yes', false],
    ['unknown', true],
    ['no', true],
  ] as const)(
    'maps the commercial-use boundary %s to blocked=%s without an override',
    (commercialUse, blocked) => {
      const component = { ...weights, commercialUse } satisfies LicenseComponent

      expect(evaluateLicenseGate(manifest({ weightsLicense: [component] }))).toEqual({
        commercialUse,
        blocked,
        components: [component],
        attestationOverride: false,
      })
    },
  )

  it('uses the minimum eligibility across every component slot', () => {
    const deniedWeights = {
      ...weights,
      spdx: 'CC-BY-NC-4.0',
      commercialUse: 'no',
    } satisfies LicenseComponent

    const gate = evaluateLicenseGate(
      manifest({
        engineLicense: engine,
        weightsLicense: [weights, deniedWeights],
        sdkLicense: sdk,
        dataSourceTerms: dataSource,
      }),
    )

    expect(gate.commercialUse).toBe('no')
    expect(gate.blocked).toBe(true)
    expect(gate.components).toEqual([engine, weights, deniedWeights, sdk, dataSource])
  })

  it('fails closed as unknown for an empty manifest', () => {
    expect(evaluateLicenseGate(manifest())).toEqual({
      commercialUse: 'unknown',
      blocked: true,
      components: [],
      attestationOverride: false,
    })
  })

  it('lets an attestation override unblock no without changing the verdict', () => {
    const denied = { ...weights, commercialUse: 'no' } satisfies LicenseComponent

    expect(evaluateLicenseGate(manifest({ weightsLicense: [denied] }), true)).toEqual({
      commercialUse: 'no',
      blocked: false,
      components: [denied],
      attestationOverride: true,
    })
  })

  it('keeps a yes gate unblocked with or without an attestation override', () => {
    const cleanManifest = manifest({ engineLicense: engine, weightsLicense: [weights] })

    expect(evaluateLicenseGate(cleanManifest, false).blocked).toBe(false)
    expect(evaluateLicenseGate(cleanManifest, true).blocked).toBe(false)
  })
})

describe('invalid input boundary', () => {
  it('rejects malformed commercial-use labels and missing weights at compile time', () => {
    type CommercialUse = LicenseComponent['commercialUse']
    type ManifestWithoutWeights = Omit<ProviderManifest, 'weightsLicense'>

    expectTypeOf<'sometimes'>().not.toMatchTypeOf<CommercialUse>()
    expectTypeOf<ManifestWithoutWeights>().not.toMatchTypeOf<ProviderManifest>()
  })
})
