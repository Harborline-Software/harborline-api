/**
 * The Capability KG-search EMBEDDING + RERANK runtime — the G-4 sandboxed inference
 * worker (ADR 0123 amendment 2026-06-24 §S6/§S7; ADR 0135 F3-lift Slice 1 vector
 * tier; security gate G-4, council-verdict-security-engineering-2026-06-24T1050Z).
 *
 * THE CRUX of Slice 1a. This runtime hosts the `embeddings` + `rerank` capability
 * kinds by driving a REAL Python worker (`kg_embed.py` running BGE-M3 /
 * bge-reranker-v2-m3 on the CPU floor) CONFINED by the OS-native S7 sandbox. The
 * worker reads DECRYPTED record text (the embedding input) — plaintext financial /
 * PII — and ALSO ingests attacker-controllable content (a poisoned invoice/email).
 * So its confinement is the load-bearing security property:
 *
 *   G-4 — the bundled worker does NOT auto-inherit S7 (S7's trigger is
 *   *downloadable* code; this worker is bundled + digest-pinned, on the
 *   trusted-the-build side, exactly where Piper/whisper.cpp/the image floor sit
 *   today). So the S7 confinement is a NEW, EXPLICIT obligation carved in here, NOT
 *   an assumed inheritance:
 *     (a) full SEC-7(a/b/c) — no keychain/credential/seed/DEK reach (the credential
 *         floor + the SEC-7 subtree-credential guard bind every spec this runtime
 *         builds), fs-confined to a per-Invoke work dir, NO network egress;
 *         no-DEK-reach is the line that keeps the no-mock-crypto discipline intact
 *         (the worker reads ALREADY-DECRYPTED text handed to it; it holds NO key
 *         and touches no key-distribution resolver);
 *     (b) the confinement is NON-VACUOUSLY arch-tested (see
 *         `kg-embedding-runtime.test.ts` + the sandbox conformance suite): the
 *         arch-test FAILS if a credential-reaching grant is planted, and a runtime
 *         fail-closed test proves the worker cannot read a planted DEK or open a
 *         socket;
 *     (c) no-egress is ASSERTED, not assumed — `allowedEgress: []`. A poisoned
 *         invoice the worker embeds cannot exfiltrate: a no-tools, no-egress,
 *         credential-fenced CPU embedder can only emit a float vector.
 *
 * The runtime NEVER spawns the worker directly — every spawn goes through the
 * injected `Sandbox`. A runtime that spawned the worker UNCONFINED would defeat
 * G-4; this runtime CANNOT (it has no direct `spawn` of the worker).
 *
 * GRACEFUL DEGRADATION (ADR 0132 ship-time-vs-runtime floor split): the real
 * worker needs the cached models + a torch/transformers interpreter AND an explicit
 * opt-in (`CAPABILITY_HOST_KG_EMBED_REAL=1` — real CPU inference is hundreds of ms to seconds,
 * too slow for CI). When unavailable OR not opted-in, the runtime returns a
 * DETERMINISTIC stub embedding/score — the SAME envelope shape, so CI stays fast
 * and green and the contract is exercised without the heavy model. Slice 1a is the
 * inference BOUNDARY: NO sqlite-vec / index (Slice 1b), NO generation (Slice 2).
 */

