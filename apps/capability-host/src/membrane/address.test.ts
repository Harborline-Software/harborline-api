import { describe, expect, it } from 'vitest'

import {
  ADDRESS_MODE_TO_TRANSPORT_TIER,
  isV0AddressMode,
  transportTierFor,
  V0_ADDRESS_MODES,
  type AddressMode,
} from './address.js'

describe('Address — NET-2 superset of ADR-0061 TransportTier', () => {
  it('name-maps each Capability addressing mode onto an ADR-0061 tier (or null for in-process)', () => {
    // NET-2: the map is a SUPERSET name-map, not a third invented enum.
    expect(ADDRESS_MODE_TO_TRANSPORT_TIER['in-process']).toBeNull()
    expect(ADDRESS_MODE_TO_TRANSPORT_TIER['local-subprocess']).toBe('LocalNetwork')
    expect(ADDRESS_MODE_TO_TRANSPORT_TIER['remote-mesh']).toBe('MeshVpn')
    expect(ADDRESS_MODE_TO_TRANSPORT_TIER['relay']).toBe('ManagedRelay')
  })

  it('covers exactly the 4 superset modes (the deferred two included in the type)', () => {
    const modes = Object.keys(ADDRESS_MODE_TO_TRANSPORT_TIER) as AddressMode[]
    expect(new Set(modes)).toEqual(
      new Set<AddressMode>(['in-process', 'local-subprocess', 'remote-mesh', 'relay']),
    )
  })

  it('maps all three ADR-0061 tiers (LocalNetwork, MeshVpn, ManagedRelay) — no tier orphaned', () => {
    const mappedTiers = Object.values(ADDRESS_MODE_TO_TRANSPORT_TIER).filter(
      (t) => t !== null,
    )
    expect(new Set(mappedTiers)).toEqual(
      new Set(['LocalNetwork', 'MeshVpn', 'ManagedRelay']),
    )
  })

  it('v0 implements exactly the two local arms; mesh/relay are deferred', () => {
    expect(V0_ADDRESS_MODES).toEqual(['in-process', 'local-subprocess'])
    expect(isV0AddressMode('in-process')).toBe(true)
    expect(isV0AddressMode('local-subprocess')).toBe(true)
    expect(isV0AddressMode('remote-mesh')).toBe(false)
    expect(isV0AddressMode('relay')).toBe(false)
  })

  it('transportTierFor resolves a concrete address to its 0061 tier', () => {
    expect(
      transportTierFor({ mode: 'in-process', runtimeId: 'r' }),
    ).toBeNull()
    expect(
      transportTierFor({
        mode: 'local-subprocess',
        runtimeId: 'r',
        baseUrl: 'http://127.0.0.1:1',
      }),
    ).toBe('LocalNetwork')
  })
})
