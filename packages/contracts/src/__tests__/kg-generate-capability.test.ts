/**
 * Contract test for the KG-search `generate` capability kind + the LLM-floor S4
 * license gate (ADR 0135 KG-search Slice 2-foundation — the safe interim
 * generative GraphRAG: proposal-only / human-CP-gated / §2.8.4-firewall-bound).
 *
 * Same TS-canonical drift-detection discipline as `kg-capability.test.ts`: pin
 * the thin-CORE key set, the grounding-source shape, the proposal artifact's
 * taint label, and the S4 gate so a future edit that widens the CORE, drops the
 * taint, or drifts the floor off Apache/MIT FAILS at CI.
 *
 * Load-bearing assertions (the gates the dispatch names):
 *  - the `generate` CORE is exactly the thin set (no auto-action / no-tools field
 *    leaks in — the firewall is enforced by the worker sandbox + the consumer,
 *    NOT a CORE flag a caller could flip);
 *  - the proposal artifact is taint-`untrusted-derived` (a proposal, never an
 *    action — the §2.8.4 firewall obligation rides the artifact);
 *  - the S4 gate PASSES the local LLM floor Qwen2.5 / Mistral (Apache-2.0) +
 *    Phi-3 (MIT) and BLOCKS the Llama-community / CC-BY-NC arms fail-closed (the
 *    "don't drift the floor to Llama" rule — no permissive regression can ship).
 */

import { describe, it, expect } from 'vitest'

import {
  evaluateLicenseGate,
  type GenerateCore,
  type GenerationGroundingSource,
  type CapabilityCore,
  type TextArtifact,
  type Artifact,
  type LicenseComponent,
  type ProviderManifest,
} from '../capability.js'