import {
  copyFileSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { basename, dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import type {
  CancelRequest,
  CapabilityResult,
  EmbeddingsArtifact,
  EmbeddingsCore,
  HealthProbe,
  HealthState,
  InvokeRequest,
  NegotiateOffer,
  ProviderManifest,
  RerankArtifact,
  RerankCore,
  RerankScore,
} from '@harborline-software/api-contracts'

import type { RuntimeManifest } from '../membrane/announce.js'
import type { ProbeKind } from '../membrane/observe.js'
import { createSandbox, type Sandbox, type SandboxSpec } from '../sandbox/index.js'
import {
  BGE_M3_DIMENSION,
  BGE_M3_FLOOR,
  BGE_RERANKER_V2_M3_FLOOR,
  STUB_EMBED_MODEL,
  STUB_RERANK_MODEL,
  type KgEmbedPaths,
  type KgModelFloor,
  kgModelsPresent,
  realKgEmbedOptedIn,
  resolveKgEmbedPaths,
} from './kg-embed-model.js'
import { CAPABILITY_CONTRACT_VERSION } from './reference-image-runtime.js'
import { resolvePythonOperationalEnvironmentHelper } from './operational-environment.js'
import type { RuntimeHost } from './runtime-host.js'

/** The KG-embed runtime's stable id. */
export const KG_EMBED_RUNTIME_ID = 'kg-embed-runtime'

/** The `embeddings` capability schema version the runtime hosts. */
export const EMBEDDINGS_SCHEMA_VERSION = '1.0.0'
/** The `rerank` capability schema version the runtime hosts. */
export const RERANK_SCHEMA_VERSION = '1.0.0'

/** The default Invoke deadline when a CORE under-specifies (real CPU inference is slow). */
const DEFAULT_TIMEOUT_MS = 120_000

/** Locate the vendored `kg_embed.py` — sibling of this module in BOTH `src/` (vitest) and `dist/` (built). */
export function resolveKgEmbedScript(): string | null {
  const here = dirname(fileURLToPath(import.meta.url))
  const candidates = [
    join(here, 'kg_embed.py'), // vendored sibling (dist/runtime + src/runtime)
    join(here, '..', '..', 'src', 'runtime', 'kg_embed.py'), // dist → source fallback
  ]
  for (const c of candidates) {
    if (existsSync(c)) return c
  }
  return null
}

/** Build the provider manifest for a KG model floor (SEC-6: no credential field; S4: MIT). */
function kgProviderManifest(floor: KgModelFloor): ProviderManifest {
  return {
    manifestVersion: 2,
    id: floor.id,
    name: floor.name,
    version: '0.0.0',
    kind: 'local',
    capability: [floor.capabilityId],
    inputSchemaRef: null,
    // S4: BOTH components are commercial-`yes` (engine Apache-2.0, weights MIT) —
    // the S4 gate (`evaluateLicenseGate`) returns `yes`, not blocked.
    engineLicense: floor.engineLicense,
    weightsLicense: [floor.weightsLicense],
    hardware: { 'min-cpu': { support: 'good', accel: 'cpu' } },
    tier: 'local',
    packaging: 'bundled',
    signing: 'user-space',
    // The model produces a vector / score; the operator owns the derived index.
    outputRights: { ownership: 'operator', copyrightable: 'no' },
    flavor: 'cpu-floor',
  }
}

/** Options for the KG-embed runtime (injectable sandbox + paths for tests/cross-platform). */
export interface KgEmbedRuntimeOptions {
  /** The OS-native sandbox the worker is confined by (defaults to the host's). */
  sandbox?: Sandbox
  /** The resolved model/interpreter paths (defaults to the env-resolved set). */
  paths?: KgEmbedPaths
  /**
   * Credential/DEK store paths to confine against (forwarded to the sandbox spec's
   * denyPaths — the platform's default credential floor is always merged on top).
   * Tests inject a planted-secret path here to PROVE the no-reach contract (G-4).
   */
  denyPaths?: readonly string[]
  /**
   * Force-enable the real worker even without the `CAPABILITY_HOST_KG_EMBED_REAL` opt-in
   * (tests inject a stub sandbox to exercise the real spawn path fast).
   */
  forceReal?: boolean
}

/**
 * The KG-embed runtime. Hosts `embeddings` + `rerank`; when armed every Invoke
 * spawns `kg_embed.py` as a REAL subprocess CONFINED by the OS-native sandbox
 * (G-4), reads the produced result JSON, and returns it on the uniform envelope.
 * When NOT armed it returns a deterministic stub (graceful degradation).
 */
export class KgEmbedRuntime implements RuntimeHost {
  private readonly sandbox: Sandbox
  private readonly paths: KgEmbedPaths
  private readonly denyPaths: readonly string[]
  private readonly armed: boolean
  private degraded = false
  /** jobIds the membrane has asked to cancel (observable for tests). */
  readonly cancelledJobs: string[] = []
  /** The sandbox specs this runtime actually ran (observable for tests — the G-4 proof surface). */
  readonly ranSpecs: SandboxSpec[] = []

  constructor(options: KgEmbedRuntimeOptions = {}) {
    this.sandbox = options.sandbox ?? createSandbox()
    this.paths = options.paths ?? resolveKgEmbedPaths()
    this.denyPaths = options.denyPaths ?? []
    this.armed =
      options.forceReal === true || (realKgEmbedOptedIn() && kgModelsPresent(this.paths))
  }

  /** Whether this runtime will run the real worker (vs. degrade to the deterministic stub). */
  get usesRealEngine(): boolean {
    return this.armed
  }

  /** Force the next health probe to report `degraded`. */
  setDegraded(degraded: boolean): void {
    this.degraded = degraded
  }

  announce(): RuntimeManifest {
    return {
      runtimeId: KG_EMBED_RUNTIME_ID,
      name: 'Capability KG Embedding + Rerank Runtime',
      contractVersion: CAPABILITY_CONTRACT_VERSION,
      capabilities: [
        {
          capabilityId: 'embeddings',
          schemaVersion: EMBEDDINGS_SCHEMA_VERSION,
          providers: [kgProviderManifest(BGE_M3_FLOOR)],
        },
        {
          capabilityId: 'rerank',
          schemaVersion: RERANK_SCHEMA_VERSION,
          providers: [kgProviderManifest(BGE_RERANKER_V2_M3_FLOOR)],
        },
      ],
    }
  }

  negotiateOffer(): NegotiateOffer {
    return {
      contractVersion: CAPABILITY_CONTRACT_VERSION,
      capabilitySchemaVersions: {
        embeddings: EMBEDDINGS_SCHEMA_VERSION,
        rerank: RERANK_SCHEMA_VERSION,
      },
    }
  }

  health(kind: ProbeKind): HealthProbe {
    const state: HealthState = this.degraded ? 'degraded' : 'up'
    const detail = this.degraded
      ? 'kg-embed throttled'
      : this.armed
        ? 'ready (BGE-M3 + bge-reranker-v2-m3 CPU floor)'
        : 'ready (deterministic stub — real CPU floor unavailable/not opted-in)'
    return { kind, state, detail }
  }

  async invoke(request: InvokeRequest): Promise<CapabilityResult> {
    const jobId = `job:${KG_EMBED_RUNTIME_ID}:${request.correlationId}`

    if (request.capabilityId === 'embeddings') {
      return this.invokeEmbeddings(jobId, request.core as EmbeddingsCore)
    }
    if (request.capabilityId === 'rerank') {
      return this.invokeRerank(jobId, request.core as RerankCore)
    }
    return failure(jobId, {
      faultDomain: 'input',
      retryable: false,
      code: 'input.capability_not_hosted',
      message: `kg-embed runtime hosts only 'embeddings'/'rerank', not '${request.capabilityId}'`,
    })
  }

  private async invokeEmbeddings(
    jobId: string,
    core: EmbeddingsCore,
  ): Promise<CapabilityResult> {
    const dimension = typeof core.dimension === 'number' ? core.dimension : BGE_M3_DIMENSION
    // GRACEFUL DEGRADATION: not armed → deterministic stub vectors (same envelope) tagged with the
    // SELF-IDENTIFYING stub model (bug-1358) so a non-opted-in host can never pin a stub as genuine bge-m3.
    if (!this.armed) {
      const vectors = core.texts.map((t) => stubVector(t, dimension))
      return embeddingsResult(jobId, vectors, dimension, core.texts.length, STUB_EMBED_MODEL, 'stub')
    }
    const result = await this.runWorker(jobId, core.timeout, {
      task: 'embed',
      texts: core.texts,
      dimension,
    })
    if ('error' in result) return result.error
    const vectors = (result.json.vectors as number[][]) ?? []
    // Assert the declared dimension (G-5 — a wrong-width vector is a fault, never
    // a silently-mixed index).
    for (const v of vectors) {
      if (v.length !== dimension) {
        return failure(jobId, {
          faultDomain: 'provider',
          retryable: false,
          code: 'provider.embed_dimension_mismatch',
          message: `worker returned ${v.length}-dim vector, declared ${dimension}`,
        })
      }
    }
    // The REAL worker output rides the genuine floor id (G-5 — the only path that yields a `bge-m3`-tagged
    // artifact destined for the durable index).
    return embeddingsResult(
      jobId,
      vectors,
      dimension,
      core.texts.length,
      BGE_M3_FLOOR.id,
      BGE_M3_FLOOR.modelVersion,
    )
  }

  private async invokeRerank(jobId: string, core: RerankCore): Promise<CapabilityResult> {
    // GRACEFUL DEGRADATION: not armed → deterministic stub scores (same envelope) tagged with the
    // SELF-IDENTIFYING stub model (bug-1358) — a stub rerank can never pass as the real cross-encoder.
    if (!this.armed) {
      const scored = stubScores(core.query, core.documents, core.topK ?? null)
      return rerankResult(jobId, scored, core.documents.length, STUB_RERANK_MODEL, 'stub')
    }
    const result = await this.runWorker(jobId, core.timeout, {
      task: 'rerank',
      query: core.query,
      documents: core.documents,
      topK: core.topK ?? null,
    })
    if ('error' in result) return result.error
    const scored = (result.json.scored as RerankScore[]) ?? []
    return rerankResult(
      jobId,
      scored,
      core.documents.length,
      BGE_RERANKER_V2_M3_FLOOR.id,
      BGE_RERANKER_V2_M3_FLOOR.modelVersion,
    )
  }

  /**
   * Spawn `kg_embed.py` CONFINED by the sandbox for one job. Builds the G-4 spec
   * (no egress, write-only work dir, credential floor + injected secrets denied,
   * read-only the HF cache + interpreter root — NONE a credential store, so the
   * SEC-7 build-time guard accepts the grant), writes the job JSON, runs, reads
   * the result JSON. Returns either `{ json }` on success or `{ error }` (the
   * uniform failed envelope) — never throws past the sandbox boundary.
   */
  private async runWorker(
    jobId: string,
    timeout: unknown,
    job: Record<string, unknown>,
  ): Promise<{ json: Record<string, unknown> } | { error: CapabilityResult }> {
    const script = resolveKgEmbedScript()
    if (script == null) {
      return {
        error: failure(jobId, {
          faultDomain: 'membrane',
          retryable: false,
          code: 'membrane.kg_worker_unavailable',
          message: 'kg_embed.py worker script not found',
        }),
      }
    }
    // A per-Invoke work dir is the ONLY path the confined worker may write.
    const workDir = mkdtempSync(join(tmpdir(), 'kg-embed-'))
    const scriptPath = join(workDir, basename(script))
    const environmentHelper = resolvePythonOperationalEnvironmentHelper()
    const jobPath = join(workDir, 'job.json')
    const outPath = join(workDir, 'result.json')
    const scratch = join(workDir, 'scratch')

    const spec = this.buildSpec(script, scriptPath, jobPath, outPath, workDir, scratch, timeout)
    try {
      // Stage the worker script INTO the work dir (its fleet-tree ancestors are not
      // traversable under the deny-default sandbox; the work-dir copy's ancestors
      // are the traversable temp root).
      copyFileSync(script, scriptPath)
      if (environmentHelper == null) throw new Error('Python operational-environment guard not found')
      copyFileSync(environmentHelper, join(workDir, basename(environmentHelper)))
      mkdirSync(scratch, { recursive: true })
      // The plaintext (decrypted record text) is written to the job file in the
      // work dir — NOT passed on argv (so it never lands in a process listing).
      writeFileSync(jobPath, JSON.stringify(job), 'utf8')
    } catch (err) {
      rmSync(workDir, { recursive: true, force: true })
      return {
        error: failure(jobId, {
          faultDomain: 'membrane',
          retryable: false,
          code: 'membrane.kg_stage_failed',
          message: `could not stage kg-embed worker: ${err instanceof Error ? err.message : String(err)}`,
        }),
      }
    }
    this.ranSpecs.push(spec)

    try {
      const result = await this.sandbox.run(spec)
      if (!result.ok) {
        rmSync(workDir, { recursive: true, force: true })
        return {
          error: failure(jobId, {
            faultDomain: 'provider',
            retryable: true,
            code: 'provider.kg_worker_failed',
            message: `kg-embed worker exited ${
              result.exitCode ?? 'signal:' + result.signal
            }: ${result.stderr.trim().slice(-500)}`,
          }),
        }
      }
      if (!existsSync(outPath)) {
        rmSync(workDir, { recursive: true, force: true })
        return {
          error: failure(jobId, {
            faultDomain: 'provider',
            retryable: true,
            code: 'provider.kg_no_output',
            message: 'kg-embed worker produced no result',
          }),
        }
      }
      const json = JSON.parse(readFileSync(outPath, 'utf8')) as Record<string, unknown>
      rmSync(workDir, { recursive: true, force: true })
      return { json }
    } catch (err) {
      rmSync(workDir, { recursive: true, force: true })
      // A sandbox-unsupported / spawn fault surfaces as a membrane fault (NEVER an
      // unconfined fallback spawn — G-4 fail-closed).
      return {
        error: failure(jobId, {
          faultDomain: 'membrane',
          retryable: false,
          code: 'membrane.sandbox_fault',
          message: err instanceof Error ? err.message : String(err),
        }),
      }
    }
  }

  /**
   * Build the G-4 `SandboxSpec` for one worker run. PUBLIC + pure-ish (it touches
   * no FS beyond what the spec names) so the arch-test can inspect the confinement
   * WITHOUT spawning: it asserts no-egress, the credential floor + injected secrets
   * are denied, the read-only set names NO credential store, and the SEC-7 guard
   * accepts the grant (non-vacuous — a planted credential-ancestor grant is refused
   * by the builder).
   */
  buildSpec(
    script: string,
    scriptPath: string,
    jobPath: string,
    outPath: string,
    workDir: string,
    scratch: string,
    timeout: unknown,
  ): SandboxSpec {
    // Read-only paths the transformers/torch import + the cached model load need.
    // The HF cache (model weights) + the interpreter install root. NONE is a
    // credential store — the SEC-7 build-time guard REFUSES any grant that is an
    // ancestor of one, so this set is proven safe by construction (non-vacuous).
    const readOnlyPaths: string[] = [this.paths.hfCache]
    // The interpreter may live under /Applications (Xcode framework python) — grant
    // its install root so seatbelt can traverse to it (holds no operator secret;
    // the credential carve-out still denies any secret nested under it).
    if (this.paths.interpreter.startsWith('/Applications/')) {
      readOnlyPaths.push('/Applications')
    }
    if (this.paths.venvSite != null) readOnlyPaths.push(this.paths.venvSite)

    return {
      command: this.paths.interpreter,
      args: ['-s', '-E', scriptPath, '--job', jobPath, '--out', outPath],
      workDir,
      readOnlyPaths,
      // (a) the planted/real credential stores to deny (the platform default
      // credential floor is ALWAYS merged on top by the sandbox impl — G-4(a)).
      denyPaths: this.denyPaths,
      // (c) NO egress — a local CPU model has zero network need. ASSERTED, not
      // assumed — this closes the poisoned-invoice exfiltration chain (G-4(c)).
      allowedEgress: [],
      timeoutMs:
        typeof timeout === 'number' && timeout > 0 ? timeout : DEFAULT_TIMEOUT_MS,
      // A scrubbed env: scratch → the write-only work dir, HF/transformers OFFLINE
      // (the models are local — zero network, matches no-egress), HOME kept real
      // for cache resolution (the credential carve-out still denies any secret
      // under it). NO credential is named (G-4(a) — no-DEK-reach: the worker holds
      // no key; it reads already-decrypted text from the job file).
      env: this.buildEnv(workDir, scratch),
    }
  }

  /** Build the confined worker's environment (minimal; scratch → work dir; HF offline; no secret). */
  private buildEnv(workDir: string, scratch: string): Record<string, string> {
    const home = homedir()
    const env: Record<string, string> = {
      HOME: home,
      PATH: '/usr/bin:/bin',
      __CF_USER_TEXT_ENCODING: process.env.__CF_USER_TEXT_ENCODING ?? '0x0:0:0',
      // Scratch → the write-only work dir (inherited TMPDIR is read-only under
      // confinement; tempfile/torch fault on it).
      TMPDIR: scratch,
      TMP: scratch,
      TEMP: scratch,
      // HF/transformers: OFFLINE + cache pinned to the read-only real cache.
      HF_HOME: this.paths.hfCache,
      HF_HUB_CACHE: join(this.paths.hfCache, 'hub'),
      TRANSFORMERS_CACHE: join(this.paths.hfCache, 'hub'),
      HF_HUB_OFFLINE: '1',
      TRANSFORMERS_OFFLINE: '1',
      // Drop user-site (~/Library/Python is unreadable under the keychain carve-out).
      PYTHONNOUSERSITE: '1',
    }
    // Inject the venv site-packages (torch/transformers) when configured.
    if (this.paths.venvSite != null) env.CAPABILITY_HOST_KG_EMBED_VENV_SITE = this.paths.venvSite
    return env
  }

  cancel(request: CancelRequest): void {
    this.cancelledJobs.push(request.jobId)
  }
}

// ---------------------------------------------------------------------------
// Deterministic stub (graceful degradation — same envelope, no model, fast CI)
// ---------------------------------------------------------------------------

/**
 * A deterministic unit vector derived from the text (hash-seeded). NOT a real
 * embedding — it exists so the contract/envelope is exercised without the heavy
 * model on CI. The real path (opted-in) produces actual BGE-M3 vectors.
 */
export function stubVector(text: string, dimension: number): number[] {
  // FNV-1a-ish seed over the text bytes for determinism.
  let seed = 2166136261 >>> 0
  for (let i = 0; i < text.length; i++) {
    seed ^= text.charCodeAt(i)
    seed = Math.imul(seed, 16777619) >>> 0
  }
  // Build the raw values via a deterministic PRNG, then L2-normalize so the stub
  // is a unit vector like a real embedding (same envelope shape).
  const raw = Array.from({ length: dimension }, () => {
    seed = (Math.imul(seed, 1103515245) + 12345) >>> 0
    return (seed / 0xffffffff) * 2 - 1
  })
  const norm = Math.sqrt(raw.reduce((acc, x) => acc + x * x, 0))
  const inv = norm > 0 ? 1 / norm : 0
  return raw.map((x) => x * inv)
}

/** Deterministic stub rerank scores (length-overlap heuristic), sorted desc, topK-truncated. */
export function stubScores(
  query: string,
  documents: string[],
  topK: number | null,
): RerankScore[] {
  const q = new Set(query.toLowerCase().split(/\s+/).filter(Boolean))
  const scored: RerankScore[] = documents.map((doc, index) => {
    const words = doc.toLowerCase().split(/\s+/).filter(Boolean)
    const overlap = words.filter((w) => q.has(w)).length
    const score = words.length > 0 ? overlap / words.length : 0
    return { index, score }
  })
  scored.sort((a, b) => b.score - a.score)
  return topK != null && topK >= 0 ? scored.slice(0, topK) : scored
}

// ---------------------------------------------------------------------------
// Envelope builders
// ---------------------------------------------------------------------------

function embeddingsResult(
  jobId: string,
  vectors: number[][],
  dimension: number,
  quantity: number,
  model: string,
  modelVersion: string,
): CapabilityResult {
  const artifact: EmbeddingsArtifact = {
    kind: 'embeddings',
    vectors,
    dimension,
    // The model id is the M1 no-fake-as-real boundary (bug-1358): the real floor id ONLY on the real path;
    // the stub self-identifies, so a stub artifact reaching the .NET durable index is rejected, never pinned.
    model,
    modelVersion,
  }
  return {
    jobId,
    status: 'succeeded',
    progress: 1,
    artifacts: [artifact],
    usage: { unit: 'embedding', quantity, tier: 'local' },
    error: null,
  }
}

function rerankResult(
  jobId: string,
  scored: RerankScore[],
  quantity: number,
  model: string,
  modelVersion: string,
): CapabilityResult {
  const artifact: RerankArtifact = {
    kind: 'rerank',
    scored,
    model,
    modelVersion,
  }
  return {
    jobId,
    status: 'succeeded',
    progress: 1,
    artifacts: [artifact],
    usage: { unit: 'rerank', quantity, tier: 'local' },
    error: null,
  }
}

/** Build a uniform failed-envelope (keeps the envelope shape on the runtime side). */
function failure(
  jobId: string,
  error: NonNullable<CapabilityResult['error']>,
): CapabilityResult {
  return {
    jobId,
    status: 'failed',
    progress: 0,
    artifacts: [],
    usage: { unit: 'call', quantity: 0, tier: 'local' },
    error,
  }
}
