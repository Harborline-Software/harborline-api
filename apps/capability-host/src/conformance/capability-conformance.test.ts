/* eslint-disable @typescript-eslint/no-explicit-any -- conformance vectors are intentionally JSON-shaped. */
import { readFileSync } from 'node:fs'

import { describe, expect, it } from 'vitest'

import { indexRuntimeManifest } from '../membrane/announce.js'
import { invokeCapability, normalizeToEnvelope } from '../membrane/invoke.js'
import { redact, redactDeep } from '../membrane/redaction.js'
import type { RuntimeTransport } from '../membrane/runtime-transport.js'
import {
  defaultConfiguration,
  resolveCapability,
  type ConfigurationResolver,
  type EntitlementResolver,
  type ProviderCandidate,
  type ProviderLicenseGate,
  type ResolutionPipeline,
} from '../resolution/pipeline.js'
import { CompositionError, resolveEdition } from '../resolution/pack-resolver.js'

type Vector = {
  id: string
  surface: string
  operation: string
  description: string
  input: any
  expected: any
}

type Divergence = {
  lane: string
  expectedVariant: any
  rationale: string
  source: string
}

const vectors = JSON.parse(
  readFileSync(new URL('../../../../_shared/conformance/capability/vectors.json', import.meta.url), 'utf8'),
) as Vector[]
const divergences = JSON.parse(
  readFileSync(new URL('../../../../_shared/conformance/capability/divergences.json', import.meta.url), 'utf8'),
) as Record<string, Divergence>
const executedVectorIds = new Set<string>()

function equal(left: unknown, right: unknown): boolean {
  if (Object.is(left, right)) return true
  if (Array.isArray(left) || Array.isArray(right)) {
    if (!Array.isArray(left) || !Array.isArray(right)) return false
    return left.length === right.length && left.every((value, index) => equal(value, right[index]))
  }
  if (left === null || right === null || typeof left !== 'object' || typeof right !== 'object') return false
  const leftRecord = left as Record<string, unknown>
  const rightRecord = right as Record<string, unknown>
  const leftKeys = Object.keys(leftRecord)
  const rightKeys = Object.keys(rightRecord)
  return leftKeys.length === rightKeys.length
    && leftKeys.every((key) => Object.prototype.hasOwnProperty.call(rightRecord, key) && equal(leftRecord[key], rightRecord[key]))
}

function assertConformant(vector: Vector, actual: unknown): void {
  const divergence = divergences[vector.id]
  if (equal(actual, vector.expected)) {
    if (divergence?.lane === 'typescript') {
      throw new Error(`${vector.id}: converged divergence: remove the entry`)
    }
    return
  }
  if (divergence?.lane === 'typescript' && equal(actual, divergence.expectedVariant)) return
  throw new Error(
    `${vector.id}: undeclared typescript mismatch\nexpected=${JSON.stringify(vector.expected)}\nactual=${JSON.stringify(actual)}`,
  )
}

function runM3(input: any): unknown {
  return normalizeToEnvelope(input.native, input.fallbackJobId)
}

function runRedaction(vectorInput: any, operation: string): unknown {
  if (operation === 'redact-text') return redact(vectorInput.text, { secrets: vectorInput.secrets })
  return redactDeep(vectorInput.value, { secrets: vectorInput.secrets })
}

function runResolution(input: any): unknown {
  const calls: string[] = []
  const capabilityId = input.capabilityId ?? 'image'
  const entitlement: EntitlementResolver = {
    isEntitled: () => {
      calls.push('entitlement')
      return input.entitled
    },
  }
  const providers = input.providers as ProviderCandidate[]
  const hardware = {
    supportedProviders: () => {
      calls.push('hardware')
      return providers
    },
  }
  const license: ProviderLicenseGate = {
    passes: () => {
      calls.push('license')
      return input.licensePass
    },
  }
  const configuration: ConfigurationResolver = input.configuration === 'none'
    ? { choose: () => { calls.push('configuration'); return null } }
    : {
        choose: (id, survivors) => {
          calls.push('configuration')
          return defaultConfiguration.choose(id, survivors)
        },
      }
  const pipeline: ResolutionPipeline = {
    composition: input.composition,
    entitlement,
    hardware,
    license,
    configuration,
  }
  return { result: resolveCapability(pipeline, capabilityId), calls }
}

function packCatalog(input: any): any[] {
  return input.catalog.map((pack: any) => ({
    ...pack,
    provides: pack.provides ?? [],
    composesOver: pack.composesOver ?? [],
  }))
}

