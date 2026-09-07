import { describe, expect, it } from 'vitest'

import { membershipOf, type CompositionManifest, type MembershipClass } from './composition.js'

const manifest: CompositionManifest = {
  solutionId: 'harborline',
  name: 'Harborline',
  capabilities: [
    { capabilityId: 'kernel', membership: 'core' },
    { capabilityId: 'scheduling', membership: 'pack' },
    { capabilityId: 'theme-lab', membership: 'extension' },
    { capabilityId: 'legacy-import', membership: 'n-a' },
  ],
}

describe('membershipOf', () => {
  it.each<[string, MembershipClass]>([
    ['kernel', 'core'],
    ['scheduling', 'pack'],
    ['theme-lab', 'extension'],
    ['legacy-import', 'n-a'],
  ])('returns the declared %s membership', (capabilityId, expected) => {
    expect(membershipOf(manifest, capabilityId)).toBe(expected)
  })

  it('returns n-a for an empty composition', () => {
    expect(membershipOf({ ...manifest, capabilities: [] }, 'kernel')).toBe('n-a')
  })

  it('returns n-a for an unknown capability identifier', () => {
    expect(membershipOf(manifest, 'not-in-this-edition')).toBe('n-a')
  })
})