describe('@harborline-software/api-contracts — KG `generate` capability kind (ADR 0135 Slice 2-foundation)', () => {
  // -------------------------------------------------------------------------
  // Thin-CORE discipline — generate = 4 fields (maxTokens optional)
  // -------------------------------------------------------------------------
  describe('per-capability thin CORE keys are EXACTLY the thin set', () => {
    it('GenerateCore has exactly the 4 fields, no more', () => {
      const core = {
        prompt: 'what did acme invoice in june?',
        grounding: [{ recordId: 'inv-1', text: 'Acme invoice June: $4,200', asserted: true }],
        maxTokens: 512,
        timeout: 120_000,
      } satisfies GenerateCore
      expect(Object.keys(core).sort()).toEqual(['grounding', 'maxTokens', 'prompt', 'timeout'].sort())
      expect(Object.keys(core)).toHaveLength(4)
      const asCore: CapabilityCore = core
      expect(asCore).toBeDefined()
    })

    it('GenerateCore.maxTokens is OPTIONAL (absent ⇒ provider default)', () => {
      const core = {
        prompt: 'summarize the lease',
        grounding: [],
        timeout: 60_000,
      } satisfies GenerateCore
      expect(core.maxTokens).toBeUndefined()
    })

    it('the CORE carries NO auto-action / no-tools / autonomy flag (firewall is the worker + consumer, not a CORE switch)', () => {
      const core = {
        prompt: 'p',
        grounding: [],
        timeout: 1000,
      } satisfies GenerateCore
      // A field a caller could flip to "let the model act" would be a CORE-level
      // bypass of the firewall. There is no such field — the proposal-only / no-
      // hands property is structural (the G-4 sandbox has no egress/tools), not a
      // toggle. Pin that the only keys are the thin grounded-generation set.
      for (const k of Object.keys(core)) {
        expect(k.toLowerCase()).not.toMatch(/action|autonom|tool|exec|send|apply|confirm/)
      }
    })
  })

  // -------------------------------------------------------------------------
  // Grounding source — authorized text + asserted/inferred provenance (§2.9 split)
  // -------------------------------------------------------------------------
  describe('grounding source carries authorized text + edge provenance', () => {
    it('a grounding source pins recordId + text + (optional) asserted', () => {
      const src: GenerationGroundingSource = {
        recordId: 'je-7',
        text: 'JE #7 posted $1,000 to rent income',
        asserted: true,
      }
      expect(src.recordId).toBe('je-7')
      expect(src.asserted).toBe(true)
    })

    it('asserted is OPTIONAL — absent ⇒ the conservative inferred default', () => {
      const src: GenerationGroundingSource = { recordId: 'r', text: 't' }
      // a consumer reads absent `asserted` as inferred (never authoritative for a guarded action)
      expect(src.asserted).toBeUndefined()
    })
  })

  // -------------------------------------------------------------------------
  // The proposal artifact — taint-labeled (the firewall obligation rides it)
  // -------------------------------------------------------------------------
  describe('a generate proposal artifact is taint-`untrusted-derived` (firewall)', () => {
    it('a TextArtifact can carry model provenance + the untrusted-derived taint', () => {
      const proposal: TextArtifact = {
        kind: 'text',
        text: 'Acme invoiced $4,200 in June (per invoice inv-1).',
        model: 'qwen2.5-7b-instruct',
        modelVersion: '1.0',
        taint: 'untrusted-derived',
      }
      const asArtifact: Artifact = proposal
      expect(asArtifact.kind).toBe('text')
      // the taint is what marks it a PROPOSAL re-entering the human path, never an action
      expect(proposal.taint).toBe('untrusted-derived')
      expect(proposal.model).toBe('qwen2.5-7b-instruct')
    })

    it('a plain `llm` TextArtifact (no taint) still satisfies the shape (backwards-compatible)', () => {
      const plain: TextArtifact = { kind: 'text', text: 'hello' }
      expect(plain.taint).toBeUndefined()
      expect(plain.model).toBeUndefined()
    })
  })

  // -------------------------------------------------------------------------
  // S4 license gate — the LLM floor must be Apache/MIT (Llama swap-only)
  // -------------------------------------------------------------------------
  describe('S4 license gate (the build-gate: Qwen2.5/Mistral Apache-2.0 + Phi-3 MIT ⇒ PASS; Llama/CC-BY-NC BLOCK)', () => {
    function llmManifestWith(
      engine: LicenseComponent | null,
      weights: LicenseComponent[],
    ): ProviderManifest {
      return {
        manifestVersion: 2,
        id: 'm',
        name: 'M',
        version: '1.0.0',
        kind: 'local',
        capability: ['generate'],
        engineLicense: engine,
        weightsLicense: weights,
        hardware: { 'min-cpu': { support: 'good', accel: 'cpu' } },
        tier: 'local',
        packaging: 'bundled',
        signing: 'n-a',
      }
    }

    it('PASSES Qwen2.5 — Apache-2.0 weights + permissive engine ⇒ commercial yes, not blocked', () => {
      const qwen = llmManifestWith(
        { role: 'engine', spdx: 'MIT', commercialUse: 'yes' }, // llama.cpp engine
        [{ role: 'weights', spdx: 'Apache-2.0', commercialUse: 'yes' }], // Qwen2.5 weights
      )
      const gate = evaluateLicenseGate(qwen)
      expect(gate.commercialUse).toBe('yes')
      expect(gate.blocked).toBe(false)
    })

    it('PASSES Phi-3 — MIT weights ⇒ commercial yes', () => {
      const phi3 = llmManifestWith({ role: 'engine', spdx: 'MIT', commercialUse: 'yes' }, [
        { role: 'weights', spdx: 'MIT', commercialUse: 'yes' },
      ])
      expect(evaluateLicenseGate(phi3).blocked).toBe(false)
    })

    it('BLOCKS a Llama-community-license floor fail-closed (the "don\'t drift the floor to Llama" rule)', () => {
      // Llama 3.1 Community License is NOT OSI-permissive (AUP + competitor
      // restriction) — declared `no` on the weights arm; the MIN drops to `no`
      // and BLOCKS it as a FLOOR (it may be a swap-only accelerator behind the
      // attestation override, never the bundled floor).
      const llama = llmManifestWith({ role: 'engine', spdx: 'MIT', commercialUse: 'yes' }, [
        { role: 'weights', spdx: 'LLAMA-3.1-Community', commercialUse: 'no', restrictions: 'community-license' },
      ])
      const gate = evaluateLicenseGate(llama)
      expect(gate.commercialUse).toBe('no')
      expect(gate.blocked).toBe(true)
    })

    it('BLOCKS a CC-BY-NC LLM weights arm fail-closed (no non-commercial generation floor)', () => {
      const nc = llmManifestWith({ role: 'engine', spdx: 'MIT', commercialUse: 'yes' }, [
        { role: 'weights', spdx: 'CC-BY-NC-4.0', commercialUse: 'no', restrictions: 'non-commercial' },
      ])
      expect(evaluateLicenseGate(nc).blocked).toBe(true)
    })
  })
})
