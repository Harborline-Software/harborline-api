import { describe, it, expect } from 'vitest'

import type { EntityRef, StorageRef, StorageRefKind } from '../index.js'

describe('cross-domain reference projections', () => {
  it('projects RegistryEntityId as an opaque string EntityRef', () => {
    const entityRef: EntityRef = 'registry-entity-1'

    expect(entityRef).toBe('registry-entity-1')
  })

  it('projects all StorageRef tiers and their serialized fields', () => {
    const refs = [
      {
        kind: 'inline',
        inlineBytes: 'AQID',
        foundationCid: null,
        externalUrl: null,
      },
      {
        kind: 'foundationBlob',
        inlineBytes: null,
        foundationCid: 'bafy-test-cid',
        externalUrl: null,
      },
      {
        kind: 'externalUrl',
        inlineBytes: null,
        foundationCid: null,
        externalUrl: 'https://example.test/blob/1',
      },
    ] satisfies StorageRef[]

    const kinds: StorageRefKind[] = refs.map(ref => ref.kind)

    expect(kinds).toEqual(['inline', 'foundationBlob', 'externalUrl'])
    expect(refs[0].inlineBytes).toBe('AQID')
    expect(refs[1].foundationCid).toBe('bafy-test-cid')
    expect(refs[2].externalUrl).toBe('https://example.test/blob/1')
  })
})
