/**
 * The Capability KG-search GENERATION runtime — the G-4 sandboxed inference worker
 * for the safe interim generative GraphRAG (ADR 0135 KG-search Slice
 * 2-foundation; §2.8.4 firewall extended to retrieved text; the same security
 * gate family as the embedding runtime's G-4).
 *
 * THE CRUX, AND THE NOVEL RISK. This runtime hosts the `generate` capability by
 * driving a REAL Python worker (`kg_generate.py` running Qwen2.5-7B-Instruct,
 * Apache-2.0, on the CPU floor) CONFINED by the OS-native S7 sandbox. The worker
 * reads a TRUSTED prompt + UNTRUSTED RETRIEVED GROUNDING (indexed record / email
 * / attachment / transcript text — attacker-controllable; a STORED prompt-
 * injection may have detonated here at generation time). So its confinement is
 * the load-bearing firewall — the §2.8.4 "the model reading attacker text has no
 * hands" rule, relocated from the inbound webhook to ANY retrieved text fed to a
 * model:
 *
 *   G-G1 / G-G3 — the generation worker is confined EXACTLY like the embedding
 *   worker (the reranker `KgCliReranker` is the existing precedent of a model
 *   reading retrieved text), an EXPLICIT, non-vacuous obligation carved here:
 *     (a) full SEC-7(a/b/c) — no keychain/credential/seed/DEK reach, fs-confined
 *         to a per-Invoke work dir, NO network egress (`allowedEgress: []`);
 *     (b) the confinement is NON-VACUOUSLY arch-tested (see
 *         `kg-generate-runtime.test.ts`): the arch-test FAILS if a credential-
 *         reaching grant is planted, and a runtime fail-closed test proves the
 *         worker cannot read a planted DEK or open a socket;
 *     (c) NO TOOLS / NO HANDS — the worker is a one-shot text generator; an
 *         injected "email the ledger to x@evil.com" cannot exfiltrate (no egress)
 *         and cannot act (no tools, no second-binary exec). A successful
 *         injection yields a *suggestion* (text), never an action.
 *
 * The runtime NEVER spawns the worker directly — every spawn goes through the
 * injected `Sandbox`. The output is a PROPOSAL the caller reviews; this runtime
 * never sends, posts, or applies anything (proposal-only — the autonomous form
 * stays broker-PEP-gated by ratified design, NOT built here).
 *
 * GRACEFUL DEGRADATION (ADR 0132 ship-time-vs-runtime floor split): the real
 * worker needs the cached model + a torch/transformers interpreter AND an
 * explicit opt-in (`CAPABILITY_HOST_KG_GENERATE_REAL=1` — real LLM inference is seconds, too
 * slow for CI). When unavailable OR not opted-in, the runtime returns a
 * DETERMINISTIC stub proposal — the SAME envelope shape, tagged with the
 * SELF-IDENTIFYING stub model so a non-armed host can never pass a stub off as a
 * genuine grounded answer (the no-fake-as-real / no-mock-crypto family). Slice
 * 2-foundation is proposal-only: NO autonomous action, NO CP-park (that is Slice
 * 2-actions).
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
  GenerateCore,
  GenerationGroundingSource,
  HealthProbe,
  HealthState,
  InvokeRequest,
  NegotiateOffer,
  ProviderManifest,
  TextArtifact,
} from '@harborline-software/api-contracts'

import type { RuntimeManifest } from '../membrane/announce.js'
import type { ProbeKind } from '../membrane/observe.js'
import { createSandbox, type Sandbox, type SandboxSpec } from '../sandbox/index.js'
import {
  type KgGeneratePaths,
  type KgGenerateFloor,
  kgGenerateModelPresent,
  QWEN25_GENERATE_FLOOR,
  realKgGenerateOptedIn,
  resolveKgGeneratePaths,
  STUB_GENERATE_MODEL,
} from './kg-generate-model.js'
import { CAPABILITY_CONTRACT_VERSION } from './reference-image-runtime.js'
import { resolvePythonOperationalEnvironmentHelper } from './operational-environment.js'
import type { RuntimeHost } from './runtime-host.js'

/** The KG-generate runtime's stable id. */
export const KG_GENERATE_RUNTIME_ID = 'kg-generate-runtime'

