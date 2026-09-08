import { Ajv2020, type ErrorObject } from 'ajv/dist/2020.js'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const protocolDirectory = resolve(fileURLToPath(new URL('.', import.meta.url)), '../../protocol')
const schema = JSON.parse(readFileSync(
  resolve(protocolDirectory, 'schemas/carrier-protocol.schema.json'),
  'utf8',
)) as { $id: string, $schema: string, $defs: Record<string, unknown> }
const fixtureManifest = JSON.parse(readFileSync(
  resolve(protocolDirectory, 'fixtures/manifest.json'),
  'utf8',
)) as {
  fixtures: { model: string, file: string }[],
  negativeFixtures: { model: string, file: string, constraint?: string, path?: string }[],
}

function fixture(file: string): unknown {
  return JSON.parse(readFileSync(resolve(protocolDirectory, 'fixtures', file), 'utf8')) as unknown
}

function expectReachableHarborlineSyncAggregate(value: unknown, file: string): void {
  const status = value as {
    aggregate: string
    peers: { state: string, isSecurityEvent: boolean }[]
  }
  const severity = new Map([['has', 0], ['will', 1], ['should', 2], ['couldnt', 3]])
  const expected = status.peers.reduce((worst, peer) => {
    const effective = peer.isSecurityEvent ? 'couldnt' : peer.state
    return severity.get(effective)! > severity.get(worst)! ? effective : worst
  }, 'has')
  expect(status.aggregate, `${file}: aggregate must equal the producer's worst-state-wins roll-up`).toBe(expected)
}

function errorMatchesDeclaredConstraint(error: ErrorObject, constraint: string, path: string): boolean {
  const propertyPath = `/${path}`
  switch (constraint) {
    case 'required':
      return error.keyword === 'required' && error.params.missingProperty === path
    case 'enum':
      return error.keyword === 'enum' && error.instancePath === propertyPath
    case 'exact-case':
    case 'unknown-property':
      return error.keyword === 'additionalProperties' && error.params.additionalProperty === path
    case 'minimum':
      return error.keyword === 'minimum' && error.instancePath === propertyPath
    case 'maximum':
      return error.keyword === 'maximum' && error.instancePath === propertyPath
    default:
      throw new Error(`unsupported declared constraint ${constraint}`)
  }
}

describe('canonical Harborline protocol JSON Schema', () => {
  it('compiles as Draft 2020-12, validates every manifest fixture, and rejects malformed data', () => {
    const ajv = new Ajv2020({ allErrors: true, strict: true })
    expect(() => ajv.compile(schema)).not.toThrow()

    for (const { model, file } of fixtureManifest.fixtures) {
      const validate = ajv.getSchema(`${schema.$id}#/$defs/${model}`)
      expect(validate, `${model} must be defined in the canonical schema`).toBeDefined()
      const value = fixture(file)
      expect(validate?.(value), `${file}: ${ajv.errorsText(validate?.errors)}`).toBe(true)
      if (model === 'HarborlineSyncStatus') expectReachableHarborlineSyncAggregate(value, file)
    }

    for (const { model, file, constraint, path } of fixtureManifest.negativeFixtures) {
      const validate = ajv.getSchema(`${schema.$id}#/$defs/${model}`)
      expect(validate, `${model} must be defined in the canonical schema`).toBeDefined()
      expect(constraint, `${file} must name its violated constraint`).toBeTruthy()
      expect(path, `${file} must name its offending property`).toBeTruthy()
      expect(validate?.(fixture(file)), `${file} unexpectedly satisfies ${constraint} at ${path}`).toBe(false)
      expect(
        validate?.errors?.some((error) => errorMatchesDeclaredConstraint(error, constraint!, path!)),
        `${file}: rejection did not match ${constraint} at ${path}: ${ajv.errorsText(validate?.errors)}`,
      ).toBe(true)
    }

    const coveredModels = new Set(fixtureManifest.fixtures.map(({ model }) => model))
    const uncoveredModels = Object.keys(schema.$defs).filter((model) => !coveredModels.has(model))
    expect(uncoveredModels, 'every canonical schema definition must have a positive manifest case').toEqual([])

    const validateNodeStatus = ajv.getSchema(`${schema.$id}#/$defs/NodeStatus`)
    expect(validateNodeStatus?.({})).toBe(false)
    expect(ajv.errorsText(validateNodeStatus?.errors)).toContain("must have required property 'state'")

    const invalidSchema = { ...schema, invalidKeyword: true }
    expect(() => new Ajv2020({ strict: true }).compile(invalidSchema)).toThrow(/unknown keyword/)
  })
})
