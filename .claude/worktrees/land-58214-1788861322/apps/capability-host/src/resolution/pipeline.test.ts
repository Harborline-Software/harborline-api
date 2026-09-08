import type { ResolutionState } from '@harborline-software/api-contracts'
import type { DeviceCapabilityProfile } from '@harborline-software/api-contracts'
import { describe, expect, it } from 'vitest'

import type { CompositionManifest } from './composition.js'
import {
  defaultConfiguration,
  deviceCapabilityHardwareResolver,
  resolveCapability,
  staticHardwareResolver,
  stubEntitledAll,
  stubLicensePassAll,
  type EntitlementResolver,
  type ProviderCandidate,
  type ProviderLicenseGate,
  type ResolutionPipeline,
} from './pipeline.js'

const floor: ProviderCandidate = {
  providerId: 'cpu-floor',
  tier: 'local',
  isFloor: true,
  speedHint: 'slow',
}
const fast: ProviderCandidate = {
  providerId: 'gpu-fast',
  tier: 'local',
  isFloor: false,
  speedHint: 'fast',
}

const composition: CompositionManifest = {
  solutionId: 'harborline',
  name: 'Harborline',
  capabilities: [
    { capabilityId: 'image', membership: 'core' },
    { capabilityId: 'bank-import', membership: 'n-a' },
    { capabilityId: 'music', membership: 'pack' },
  ],
}

function pipeline(overrides: Partial<ResolutionPipeline> = {}): ResolutionPipeline {
  return {
    composition,
    entitlement: stubEntitledAll,
    hardware: staticHardwareResolver({ image: [floor, fast], music: [floor] }),
    license: stubLicensePassAll,
    configuration: defaultConfiguration,
    ...overrides,
  }
}

describe('D7 resolution pipeline — typed resolutionState (FE-1) + deliberate order', () => {
  it('resolves a core capability with a fast provider to `available`', () => {
    const r = resolveCapability(pipeline(), 'image')
    expect(r.resolutionState satisfies ResolutionState).toBe('available')
    expect(r.chosenProviderId).toBe('gpu-fast') // config prefers non-floor
    expect(r.isFallback).toBe(false)
  })

  it('stage 1 Membership — an `n-a` capability resolves to `not-in-edition`', () => {
    const r = resolveCapability(pipeline(), 'bank-import')
    expect(r.resolutionState).toBe('not-in-edition')
    expect(r.chosenProviderId).toBeNull()
  })

  it('stage 1 Membership — a capability absent from the composition is also `not-in-edition`', () => {
    const r = resolveCapability(pipeline(), 'llm')
    expect(r.resolutionState).toBe('not-in-edition')
  })

  it('order is load-bearing: an unentitled CORE reads `upsell`, an unentitled PACK reads `locked-entitlement`', () => {
    const denyAll: EntitlementResolver = { isEntitled: () => false }
    expect(resolveCapability(pipeline({ entitlement: denyAll }), 'image').resolutionState).toBe(
      'upsell',
    )
    expect(resolveCapability(pipeline({ entitlement: denyAll }), 'music').resolutionState).toBe(
      'locked-entitlement',
    )
  })

  it('stage 3 Hardware — no fitting provider resolves to `unavailable-hardware`', () => {
    const noHw = staticHardwareResolver({}) // image has no candidates
    const r = resolveCapability(pipeline({ hardware: noHw }), 'image')
    expect(r.resolutionState).toBe('unavailable-hardware')
  })

  it('stage 4 Provider-license — all providers blocked resolves to `locked-entitlement`', () => {
    const blockAll: ProviderLicenseGate = { passes: () => false }
    const r = resolveCapability(pipeline({ license: blockAll }), 'image')
    expect(r.resolutionState).toBe('locked-entitlement')
    expect(r.chosenProviderId).toBeNull()
  })

  it('stage 5 Config — only the floor survives → `degraded` fallback with the floor chosen', () => {
    const floorOnly = staticHardwareResolver({ image: [floor] })
    const r = resolveCapability(pipeline({ hardware: floorOnly }), 'image')
    expect(r.resolutionState).toBe('degraded')
    expect(r.chosenProviderId).toBe('cpu-floor')
    expect(r.isFallback).toBe(true)
    expect(r.selectionReason).toBe('fallback-floor')
  })

  it('short-circuits: membership failure never consults entitlement/hardware/license', () => {
    let consulted = false
    const spyEntitlement: EntitlementResolver = {
      isEntitled: () => {
        consulted = true
        return true
      },
    }
    resolveCapability(pipeline({ entitlement: spyEntitlement }), 'bank-import')
    expect(consulted).toBe(false) // n-a short-circuited at stage 1
  })
})

describe('ADR 0132 local-model admission from DeviceCapabilityProfile', () => {
  const profile: DeviceCapabilityProfile = {
    schemaVersion: 1,
    detectedAtMs: 1_753_200_000_000,
    osFamily: 'macos',
    architecture: 'aarch64',
    systemMemoryBytes: 16 * 1024 ** 3,
    fastMemoryBytes: 16 * 1024 ** 3,
    fastMemoryKind: 'unified',
    bandwidthClass: 'unknown',
    bandwidthEvidence: 'unknown',
    maximumLocalAiTier: 'T-A',
    detectionStatus: 'partial',
    limitations: ['bandwidthUnavailable'],
  }

  it('admits declared local candidates no higher than the detected tier', () => {
    const resolver = deviceCapabilityHardwareResolver(profile, {
      llm: [
        { ...floor, providerId: 'embedding', minimumDeviceCapabilityTier: 'T-E' },
        { ...fast, providerId: 'assistant', minimumDeviceCapabilityTier: 'T-A' },
        { ...fast, providerId: 'review', minimumDeviceCapabilityTier: 'T-R' },
      ],
    })

    expect(resolver.supportedProviders('llm').map((candidate) => candidate.providerId)).toEqual([
      'embedding',
      'assistant',
    ])
  })

  it('fails closed for a local candidate without a declared tier floor', () => {
    const resolver = deviceCapabilityHardwareResolver(profile, { llm: [floor] })
    expect(resolver.supportedProviders('llm')).toEqual([])
  })

  it('does not apply the local device tier to remote or cloud candidates', () => {
    const resolver = deviceCapabilityHardwareResolver(profile, {
      llm: [
        { ...fast, providerId: 'remote-review', tier: 'remote' },
        { ...fast, providerId: 'cloud-review', tier: 'cloud' },
      ],
    })
    expect(resolver.supportedProviders('llm')).toHaveLength(2)
  })
})