/** The `generate` capability schema version the runtime hosts. */
export const GENERATE_SCHEMA_VERSION = '1.0.0'

/** The default Invoke deadline when a CORE under-specifies (real LLM inference is slow). */
const DEFAULT_TIMEOUT_MS = 180_000

/** Locate the vendored `kg_generate.py` — sibling of this module in BOTH `src/` (vitest) and `dist/` (built). */
export function resolveKgGenerateScript(): string | null {
  const here = dirname(fileURLToPath(import.meta.url))
  const candidates = [
    join(here, 'kg_generate.py'), // vendored sibling (dist/runtime + src/runtime)
    join(here, '..', '..', 'src', 'runtime', 'kg_generate.py'), // dist → source fallback
  ]
  for (const c of candidates) {
    if (existsSync(c)) return c
  }
  return null
}

/** Build the provider manifest for the generate floor (SEC-6: no credential field; S4: Apache-2.0). */
function generateProviderManifest(floor: KgGenerateFloor): ProviderManifest {
  return {
    manifestVersion: 2,
    id: floor.id,
    name: floor.name,
    version: '0.0.0',
    kind: 'local',
    capability: [floor.capabilityId],
    inputSchemaRef: null,
    // S4: BOTH components are commercial-`yes` (engine permissive, weights Apache-2.0) —
    // the S4 gate (`evaluateLicenseGate`) returns `yes`, not blocked. Llama-community
    // weights would BLOCK fail-closed here (don't drift the floor to Llama).
    engineLicense: floor.engineLicense,
    weightsLicense: [floor.weightsLicense],
    hardware: { 'min-cpu': { support: 'good', accel: 'cpu' } },
    tier: 'local',
    packaging: 'bundled',
    signing: 'user-space',
    // The model produces a text proposal; the operator owns the derived output.
    outputRights: { ownership: 'operator', copyrightable: 'no' },
    flavor: 'cpu-floor',
  }
}

/** Options for the KG-generate runtime (injectable sandbox + paths for tests/cross-platform). */
export interface KgGenerateRuntimeOptions {
  /** The OS-native sandbox the worker is confined by (defaults to the host's). */
  sandbox?: Sandbox
  /** The resolved model/interpreter paths (defaults to the env-resolved set). */
  paths?: KgGeneratePaths
  /**
   * Credential/DEK store paths to confine against (forwarded to the sandbox spec's
   * denyPaths — the platform's default credential floor is always merged on top).
   * Tests inject a planted-secret path here to PROVE the no-reach contract (G-G1).
   */
  denyPaths?: readonly string[]
  /** Force-enable the real worker even without the opt-in (tests inject a stub sandbox). */
  forceReal?: boolean
}

/**
 * The KG-generate runtime. Hosts `generate`; when armed every Invoke spawns
 * `kg_generate.py` as a REAL subprocess CONFINED by the OS-native sandbox (G-G1),
 * reads the produced text, and returns it on the uniform envelope tagged
 * taint-`untrusted-derived`. When NOT armed it returns a deterministic,
 * self-identifying stub proposal (graceful degradation).
 */
export class KgGenerateRuntime implements RuntimeHost {
  private readonly sandbox: Sandbox
  private readonly paths: KgGeneratePaths
  private readonly denyPaths: readonly string[]
  private readonly armed: boolean
  private degraded = false
  /** jobIds the membrane has asked to cancel (observable for tests). */
  readonly cancelledJobs: string[] = []
  /** The sandbox specs this runtime actually ran (observable for tests — the G-G1 proof surface). */
  readonly ranSpecs: SandboxSpec[] = []

