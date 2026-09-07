import { describe, expect, it } from 'vitest'

import { indexRuntimeManifest, type RuntimeManifest } from './announce.js'

describe('indexRuntimeManifest', () => {
  it('indexes every announced capability while preserving runtime metadata', () => {
    const manifest: RuntimeManifest = {
      runtimeId: 'harborline-runtime',
      name: 'Harborline',
      contractVersion: '0.1.0',
      capabilities: [
        { capabilityId: 'image', schemaVersion: '1.0.0', providers: [] },
        { capabilityId: 'calendar', schemaVersion: '2.3.0', providers: [] },
      ],
    }

    const entry = indexRuntimeManifest(manifest)

    expect(entry.runtimeId).toBe('harborline-runtime')
    expect(entry.contractVersion).toBe('0.1.0')
    expect(entry.byCapability).toEqual(
      new Map([
        ['image', manifest.capabilities[0]],
        ['calendar', manifest.capabilities[1]],
      ]),
    )
  })

  it('returns an empty lookup for a runtime that announces no capabilities', () => {
    const manifest: RuntimeManifest = {
      runtimeId: 'empty-runtime',
      name: 'Empty runtime',
      contractVersion: '0.1.0',
      capabilities: [],
    }

    // typed-total: no runtime-invalid case
    expect(indexRuntimeManifest(manifest).byCapability).toEqual(new Map())
  })
})
