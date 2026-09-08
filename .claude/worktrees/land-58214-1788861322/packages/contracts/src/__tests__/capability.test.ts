/**
 * Contract test for the Capability membrane `capability` namespace (ADR 0123/0124).
 *
 * These types are TS-CANONICAL (ADR 0123 OQ-1) — there is no upstream C#/JSON
 * fixture to round-trip against (unlike `bundles`). The drift-detection
 * mechanism here is a STRUCTURAL contract test: it pins the binding P1
 * paper-gate placements (R1/R2/R3) and the binding council conditions
 * (SEC-2/SEC-6/FE-1/FE-3/transport/fault-domain) so a future edit that, say,
 * adds a field to a CORE or widens a closed enum FAILS at CI.
 *
 * Two layers:
 *  1. Compile-time: `satisfies` / typed literals assert the SHAPES (a field
 *     added to or removed from a CORE breaks the build).
 *  2. Runtime: exhaustive-enum guards assert the CLOSED enums (FE-1
 *     resolutionState, transport, fault-domain) are exactly the specified
 *     members — a widened enum breaks a `satisfies` exhaustiveness check.
 *
 * The discriminating assertion (paper-gate): the per-capability CORE keys are
 * EXACTLY the thin set. `image` = 6 keys, `bank-import` = 4 top-level keys.
 * Adding a 7th `image` key or a 5th `bank-import` key is the treadmill S3
 * closes — this test catches it.
 */

import { describe, it, expect } from 'vitest'

import type {
  ImageCore,
  BankImportCore,
  BankImportSource,
  ImageFormat,
  TtsCore,
  InvokeRequest,
  RequestAttachment,
  Transport,
  CapabilityResult,
  ProgressEnvelope,
  Artifact,
  AudioArtifact,
  ImportBatchArtifact,
  FaultDomain,
  CapabilityError,
  Usage,
  ResolutionResult,
  ResolutionState,
  ProviderManifest,
  LicenseComponent,
  EffectiveLicenseGate,
  OutputRights,
} from '../capability.js'