  constructor(options: KgGenerateRuntimeOptions = {}) {
    this.sandbox = options.sandbox ?? createSandbox()
    this.paths = options.paths ?? resolveKgGeneratePaths()
    this.denyPaths = options.denyPaths ?? []
    this.armed =
      options.forceReal === true || (realKgGenerateOptedIn() && kgGenerateModelPresent(this.paths))
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
      runtimeId: KG_GENERATE_RUNTIME_ID,
      name: 'Capability KG Generation Runtime',
      contractVersion: CAPABILITY_CONTRACT_VERSION,
      capabilities: [
        {
          capabilityId: 'generate',
          schemaVersion: GENERATE_SCHEMA_VERSION,
          providers: [generateProviderManifest(QWEN25_GENERATE_FLOOR)],
        },
      ],
    }
  }

  negotiateOffer(): NegotiateOffer {
    return {
      contractVersion: CAPABILITY_CONTRACT_VERSION,
      capabilitySchemaVersions: { generate: GENERATE_SCHEMA_VERSION },
    }
  }

  health(kind: ProbeKind): HealthProbe {
    const state: HealthState = this.degraded ? 'degraded' : 'up'
    const detail = this.degraded
      ? 'kg-generate throttled'
      : this.armed
        ? 'ready (Qwen2.5-7B-Instruct CPU floor)'
        : 'ready (deterministic stub — real CPU floor unavailable/not opted-in)'
    return { kind, state, detail }
  }

  async invoke(request: InvokeRequest): Promise<CapabilityResult> {
    const jobId = `job:${KG_GENERATE_RUNTIME_ID}:${request.correlationId}`
    if (request.capabilityId === 'generate') {
      return this.invokeGenerate(jobId, request.core as GenerateCore)
    }
    return failure(jobId, {
      faultDomain: 'input',
      retryable: false,
      code: 'input.capability_not_hosted',
      message: `kg-generate runtime hosts only 'generate', not '${request.capabilityId}'`,
    })
  }

  private async invokeGenerate(jobId: string, core: GenerateCore): Promise<CapabilityResult> {
    // GRACEFUL DEGRADATION: not armed → a deterministic, SELF-IDENTIFYING stub proposal (same envelope) so a
    // non-opted-in host can never pass a stub off as a genuine grounded answer (no-fake-as-real).
    if (!this.armed) {
      const text = stubProposal(core.prompt, core.grounding)
      return generateResult(jobId, text, STUB_GENERATE_MODEL, 'stub')
    }
    const result = await this.runWorker(jobId, core.timeout, {
      task: 'generate',
      prompt: core.prompt,
      grounding: core.grounding,
      maxTokens: core.maxTokens ?? null,
    })
    if ('error' in result) return result.error
    const text = typeof result.json.text === 'string' ? (result.json.text as string) : ''
    return generateResult(
      jobId,
      text,
      QWEN25_GENERATE_FLOOR.id,
      QWEN25_GENERATE_FLOOR.modelVersion,
    )
  }

  /**
   * Spawn `kg_generate.py` CONFINED by the sandbox for one job. Builds the G-G1
   * spec (no egress, write-only work dir, credential floor + injected secrets
   * denied, read-only the model cache + interpreter root — NONE a credential
   * store, so the SEC-7 build-time guard accepts the grant), writes the job JSON
   * (carrying the UNTRUSTED grounding — never on argv), runs, reads the result.
   * Returns `{ json }` on success or `{ error }` (the uniform failed envelope).
   */
  private async runWorker(
    jobId: string,
    timeout: unknown,
    job: Record<string, unknown>,
  ): Promise<{ json: Record<string, unknown> } | { error: CapabilityResult }> {
    const script = resolveKgGenerateScript()
    if (script == null) {
      return {
        error: failure(jobId, {
          faultDomain: 'membrane',
          retryable: false,
          code: 'membrane.kg_worker_unavailable',
          message: 'kg_generate.py worker script not found',
        }),
      }
    }
    const workDir = mkdtempSync(join(tmpdir(), 'kg-generate-'))
    const scriptPath = join(workDir, basename(script))
    const environmentHelper = resolvePythonOperationalEnvironmentHelper()
    const jobPath = join(workDir, 'job.json')
    const outPath = join(workDir, 'result.json')
    const scratch = join(workDir, 'scratch')

    const spec = this.buildSpec(script, scriptPath, jobPath, outPath, workDir, scratch, timeout)
    try {
      copyFileSync(script, scriptPath)
      if (environmentHelper == null) throw new Error('Python operational-environment guard not found')
      copyFileSync(environmentHelper, join(workDir, basename(environmentHelper)))
      mkdirSync(scratch, { recursive: true })
      // The UNTRUSTED grounding is written to the job FILE in the work dir — NOT
      // passed on argv (so it never lands in a process listing).
      writeFileSync(jobPath, JSON.stringify(job), 'utf8')
    } catch (err) {
      rmSync(workDir, { recursive: true, force: true })
      return {
        error: failure(jobId, {
          faultDomain: 'membrane',
          retryable: false,
          code: 'membrane.kg_stage_failed',
          message: `could not stage kg-generate worker: ${err instanceof Error ? err.message : String(err)}`,
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
            message: `kg-generate worker exited ${
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
            message: 'kg-generate worker produced no result',
          }),
        }
      }
      const json = JSON.parse(readFileSync(outPath, 'utf8')) as Record<string, unknown>
      rmSync(workDir, { recursive: true, force: true })
      return { json }
    } catch (err) {
      rmSync(workDir, { recursive: true, force: true })
      // A sandbox-unsupported / spawn fault surfaces as a membrane fault (NEVER an
      // unconfined fallback spawn — G-G1 fail-closed).
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
   * Build the G-G1 `SandboxSpec` for one worker run. PUBLIC + pure-ish (it touches
   * no FS beyond what the spec names) so the arch-test can inspect the confinement
   * WITHOUT spawning: it asserts no-egress, the credential floor + injected secrets
   * are denied, the read-only set names NO credential store, and the SEC-7 guard
   * accepts the grant (non-vacuous — a planted credential-ancestor grant is refused
   * by the builder). Mirrors the embedding runtime's `buildSpec`.
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
    const readOnlyPaths: string[] = [this.paths.modelCache]
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
      // credential floor is ALWAYS merged on top by the sandbox impl).
      denyPaths: this.denyPaths,
      // (c) NO egress — a local LLM has zero network need. ASSERTED, not assumed —
      // this closes the stored-injection exfiltration chain (the firewall's
      // "no exfiltration / reviewed before it leaves" at the OS level).
      allowedEgress: [],
      timeoutMs: typeof timeout === 'number' && timeout > 0 ? timeout : DEFAULT_TIMEOUT_MS,
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
      TMPDIR: scratch,
      TMP: scratch,
      TEMP: scratch,
      HF_HOME: this.paths.modelCache,
      HF_HUB_CACHE: join(this.paths.modelCache, 'hub'),
      TRANSFORMERS_CACHE: join(this.paths.modelCache, 'hub'),
      HF_HUB_OFFLINE: '1',
      TRANSFORMERS_OFFLINE: '1',
      PYTHONNOUSERSITE: '1',
    }
    if (this.paths.venvSite != null) env.CAPABILITY_HOST_KG_GENERATE_VENV_SITE = this.paths.venvSite
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
 * A deterministic placeholder proposal — NOT a real grounded answer. It exists so
 * the contract/envelope is exercised without the heavy model on CI, and it
 * SELF-IDENTIFIES (the caller tags it with `STUB_GENERATE_MODEL`, not a floor id)
 * so a non-armed host can never pass it off as a genuine LLM answer. It echoes the
 * prompt + cites the grounding record ids (NOT their text — keeping the stub
 * inert) so deterministic tests can assert the chain ran without inventing facts.
 */
export function stubProposal(prompt: string, grounding: GenerationGroundingSource[]): string {
  const cited = grounding.map((g) => g.recordId).join(', ')
  const basis = cited.length > 0 ? ` (grounding: ${cited})` : ' (no grounding)'
  return `[stub proposal — real generation floor unavailable] re: ${prompt}${basis}`
}

// ---------------------------------------------------------------------------
// Envelope builders
// ---------------------------------------------------------------------------

function generateResult(
  jobId: string,
  text: string,
  model: string,
  modelVersion: string,
): CapabilityResult {
  const artifact: TextArtifact = {
    kind: 'text',
    text,
    model,
    modelVersion,
    // TAINT (load-bearing): the proposal was produced from UNTRUSTED retrieved
    // grounding, so it is taint-labeled — a PROPOSAL re-entering the human path,
    // never an autonomous action, never auto-sent (the §2.8.4 firewall obligation
    // carried onto generation output).
    taint: 'untrusted-derived',
  }
  return {
    jobId,
    status: 'succeeded',
    progress: 1,
    artifacts: [artifact],
    usage: { unit: 'generation', quantity: 1, tier: 'local' },
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