function runPack(input: any, operation: string): unknown {
  try {
    const edition = resolveEdition(packCatalog(input), input.seed)
    if (operation === 'resolve-error') return { throws: 'none' }
    return {
      packs: edition.packs,
      defaults: edition.resolvedDefaults,
      defaultProvenance: edition.provenance.defaults,
    }
  } catch (error) {
    if (operation !== 'resolve-error') throw error
    if (error instanceof CompositionError) return { throws: 'CompositionError', reason: error.reason }
    throw error
  }
}

function runAnnounce(input: any): unknown {
  try {
    const entry = indexRuntimeManifest({
      runtimeId: input.runtimeId,
      name: input.runtimeId,
      contractVersion: '0.1.0',
      capabilities: input.capabilities,
    })
    return {
      byCapability: Object.fromEntries(
        [...entry.byCapability.entries()].map(([id, capability]) => [id, { schemaVersion: capability.schemaVersion }]),
      ),
    }
  } catch (error) {
    return { throws: error instanceof Error ? error.name : 'unknown' }
  }
}

async function runInvokeLog(input: any): Promise<unknown> {
  const records: any[] = []
  const transport: RuntimeTransport = {
    runtimeId: input.runtimeId,
    mode: 'in-process',
    announce: async () => ({ runtimeId: input.runtimeId, name: input.runtimeId, contractVersion: '0.1.0', capabilities: [] }),
    negotiateOffer: async () => ({ contractVersion: '0.1.0', capabilitySchemaVersions: {} }),
    health: async () => ({ kind: 'liveness', state: 'up', detail: null }),
    invoke: async () => ({
      jobId: 'job-log', status: 'succeeded', progress: 1, artifacts: [],
      usage: { unit: 'call', quantity: 1, costMicros: null, tier: 'local' }, error: null,
    }),
    cancel: async () => undefined,
  }
  await invokeCapability(transport, {
    capabilityId: input.capabilityId,
    core: { capabilityId: input.capabilityId },
    providerInputs: {},
    attachments: [],
    idempotencyKey: 'idem-conformance',
    correlationId: input.correlationId,
    transport: input.transport,
  } as any, {
    principal: { id: input.principalId },
    logSink: { write: (record) => records.push(record) },
  })
  const record = records[0]
  return {
    message: record.message,
    correlationId: record.correlationId,
    runtimeId: record.runtimeId,
    capabilityId: record.capabilityId,
  }
}

async function runVector(candidate: Vector): Promise<unknown> {
  executedVectorIds.add(candidate.id)
  switch (candidate.surface) {
    case 'm3':
      if (candidate.operation === 'normalize') return runM3(candidate.input)
      if (candidate.operation === 'invoke-log') return runInvokeLog(candidate.input)
      break
    case 'redaction':
      if (candidate.operation === 'redact-text' || candidate.operation === 'redact-deep') {
        return runRedaction(candidate.input, candidate.operation)
      }
      break
    case 'resolution':
      if (candidate.operation === 'resolve') return runResolution(candidate.input)
      break
    case 'pack':
      if (candidate.operation === 'resolve-error' || candidate.operation === 'resolve-summary') {
        return runPack(candidate.input, candidate.operation)
      }
      break
    case 'announce':
      if (candidate.operation === 'index') return runAnnounce(candidate.input)
      break
  }
  throw new Error(
    `${candidate.id}: unsupported conformance surface/operation ${candidate.surface}/${candidate.operation}`,
  )
}

describe('Capability ADR 0103 cross-lane conformance vectors', () => {
  it('has unique vector ids and every declared divergence points to a vector', () => {
    const ids = new Set(vectors.map((candidate) => candidate.id))
    expect(ids.size).toBe(vectors.length)
    for (const [id, entry] of Object.entries(divergences)) {
      expect(ids.has(id), `${id}: dead divergence entry`).toBe(true)
      expect(entry.rationale.trim(), `${id}: missing rationale`).not.toBe('')
      expect(entry.source.trim(), `${id}: missing source`).not.toBe('')
      expect(entry.lane).toMatch(/^(typescript|dotnet)$/)
    }
  })

  it.each(vectors)('$id — $description', async (candidate) => {
    assertConformant(candidate, await runVector(candidate))
  })

  it('executes every loaded vector', () => {
    expect([...executedVectorIds].sort()).toEqual(vectors.map((candidate) => candidate.id).sort())
  })
})
