/**
 * Contract test for the KG-search `embeddings` + `rerank` capability kinds and
 * the S4 license-gate evaluator (ADR 0123 amendment 2026-06-24 — the ADR 0135
 * F3-lift vector tier).
 *
 * Same TS-canonical drift-detection discipline as `capability.test.ts`: pin the
 * thin-CORE key sets, the artifact shapes, and the S4 MIN-over-components gate so
 * a future edit that widens a CORE or weakens the gate FAILS at CI.
 *
 * The load-bearing assertion here (the build-gate the dispatch names): the S4
 * license gate PASSES BGE-M3 (MIT) + bge-reranker-v2-m3 (MIT) for commercial
 * intent, and BLOCKS a CC-BY-NC / unknown weights arm fail-closed — the structural
 * reason the MIT models were chosen (no permissive-license regression can ship).
 */

import { describe, it, expect } from 'vitest'

import {
  collectLicenseComponents,
  evaluateLicenseGate,
  type EmbeddingsCore,
  type RerankCore,
  type CapabilityCore,
  type EmbeddingsArtifact,
  type RerankArtifact,
  type RerankScore,
  type Artifact,
  type LicenseComponent,
  type ProviderManifest,
} from '../capability.js'

describe('@harborline-software/api-contracts — KG capability kinds (ADR 0123 amendment 2026-06-24)', () => {
  // -------------------------------------------------------------------------
  // Thin-CORE discipline — embeddings = 3 fields, rerank = 4 fields
  // -------------------------------------------------------------------------
  describe('per-capability thin CORE keys are EXACTLY the thin set', () => {
    it('EmbeddingsCore has exactly the 3 fields, no more', () => {
      const core = {
        texts: ['invoice line one', 'invoice line two'],
        dimension: 1024, // BGE-M3 floor — a value, not a per-provider field
        timeout: 30_000,
      } satisfies EmbeddingsCore
      expect(Object.keys(core).sort()).toEqual(['dimension', 'texts', 'timeout'].sort())
      expect(Object.keys(core)).toHaveLength(3)
      // it IS a CapabilityCore arm
      const asCore: CapabilityCore = core
      expect(asCore).toBeDefined()
    })

    it('RerankCore has exactly the 4 fields (topK optional), no more', () => {
      const core = {
        query: 'what did acme invoice in june',
        documents: ['doc a', 'doc b', 'doc c'],
        topK: 2,
        timeout: 30_000,
      } satisfies RerankCore
      expect(Object.keys(core).sort()).toEqual(['documents', 'query', 'timeout', 'topK'].sort())
      expect(Object.keys(core)).toHaveLength(4)
      const asCore: CapabilityCore = core
      expect(asCore).toBeDefined()
    })

    it('RerankCore.topK is OPTIONAL (absent ⇒ score all)', () => {
      const core = {
        query: 'q',
        documents: ['a'],
        timeout: 1000,
      } satisfies RerankCore
      expect(core.topK).toBeUndefined()
    })
  })

  // -------------------------------------------------------------------------
  // Artifact shapes — model+version ride the artifact (G-5 versioned projection)
  // -------------------------------------------------------------------------
  describe('result artifacts carry model+version (G-5 — no silent mixed index)', () => {
    it('EmbeddingsArtifact is a kind-discriminated Artifact with vectors+dimension+model', () => {
      const artifact: EmbeddingsArtifact = {
        kind: 'embeddings',
        vectors: [[0.1, 0.2, 0.3]],
        dimension: 3,
        model: 'bge-m3',
        modelVersion: '1.0',
      }
      const asArtifact: Artifact = artifact
      expect(asArtifact.kind).toBe('embeddings')
      // the model id rides the artifact — the consumer pins it per row
      expect(artifact.model).toBe('bge-m3')
      expect(artifact.vectors[0]).toHaveLength(artifact.dimension)
    })

    it('RerankArtifact carries {index,score} back-references + reranker model', () => {
      const scored: RerankScore[] = [
        { index: 2, score: 0.91 },
        { index: 0, score: 0.42 },
      ]
      const artifact: RerankArtifact = {
        kind: 'rerank',
        scored,
        model: 'bge-reranker-v2-m3',
        modelVersion: '1.0',
      }
      const asArtifact: Artifact = artifact
      expect(asArtifact.kind).toBe('rerank')
      // scored references back into the request documents by index
      expect(artifact.scored[0].index).toBe(2)
      expect(artifact.model).toBe('bge-reranker-v2-m3')
    })
  })

  // -------------------------------------------------------------------------
  // S4 license AND-gate evaluator — the build-gate
  // -------------------------------------------------------------------------
  describe('S4 license AND-gate (the build-gate: BGE-M3 + reranker MIT ⇒ PASS)', () => {
    /** Build a minimal embeddings provider manifest with the given license components. */
    function manifestWith(
      engine: LicenseComponent | null,
      weights: LicenseComponent[],
      capability = 'embeddings',
    ): ProviderManifest {
      return {
        manifestVersion: 2,
        id: 'm',
        name: 'M',
        version: '1.0.0',
        kind: 'local',
        capability: [capability],
        engineLicense: engine,
        weightsLicense: weights,
        hardware: { 'min-cpu': { support: 'good', accel: 'cpu' } },
        tier: 'local',
        packaging: 'bundled',
        signing: 'n-a',
      }
    }

    it('PASSES BGE-M3 — MIT weights + MIT engine ⇒ commercial yes, not blocked', () => {
      const bgeM3 = manifestWith(
        { role: 'engine', spdx: 'MIT', commercialUse: 'yes' }, // ONNX runtime / transformers
        [{ role: 'weights', spdx: 'MIT', commercialUse: 'yes' }], // BGE-M3 weights = MIT
      )
      const gate = evaluateLicenseGate(bgeM3)
      expect(gate.commercialUse).toBe('yes')
      expect(gate.blocked).toBe(false)
      // the MIN was driven by exactly the declared components
      expect(gate.components).toHaveLength(2)
    })

    it('PASSES bge-reranker-v2-m3 — MIT weights + MIT engine ⇒ commercial yes', () => {
      const reranker = manifestWith(
        { role: 'engine', spdx: 'MIT', commercialUse: 'yes' },
        [{ role: 'weights', spdx: 'MIT', commercialUse: 'yes' }],
        'rerank',
      )
      const gate = evaluateLicenseGate(reranker)
      expect(gate.commercialUse).toBe('yes')
      expect(gate.blocked).toBe(false)
    })

    it('BLOCKS a CC-BY-NC weights arm fail-closed (the reason BGE-M3 was chosen)', () => {
      // A non-commercial multilingual model (e.g. CC-BY-NC) would be `no` on its
      // weights arm; the MIN drops the whole provider to `no` and BLOCKS it.
      const ncModel = manifestWith({ role: 'engine', spdx: 'MIT', commercialUse: 'yes' }, [
        { role: 'weights', spdx: 'CC-BY-NC-4.0', commercialUse: 'no', restrictions: 'non-commercial' },
      ])
      const gate = evaluateLicenseGate(ncModel)
      expect(gate.commercialUse).toBe('no')
      expect(gate.blocked).toBe(true)
    })

    it('BLOCKS an UNKNOWN license fail-closed (an undeclared license is not a clean yes)', () => {
      const unknownWeights = manifestWith({ role: 'engine', spdx: 'MIT', commercialUse: 'yes' }, [
        { role: 'weights', spdx: 'unknown', commercialUse: 'unknown' },
      ])
      const gate = evaluateLicenseGate(unknownWeights)
      expect(gate.commercialUse).toBe('unknown')
      expect(gate.blocked).toBe(true)
    })

    it('BLOCKS a provider with NO declared components fail-closed', () => {
      const empty = manifestWith(null, [])
      const gate = evaluateLicenseGate(empty)
      expect(gate.commercialUse).toBe('unknown')
      expect(gate.blocked).toBe(true)
      expect(collectLicenseComponents(empty)).toHaveLength(0)
    })

    it('MIN is monotone — one `no` arm overrides many `yes` arms', () => {
      const mixed = manifestWith({ role: 'engine', spdx: 'MIT', commercialUse: 'yes' }, [
        { role: 'weights', spdx: 'MIT', commercialUse: 'yes' },
        { role: 'weights', spdx: 'GPL-3.0-only', commercialUse: 'no' }, // one bad arm
      ])
      expect(evaluateLicenseGate(mixed).commercialUse).toBe('no')
    })

    it('a timestamped attestation override lifts the block (but not the verdict)', () => {
      const ncModel = manifestWith({ role: 'engine', spdx: 'MIT', commercialUse: 'yes' }, [
        { role: 'weights', spdx: 'CC-BY-NC-4.0', commercialUse: 'no' },
      ])
      const gate = evaluateLicenseGate(ncModel, /* attestationOverride */ true)
      // the verdict stays honest (still `no`)…
      expect(gate.commercialUse).toBe('no')
      // …but the override unblocks it (operator took the risk, S4 / council Q5)
      expect(gate.blocked).toBe(false)
      expect(gate.attestationOverride).toBe(true)
    })

    it('a `yes` gate is never blocked and needs no override', () => {
      const mit = manifestWith({ role: 'engine', spdx: 'MIT', commercialUse: 'yes' }, [
        { role: 'weights', spdx: 'MIT', commercialUse: 'yes' },
      ])
      expect(evaluateLicenseGate(mit, false).blocked).toBe(false)
      expect(evaluateLicenseGate(mit, true).blocked).toBe(false)
    })
  })
})