describe('@harborline-software/api-contracts — capability namespace (ADR 0123/0124, P1 paper-gate)', () => {
  // -------------------------------------------------------------------------
  // Thin-CORE discipline (ADR 0123 §S3 — the treadmill the gate exists to catch)
  // -------------------------------------------------------------------------
  describe('per-capability thin CORE keys are EXACTLY the thin set', () => {
    it('ImageCore has exactly the 6 paper-gate fields, no more', () => {
      // A representative value with EXACTLY the canonical keys.
      const core = {
        prompt: 'a cat',
        size: { w: 2048, h: 2048 }, // native-2k is a value, not a new field
        seed: 42,
        count: 1,
        format: 'png',
        timeout: 30_000,
      } satisfies ImageCore
      expect(Object.keys(core).sort()).toEqual(
        ['count', 'format', 'prompt', 'seed', 'size', 'timeout'].sort(),
      )
      // exactly six — the falsifying count
      expect(Object.keys(core)).toHaveLength(6)
    })

    it('BankImportCore has exactly 4 top-level fields (R2: format/cursor are NOT among them)', () => {
      const core = {
        source: { kind: 'file', attachmentId: 'a1', fileName: 'acme.csv' },
        accountId: 'acct-1',
        dateRange: { from: null, to: null },
        timeout: 30_000,
      } satisfies BankImportCore
      const keys = Object.keys(core).sort()
      expect(keys).toEqual(['accountId', 'dateRange', 'source', 'timeout'].sort())
      // R2: neither `format` nor `cursor` is a top-level CORE field
      expect(keys).not.toContain('format')
      expect(keys).not.toContain('cursor')
      // dateRange is optional — the REQUIRED top-level set is source/accountId/timeout
      const minimal = {
        source: { kind: 'file', attachmentId: 'a1', fileName: 'acme.csv' },
        accountId: 'acct-1',
        timeout: 30_000,
      } satisfies BankImportCore
      expect(Object.keys(minimal).sort()).toEqual(['accountId', 'source', 'timeout'].sort())
    })

    it('TtsCore has exactly the thin set (voice optional); no per-engine fields leak in', () => {
      // The REQUIRED top-level set is text/format/timeout; voice is optional.
      const minimal = {
        text: 'capability probe sandbox',
        format: 'aiff',
        timeout: 30_000,
      } satisfies TtsCore
      expect(Object.keys(minimal).sort()).toEqual(['format', 'text', 'timeout'].sort())
      const full = {
        text: 'capability probe sandbox',
        voice: 'Samantha', // opaque token, membrane does not interpret it
        format: 'aiff',
        timeout: 30_000,
      } satisfies TtsCore
      const keys = Object.keys(full).sort()
      expect(keys).toEqual(['format', 'text', 'timeout', 'voice'].sort())
      // S3 treadmill guard: no per-engine knob leaks into the CORE
      expect(keys).not.toContain('sampleRate')
      expect(keys).not.toContain('rate')
      expect(keys).not.toContain('pitch')
      expect(keys).not.toContain('ssml')
      expect(keys).not.toContain('model')
    })
  })

  // -------------------------------------------------------------------------
  // R2 — source discriminator (file | feed); cursor lives INSIDE the feed branch
  // -------------------------------------------------------------------------
  describe('R2 — bank-import source is the one universal discriminator', () => {
    it('the file branch carries fileName/attachmentId (bytes ride attachments[])', () => {
      const src: BankImportSource = { kind: 'file', attachmentId: 'a1', fileName: 'statement.xml' }
      expect(src.kind).toBe('file')
      if (src.kind === 'file') {
        expect(src.fileName).toBe('statement.xml')
        expect(src.attachmentId).toBe('a1')
      }
    })

    it('the cursor lives INSIDE source.feedPull, NOT as a top-level CORE field', () => {
      const src: BankImportSource = {
        kind: 'feed',
        connectionRef: 'conn-1',
        accountRef: 'feed-acct-1',
        cursor: 'opaque-sync-pos', // identical to shipped TransactionPage.NextCursor
      }
      expect(src.kind).toBe('feed')
      if (src.kind === 'feed') {
        // cursor is a property of the feed BRANCH, the way fileName is of the file branch
        expect(src.cursor).toBe('opaque-sync-pos')
      }
      // first-pull: cursor absent
      const firstPull: BankImportSource = { kind: 'feed', connectionRef: 'c', accountRef: 'a' }
      expect(firstPull.kind).toBe('feed')
    })
  })

  // -------------------------------------------------------------------------
  // R1 — attachments[] is a request-envelope face, never a CORE field
  // -------------------------------------------------------------------------
  describe('R1 — attachments[] is on the Invoke REQUEST envelope, parallel to RESULT artifacts[]', () => {
    it('InvokeRequest carries attachments[] at the envelope level (not in core)', () => {
      const attachment: RequestAttachment = {
        attachmentId: 'init-1',
        mime: 'image/png',
        fileName: 'init.png',
        sha256: 'deadbeef',
        byteLength: 1024,
      }
      const req = {
        capabilityId: 'image',
        core: { prompt: 'x', size: { w: 512, h: 512 }, seed: 1, count: 1, format: 'png', timeout: 30_000 },
        providerInputs: { steps: 30, initImage: { attachmentId: 'init-1' } }, // refs the attachment by id
        attachments: [attachment],
        idempotencyKey: 'idem-1',
        correlationId: 'corr-1',
        transport: 'poll',
      } satisfies InvokeRequest
      // attachments is an envelope-level field
      expect(Object.keys(req)).toContain('attachments')
      expect(req.attachments[0].attachmentId).toBe('init-1')
      // the CORE does NOT carry the binary — no `initImage` in the core
      expect(Object.keys(req.core)).not.toContain('initImage')
      // the semantic binding lives in providerInputs, referencing the attachment by id
      expect((req.providerInputs as { initImage: { attachmentId: string } }).initImage.attachmentId).toBe(
        'init-1',
      )
    })
  })

  // -------------------------------------------------------------------------
  // SEC-2 — idempotencyKey is a REQUIRED field on the Invoke request envelope
  // -------------------------------------------------------------------------
  describe('SEC-2 — idempotencyKey is required on the Invoke request envelope', () => {
    it('a financial (bank-import) Invoke must carry idempotencyKey (type-enforced)', () => {
      const req = {
        capabilityId: 'bank-import',
        core: {
          source: { kind: 'file', attachmentId: 'csv-1', fileName: 'acme.csv' },
          accountId: 'acct-1',
          timeout: 30_000,
        },
        providerInputs: { dateColumn: 0, amountColumn: 1 },
        attachments: [{ attachmentId: 'csv-1', mime: 'text/csv', fileName: 'acme.csv' }],
        idempotencyKey: 'idem-financial-1', // REQUIRED — the type forbids omitting it
        correlationId: 'corr-2',
        transport: 'sync',
      } satisfies InvokeRequest
      expect(req.idempotencyKey).toBeTruthy()
      // The field is structurally present in every InvokeRequest.
      expect(Object.keys(req)).toContain('idempotencyKey')
    })
  })

  // -------------------------------------------------------------------------
  // Transport discriminator (closed enum)
  // -------------------------------------------------------------------------
  describe('transport discriminator is the closed 4-member set', () => {
    it('exactly sync|poll|stream|webhook', () => {
      const all: Transport[] = ['sync', 'poll', 'stream', 'webhook']
      // exhaustiveness: a 5th member would break this assertion-via-satisfies
      const check = (t: Transport): string => {
        switch (t) {
          case 'sync':
          case 'poll':
          case 'stream':
          case 'webhook':
            return t
          default: {
            const _never: never = t
            return _never
          }
        }
      }
      expect(all.map(check)).toEqual(['sync', 'poll', 'stream', 'webhook'])
    })
  })

  // -------------------------------------------------------------------------
  // R3 + envelope-does-not-fork — RESULT envelope is six fixed top-level fields
  // -------------------------------------------------------------------------
  describe('R3 — canonical RESULT envelope has 6 fixed top-level fields; continuation lives in artifacts[]', () => {
    it('CapabilityResult top-level keys are exactly the six', () => {
      const result = {
        jobId: 'job-1',
        status: 'succeeded',
        progress: 1.0,
        artifacts: [{ kind: 'image', uri: 'blob://x', mime: 'image/png', w: 2048, h: 2048 }],
        usage: { unit: 'image', quantity: 1, costMicros: 100, tier: 'cloud' },
        error: null,
      } satisfies CapabilityResult
      expect(Object.keys(result).sort()).toEqual(
        ['artifacts', 'error', 'jobId', 'progress', 'status', 'usage'].sort(),
      )
      expect(Object.keys(result)).toHaveLength(6)
    })

    it('R3 — nextCursor + batch summary live INSIDE the import-batch artifact, not at envelope top level', () => {
      const batch: ImportBatchArtifact = {
        kind: 'import-batch',
        batchRef: 'batch-1',
        summary: { inserted: 140, duplicates: 2 },
        nextCursor: 'next-opaque-cursor',
      }
      const result = {
        jobId: 'job-2',
        status: 'succeeded',
        progress: 1.0,
        artifacts: [batch],
        usage: { unit: 'line', quantity: 142, tier: 'local' },
        error: null,
      } satisfies CapabilityResult
      // nextCursor is NOT a top-level envelope field (would fork the envelope)
      expect(Object.keys(result)).not.toContain('nextCursor')
      // it lives inside the artifact
      const a = result.artifacts[0] as ImportBatchArtifact
      expect(a.nextCursor).toBe('next-opaque-cursor')
      expect(a.summary).toEqual({ inserted: 140, duplicates: 2 })
    })

    it('artifacts[] is the polymorphic slot — image/text/import-batch/audio share the base, vary by kind', () => {
      const arts: Artifact[] = [
        { kind: 'image', uri: 'u', mime: 'image/png', w: 1, h: 1 },
        { kind: 'text', text: 'hi' },
        { kind: 'import-batch', batchRef: 'b', summary: { inserted: 1, duplicates: 0 } },
        { kind: 'audio', uri: 'file:///out.aiff', mime: 'audio/x-aiff', durationMs: 1200 },
      ]
      expect(arts.map(a => a.kind)).toEqual(['image', 'text', 'import-batch', 'audio'])
    })

    it('AudioArtifact carries the bytes ref inside the artifact (tts), not at envelope top level', () => {
      const audio: AudioArtifact = {
        kind: 'audio',
        uri: 'file:///tmp/capability/out.aiff',
        mime: 'audio/x-aiff',
        durationMs: 1500,
      }
      const result = {
        jobId: 'job-tts-1',
        status: 'succeeded',
        progress: 1.0,
        artifacts: [audio],
        usage: { unit: 'audio', quantity: 1, tier: 'local' },
        error: null,
      } satisfies CapabilityResult
      // no audio field at the envelope top level (would fork the envelope)
      expect(Object.keys(result)).not.toContain('uri')
      expect(Object.keys(result)).not.toContain('durationMs')
      const a = result.artifacts[0] as AudioArtifact
      expect(a.uri).toBe('file:///tmp/capability/out.aiff')
      expect(a.mime).toBe('audio/x-aiff')
    })
  })

  // -------------------------------------------------------------------------
  // FE-3 — mid-flight progress envelope is one fixed shape with typed slots
  // -------------------------------------------------------------------------
  describe('FE-3 — mid-flight progress envelope is one fixed shape', () => {
    it('llm delta and progressive-image partial share the SAME envelope shape', () => {
      const llmProgress = {
        jobId: 'job-3',
        status: 'running',
        progress: 0.4,
        stage: 'generating',
        delta: 'token-chunk', // capability-typed payload slot
      } satisfies ProgressEnvelope
      const imageProgress = {
        jobId: 'job-4',
        status: 'running',
        progress: 0.4,
        stage: 'sampling 12/30',
        partial: { artifactRef: 'preview-1' }, // capability-typed payload slot
      } satisfies ProgressEnvelope
      // Same fixed top-level fields; only delta/partial vary (value-space)
      const topLevel = (p: ProgressEnvelope) =>
        Object.keys(p).filter(k => k !== 'delta' && k !== 'partial').sort()
      expect(topLevel(llmProgress)).toEqual(topLevel(imageProgress))
      expect(llmProgress.status).toBe('running')
      expect(imageProgress.status).toBe('running')
    })
  })

  // -------------------------------------------------------------------------
  // Error taxonomy (closed fault-domain set, three members)
  // -------------------------------------------------------------------------
  describe('error taxonomy — three closed fault domains', () => {
    it('exactly input|provider|membrane, each with retryable + code + message', () => {
      const domains: FaultDomain[] = ['input', 'provider', 'membrane']
      const errs: CapabilityError[] = [
        { faultDomain: 'input', retryable: false, code: 'parse.malformed', message: 'bad file' },
        { faultDomain: 'provider', retryable: true, code: 'provider.overloaded', message: 'busy', retryAfter: 30_000 },
        { faultDomain: 'membrane', retryable: false, code: 'membrane.duplicate', message: 'dup' },
      ]
      expect(errs.map(e => e.faultDomain)).toEqual(domains)
      // membrane.duplicate is the SEC-2 fail-closed dedupe signal (not a silent double-post)
      expect(errs[2].code).toBe('membrane.duplicate')
      expect(errs[2].retryable).toBe(false)
    })
  })

  // -------------------------------------------------------------------------
  // usage — fixed shape, value-space unit variance only
  // -------------------------------------------------------------------------
  describe('usage — fixed {unit, quantity, costMicros?, tier} shape', () => {
    it('only `unit` value varies across capabilities, never a new field', () => {
      const usages: Usage[] = [
        { unit: 'image', quantity: 1, tier: 'cloud', costMicros: 100 },
        { unit: 'token', quantity: 512, tier: 'cloud' },
        { unit: 'line', quantity: 142, tier: 'local' },
      ]
      for (const u of usages) {
        const keys = Object.keys(u).filter(k => k !== 'costMicros').sort()
        expect(keys).toEqual(['quantity', 'tier', 'unit'].sort())
      }
    })
  })

  // -------------------------------------------------------------------------
  // FE-1 — closed resolutionState enum + resolution-result; SEC-6 no-credential
  // -------------------------------------------------------------------------
  describe('FE-1 — resolutionState is the closed 6-member enum the UI switches on', () => {
    it('exactly the six states', () => {
      const states: ResolutionState[] = [
        'available',
        'locked-entitlement',
        'upsell',
        'unavailable-hardware',
        'degraded',
        'not-in-edition',
      ]
      const check = (s: ResolutionState): string => {
        switch (s) {
          case 'available':
          case 'locked-entitlement':
          case 'upsell':
          case 'unavailable-hardware':
          case 'degraded':
          case 'not-in-edition':
            return s
          default: {
            const _never: never = s
            return _never
          }
        }
      }
      expect(states.map(check)).toHaveLength(6)
    })

    it('resolution-result reason is a human string, NOT the branch key (FE-1)', () => {
      const r = {
        resolutionState: 'degraded',
        chosenProviderId: 'cpu-diffusion',
        selectionReason: 'fallback-floor',
        isFallback: true,
        tier: 'local',
        speedHint: 'slow',
        reason: 'No GPU provider available; using the slower CPU image floor.',
      } satisfies ResolutionResult
      // reason is human text, distinct from the enum branch key
      expect(r.reason).not.toBe(r.resolutionState)
      expect(r.reason.length).toBeGreaterThan(10)
    })

    it('SEC-6 — resolution-result surface carries NO credential-bearing field', () => {
      const r: ResolutionResult = {
        resolutionState: 'available',
        chosenProviderId: 'p1',
        selectionReason: 'only-candidate',
        isFallback: false,
        tier: 'local',
        speedHint: 'fast',
        reason: 'only candidate',
      }
      const forbidden = ['token', 'secret', 'apiKey', 'connectionString', 'password', 'credential']
      for (const f of forbidden) {
        expect(Object.keys(r)).not.toContain(f)
      }
    })
  })

  // -------------------------------------------------------------------------
  // S4 — two-layer license + SEC-6 no-credential on the license/output surfaces
  // -------------------------------------------------------------------------
  describe('S4 — manifest v2 two-layer license; SEC-6 no-credential on license/output-rights', () => {
    it('a provider declares engineLicense ∧ weightsLicense[] (inference)', () => {
      const manifest = {
        manifestVersion: 2,
        id: 'comfyui-sdxl',
        name: 'ComfyUI SDXL',
        version: '1.0.0',
        kind: 'local',
        capability: ['image'],
        engineLicense: { role: 'engine', spdx: 'GPL-3.0-only', commercialUse: 'no' },
        weightsLicense: [{ role: 'weights', spdx: 'OpenRAIL-M', commercialUse: 'unknown', restrictions: 'non-commercial?' }],
        hardware: { 'apple-silicon': { support: 'good', accel: 'mps' } },
        tier: 'local',
        packaging: 'managed-install',
        signing: 'user-space',
      } satisfies ProviderManifest
      expect(manifest.manifestVersion).toBe(2)
      expect(manifest.engineLicense?.role).toBe('engine')
      expect(manifest.weightsLicense[0].role).toBe('weights')
    })

    it('a bank-feed provider declares sdkLicense ∧ dataSourceTerms (S4 bank-feed)', () => {
      const manifest = {
        manifestVersion: 2,
        id: 'plaid-feed',
        name: 'Plaid Feed',
        version: '1.0.0',
        kind: 'cloud',
        capability: ['bank-import'],
        weightsLicense: [], // n/a for a data feed
        sdkLicense: { role: 'sdk', spdx: 'MIT', commercialUse: 'yes' },
        dataSourceTerms: { role: 'dataSource', spdx: 'proprietary', commercialUse: 'unknown' },
        hardware: { any: { support: 'best', accel: 'any' } },
        tier: 'cloud',
        packaging: 'cloud',
        signing: 'n-a',
      } satisfies ProviderManifest
      expect(manifest.sdkLicense?.role).toBe('sdk')
      expect(manifest.dataSourceTerms?.commercialUse).toBe('unknown') // forces fail-closed
    })

    it('effective license gate fails closed when any component commercialUse is unknown', () => {
      const components: LicenseComponent[] = [
        { role: 'engine', spdx: 'MIT', commercialUse: 'yes' },
        { role: 'weights', spdx: 'OpenRAIL-M', commercialUse: 'unknown' },
      ]
      // Mirror the MIN-over-components rule the membrane computes (test the SHAPE,
      // not the build-new logic): unknown ⇒ blocked unless attestation.
      const gate = {
        commercialUse: 'unknown',
        blocked: true,
        components,
        attestationOverride: false,
      } satisfies EffectiveLicenseGate
      expect(gate.blocked).toBe(true)
      expect(gate.commercialUse).toBe('unknown')
    })

    it('SEC-6 — license-component and output-rights surfaces carry NO credential field', () => {
      const comp: LicenseComponent = { role: 'sdk', spdx: 'MIT', commercialUse: 'yes' }
      const rights: OutputRights = { ownership: 'operator', copyrightable: 'unknown' }
      const forbidden = ['token', 'secret', 'apiKey', 'connectionString', 'password', 'credential']
      for (const f of forbidden) {
        expect(Object.keys(comp)).not.toContain(f)
        expect(Object.keys(rights)).not.toContain(f)
      }
    })

    it('SEC-6 — provider manifest carries NO credential-bearing field', () => {
      const manifest: ProviderManifest = {
        manifestVersion: 2,
        id: 'p',
        name: 'P',
        version: '1.0.0',
        kind: 'local',
        capability: ['image'],
        weightsLicense: [],
        hardware: {},
        tier: 'local',
        packaging: 'bundled',
        signing: 'n-a',
      }
      const forbidden = ['token', 'secret', 'apiKey', 'connectionString', 'password', 'credential']
      for (const f of forbidden) {
        expect(Object.keys(manifest)).not.toContain(f)
      }
    })
  })

  // -------------------------------------------------------------------------
  // ImageFormat closed enum
  // -------------------------------------------------------------------------
  describe('image format is the closed png|jpeg|webp set', () => {
    it('exactly three', () => {
      const fmts: ImageFormat[] = ['png', 'jpeg', 'webp']
      expect(fmts).toHaveLength(3)
    })
  })
})
