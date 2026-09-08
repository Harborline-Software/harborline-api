import { describe, expect, it } from 'vitest'

import {
  isContractVersionCompatible,
  negotiate,
  type ShellNegotiationProfile,
} from './negotiate.js'

const shell: ShellNegotiationProfile = {
  contractVersion: '0.1.0',
  supportedSchemaVersions: { image: '1.0.0', 'bank-import': '1.0.0' },
}

describe('Negotiate — connect-time version + capability handshake (no lockstep)', () => {
  it('same-major contract versions are compatible (additive minor/patch)', () => {
    expect(isContractVersionCompatible('0.1.0', '0.1.0')).toBe(true)
    expect(isContractVersionCompatible('0.1.0', '0.9.3')).toBe(true) // runtime ahead, same major
    expect(isContractVersionCompatible('1.4.0', '1.0.0')).toBe(true)
  })

  it('different-major contract versions are incompatible; unparseable fails closed', () => {
    expect(isContractVersionCompatible('1.0.0', '2.0.0')).toBe(false)
    expect(isContractVersionCompatible('0.1.0', 'garbage')).toBe(false)
  })

  it('accepts the intersection of capabilities both sides speak at a matching schema major', () => {
    const result = negotiate(shell, {
      contractVersion: '0.1.0',
      capabilitySchemaVersions: { image: '1.2.0', 'bank-import': '1.0.0', music: '1.0.0' },
    })
    expect(result.compatible).toBe(true)
    expect(result.agreedContractVersion).toBe('0.1.0')
    // `music` is dropped (shell does not speak it); image accepted at major-1 match.
    expect(new Set(result.acceptedCapabilities)).toEqual(
      new Set(['image', 'bank-import']),
    )
  })

  it('drops a capability whose schema MAJOR diverges', () => {
    const result = negotiate(shell, {
      contractVersion: '0.1.0',
      capabilitySchemaVersions: { image: '2.0.0' }, // major mismatch vs shell 1.0.0
    })
    expect(result.compatible).toBe(true)
    expect(result.acceptedCapabilities).toEqual([])
  })

  it('rejects everything when the contract major mismatches', () => {
    const result = negotiate(shell, {
      contractVersion: '9.0.0',
      capabilitySchemaVersions: { image: '1.0.0' },
    })
    expect(result.compatible).toBe(false)
    expect(result.acceptedCapabilities).toEqual([])
    expect(result.reason).toContain('contract-version mismatch')
  })
})
