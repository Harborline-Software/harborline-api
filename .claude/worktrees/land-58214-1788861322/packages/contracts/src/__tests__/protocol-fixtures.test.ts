import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import {
  HARBORLINE_HOST_COMMANDS,
  HARBORLINE_PROTOCOL_VERSION,
  HARBORLINE_APPLICATION_ROUTES,
  parseHarborlineProtocolModel,
  assertOutboundProtocolModel,
  type HarborlineProtocolModels,
} from '../protocol.js'

const fixtureDirectory = resolve(fileURLToPath(new URL('.', import.meta.url)), '../../protocol/fixtures')
const schema = JSON.parse(readFileSync(
  resolve(fixtureDirectory, '../schemas/carrier-protocol.schema.json'),
  'utf8',
)) as { $defs: Record<string, unknown> }
type ManifestCase = {
  model: keyof HarborlineProtocolModels
  file: string
  outcome: 'accept' | 'reject'
  constraint?: string
  path?: string
}

const fixtureManifest = JSON.parse(readFileSync(
  resolve(fixtureDirectory, 'manifest.json'),
  'utf8',
)) as { fixtures: ManifestCase[], negativeFixtures: ManifestCase[] }
const manifestCases = [...fixtureManifest.fixtures, ...fixtureManifest.negativeFixtures]

function fixture(name: string): unknown {
  return JSON.parse(readFileSync(resolve(fixtureDirectory, name), 'utf8')) as unknown
}

function assertDeclaredConstraint(error: unknown, testCase: ManifestCase): void {
  const diagnostic = error instanceof Error ? error.message : String(error)
  const path = testCase.path
  expect(path, `${testCase.file} must name its offending property`).toBeTruthy()
  expect(diagnostic, `${testCase.file} must identify ${path}`).toContain(path)

  switch (testCase.constraint) {
    case 'required':
      expect(diagnostic).toContain(`missing required property ${path}`)
      break
    case 'enum':
      expect(diagnostic).toContain('outside enum')
      break
    case 'exact-case':
      expect(diagnostic).toContain(`unexpected property ${path}`)
      break
    case 'minimum':
      expect(diagnostic).toContain('below minimum')
      break
    case 'maximum':
      expect(diagnostic).toContain('above maximum')
      break
    case 'unknown-property':
      expect(diagnostic).toContain(`unexpected property ${path}`)
      break
    default:
      throw new Error(`${testCase.file}: unsupported declared constraint ${testCase.constraint}`)
  }
}

describe('Harborline protocol generated TypeScript projection', () => {
  it('pins the protocol version and complete host command surface', () => {
    expect(HARBORLINE_PROTOCOL_VERSION).toBe('2.0.0')
    expect(HARBORLINE_APPLICATION_ROUTES.getSyncStatus).toBe('/api/local-node/sync-status')
    expect(Object.values(HARBORLINE_HOST_COMMANDS).sort()).toEqual([
      'append_renderer_log',
      'capability_cp_demo_execute',
      'capability_health',
      'capability_invoke',
      'current_principal',
      'get_data_location_status',
      'get_device_capability_profile',
      'get_peer_sync_config',
      'node_status',
      'set_peer_sync_config',
    ])
  })

  it('validates every manifest fixture against its declared outcome', () => {
    for (const testCase of manifestCases) {
      const { model, file, outcome, constraint } = testCase
      const expected = fixture(file)
      const candidate = fixture(file)
      expect(Object.hasOwn(schema.$defs, model), `${file}: unknown protocol model ${model}`).toBe(true)

      if (outcome === 'accept') {
        const parsed = parseHarborlineProtocolModel(model, candidate)
        expect(parsed, file).toEqual(expected)
      } else {
        expect(constraint, `${file} must name its violated constraint`).toBeTruthy()
        try {
          parseHarborlineProtocolModel(model, candidate)
          throw new Error(`${file}: parser accepted a fixture violating ${constraint}`)
        } catch (error) {
          assertDeclaredConstraint(error, testCase)
        }
      }
    }
  })

  it('round-trips metadata extensions without opening the top-level model', () => {
    const expected = fixture('device-capability-profile.json') as Record<string, unknown>
    const parsed = parseHarborlineProtocolModel('DeviceCapabilityProfile', expected)
    expect(JSON.parse(JSON.stringify(parsed))).toEqual(expected)
    expect(() => parseHarborlineProtocolModel('DeviceCapabilityProfile', {
      ...expected,
      injected: true,
    })).toThrow(/unexpected property injected/)
  })

  it('refuses to serialize an integer above the cross-language maximum', () => {
    const cadence = fixture('sync-cadence-above-safe-integer.json') as HarborlineProtocolModels['SyncCadence']
    expect(() => JSON.stringify(assertOutboundProtocolModel(
      'SyncCadence',
      'carrier.application.getSyncStatus',
      cadence,
    ))).toThrow(/SyncCadence payload rejected.*outbound/)
  })

  it('rejects schema enum, range, and closed-object violations', () => {
    expect(() => parseHarborlineProtocolModel('CapabilityResult', {
      ...fixture('capability-result.json') as object,
      progress: 2,
    })).toThrow(/above maximum/)
    expect(() => parseHarborlineProtocolModel('Principal', {
      ...fixture('principal.json') as object,
      kind: 'administrator',
    })).toThrow(/outside enum/)
    expect(() => parseHarborlineProtocolModel('CapabilityInvokeRequest', {
      ...fixture('capability-invoke-request.json') as object,
      principal: { id: 'caller-controlled' },
    })).toThrow(/unexpected property principal/)
  })

  it('rejects a DeviceCapabilityProfile limitation outside the closed union', () => {
    const expected = fixture('device-capability-profile.json') as Record<string, unknown>
    expect(() => parseHarborlineProtocolModel('DeviceCapabilityProfile', {
      ...expected,
      limitations: ['futureHostCondition'],
    })).toThrow(/limitations\[0\].*outside enum/)
  })

  it.each([Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY])(
    'rejects non-finite JSON numbers (%s)',
    (progress) => {
      expect(() => parseHarborlineProtocolModel('CapabilityResult', {
        ...fixture('capability-result.json') as object,
        progress,
      })).toThrow(/expected number/)
    },
  )
})
