import { describe, expect, it } from 'vitest'

import {
  DeviceCapabilityTier,
  admitsLocalAiTier,
  parseDeviceCapabilityProfile,
  type DeviceCapabilityProfile,
} from '../device-capability.js'

const assistantProfile: DeviceCapabilityProfile = {
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

describe('DeviceCapabilityProfile contract', () => {
  it('admits local models at or below the detected ceiling only', () => {
    expect(admitsLocalAiTier(assistantProfile, DeviceCapabilityTier.Embedding)).toBe(true)
    expect(admitsLocalAiTier(assistantProfile, DeviceCapabilityTier.SmallChat)).toBe(true)
    expect(admitsLocalAiTier(assistantProfile, DeviceCapabilityTier.Assistant)).toBe(true)
    expect(admitsLocalAiTier(assistantProfile, DeviceCapabilityTier.Review)).toBe(false)
    expect(admitsLocalAiTier(assistantProfile, DeviceCapabilityTier.Concurrent)).toBe(false)
  })

  it('accepts the camel-case host payload', () => {
    expect(parseDeviceCapabilityProfile(structuredClone(assistantProfile))).toEqual(assistantProfile)
  })

  it('rejects an unknown tier instead of optimistically coercing it', () => {
    expect(() =>
      parseDeviceCapabilityProfile({ ...assistantProfile, maximumLocalAiTier: 'T-X' }),
    ).toThrow(/maximumLocalAiTier/)
  })

  it('rejects malformed byte counts before they reach admission logic', () => {
    expect(() =>
      parseDeviceCapabilityProfile({ ...assistantProfile, fastMemoryBytes: -1 }),
    ).toThrow(/fastMemoryBytes/)
  })

  it.each([
    ['a negative detection timestamp', { detectedAtMs: -1 }, /detectedAtMs/],
    ['an unrecognized operating-system family', { osFamily: 'plan9' }, /osFamily/],
    ['an empty architecture', { architecture: '' }, /architecture/],
    ['a non-string architecture', { architecture: 64 }, /architecture/],
    ['a negative system-memory byte count', { systemMemoryBytes: -1 }, /systemMemoryBytes/],
    ['an unrecognized fast-memory kind', { fastMemoryKind: 'sharedVram' }, /fastMemoryKind/],
    ['an unrecognized bandwidth class', { bandwidthClass: 'extreme' }, /bandwidthClass/],
    ['an unrecognized bandwidth evidence value', { bandwidthEvidence: 'measured' }, /bandwidthEvidence/],
    ['an unrecognized detection status', { detectionStatus: 'failed' }, /detectionStatus/],
    ['an unrecognized limitation code', { limitations: ['bandwidthUnavailable', 'futureCode'] }, /limitations/],
  ] as const)('rejects %s from the host payload', (_description, overrides, message) => {
    expect(() => parseDeviceCapabilityProfile({ ...assistantProfile, ...overrides })).toThrow(message)
  })

  it('rejects a tier that exceeds the supplied memory evidence', () => {
    expect(() =>
      parseDeviceCapabilityProfile({
        ...assistantProfile,
        fastMemoryBytes: null,
        fastMemoryKind: 'unknown',
        maximumLocalAiTier: 'T-C',
      }),
    ).toThrow(/exceeds hardware evidence/)

    expect(() =>
      parseDeviceCapabilityProfile({
        ...assistantProfile,
        fastMemoryBytes: 64 * 1024 ** 3,
        fastMemoryKind: 'system',
        maximumLocalAiTier: 'T-A',
      }),
    ).toThrow(/exceeds hardware evidence/)
  })
})
