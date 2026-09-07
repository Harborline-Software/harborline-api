/**
 * The Capability v0 REAL bundled-floor IMAGE runtime — the runtime SIDE of the membrane,
 * hosting the `image` capability by driving a REAL spawned subprocess (a CPU
 * Stable-Diffusion 1.5 diffusers generator) CONFINED by the OS-native S7 sandbox
 * (ADR 0123 S7 / ADR 0125 D9 / council SEC-7).
 *
 * This is the IMAGE analogue of `SayTtsRuntime` (the `tts` floor): where the TTS
 * floor spawns `say`/Piper, the image floor spawns the A1111 venv python running
 * the vendored `sd_local.py` (CPU SD-1.5, 256×256 attention-sliced — minutes per
 * image). It replaces the in-memory `ReferenceImageRuntime` STUB with a REAL local
 * capability through a REAL subprocess runtime, returning the uniform
 * `@harborline-software/api-contracts` envelope. The POINT is the real spawn-confined-by-sandbox
 * path producing real pixels, not the fidelity of the generated image.
 *
 * ENGINE: a CPU diffusers generator (`sd_local.py`) driven by the A1111 venv
 * python (the only python on this host with torch/diffusers installed). The
 * runtime is engine-pluggable: an `ImageEngine` abstracts "interpreter + script +
 * argv mapping", so swapping the floor for a GPU worker (when one lands) is one
 * engine object, not a runtime rewrite.
 *
 * GRACEFUL DEGRADATION (ADR 0123 best-effort floor / ADR 0132 inv-1): the real
 * engine needs the model + venv + script present AND an explicit opt-in
 * (`CAPABILITY_HOST_IMAGE_REAL=1` — a real CPU render is MINUTES, far too slow for CI and the
 * inc-3 bridge/round-trip tests that send a 30 s timeout). When the engine is
 * unavailable OR not opted-in, the runtime FALLS BACK to the deterministic
 * reference stub (`ReferenceImageRuntime`) — never hangs, never crashes, and keeps
 * the existing image envelope/round-trip tests green. So the same `image`
 * capability is a real CPU floor when armed and the deterministic stub otherwise.
 *
 * The runtime NEVER spawns the engine directly — every spawn goes through the
 * injected `Sandbox`. A runtime that spawned a capability engine UNCONFINED would
 * defeat SEC-7; this runtime cannot (it has no direct `spawn` of the engine).
 */

import {
  copyFileSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  statSync,
} from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { basename, dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import {
  type ImageFloorPaths,
  modelSizeMatches,
  resolveImageFloorPaths,
  resolveVenvSitePackages,
  verifyImageFloorModel,
} from './image-floor-model.js'

import type {
  CancelRequest,
  CapabilityResult,
  HealthProbe,
  HealthState,
  ImageArtifact,
  ImageCore,
  InvokeRequest,
  NegotiateOffer,
  ProviderManifest,
} from '@harborline-software/api-contracts'

import type { RuntimeManifest } from '../membrane/announce.js'
import type { ProbeKind } from '../membrane/observe.js'
import { createSandbox, type Sandbox, type SandboxSpec } from '../sandbox/index.js'
import {
  CAPABILITY_CONTRACT_VERSION,
  IMAGE_SCHEMA_VERSION,
  ReferenceImageRuntime,
  STUB_IMAGE_PROVIDER,
} from './reference-image-runtime.js'
import {
  assertNoLegacyOperationalVariables,
  resolvePythonOperationalEnvironmentHelper,
} from './operational-environment.js'
import type { RuntimeHost } from './runtime-host.js'

/** The CPU image-floor runtime's stable id. */
export const CPU_IMAGE_RUNTIME_ID = 'capability-cpu-image-floor-runtime'

/**
 * The fast CPU path (the directive's intended floor): 256×256, attention-sliced,
 * few steps. A real 256² render is still ~20-40 s on this host (8-core CPU); the
 * UI requests these dimensions + a generous timeout so the Invoke does not race
 * the render. Larger sizes / step counts are honored if the CORE asks (but cost
 * scales steeply on CPU).
 */
export const CPU_IMAGE_DEFAULT_STEPS = 8

/**
 * A pluggable image engine: maps an `image` request to a confined subprocess
 * command (the python interpreter) + script + argv, and names the read-only paths
 * the engine needs to FUNCTION. Swapping the CPU SD floor for a GPU worker is one
 * of these.
 */
export interface ImageEngine {
  /** Engine id (becomes the provider id). */
  readonly id: string
  /** Human-readable engine name. */
  readonly name: string
  /**
   * The ABSOLUTE path to the interpreter binary that is actually exec'd (the
   * confined subprocess). MUST be the RESOLVED binary, not a launcher symlink:
   * the S7 seatbelt profile grants `process-exec` of ONLY this literal (the F1
   * narrowing), so a launcher-stub that re-execs a second binary would be denied
   * at its second exec. See `resolveInterpreter`.
   */
  readonly interpreter: string
  /** The absolute path to the generator script the interpreter runs. */
  readonly script: string
  /**
   * The absolute path to the model checkpoint the engine loads — the target of
   * the S6 digest-pin verification the runtime runs lazily before the first real
   * render. Optional: a test engine that never loads a real model omits it (no
   * verification gate); the real CPU SD floor always sets it.
   */
  readonly modelFile?: string
  /**
   * Read-only paths the engine needs to FUNCTION (model weights, the venv
   * site-packages, the HF cache, system config). The platform sandbox allowlists
   * these; the credential carve-out still denies any secret under them. NEVER a
   * credential store.
   */
  readonly readOnlyPaths: readonly string[]
  /**
   * Build the interpreter argv that runs `scriptPath` to render `core` into
   * `outFile`. Pure — no spawning here; the runtime spawns it via the sandbox.
   * The runtime passes a workDir-LOCAL copy of the script as `scriptPath` (so its
   * ancestor dirs are the traversable temp root, not the deny-default fleet tree).
   * Prompt / seed / size become FINAL argv ELEMENTS (never shell-interpolated).
   */
  buildArgs(core: ImageCore, outFile: string, steps: number, scriptPath: string): string[]
  /**
   * Build the confined process's environment. The inherited `TMPDIR`
   * (`/var/folders/.../T`) is READ-ONLY under confinement, so a Python ML stack's
   * `tempfile`/torch scratch faults; this points scratch at the write-only
   * `workDir` and forces HF/transformers OFFLINE (the model is a local checkpoint —
   * zero network, consistent with the no-egress sandbox). It KEEPS `HOME` real
   * (the engine resolves the venv/model relative to it) but the credential
   * carve-out still denies any secret under it.
   */
  buildEnv(workDir: string): Record<string, string>
}

/**
 * Resolve the venv python symlink to the REAL interpreter binary that gets
 * exec'd. The A1111 venv `python3` is a symlink to Xcode's framework python, which
 * is itself a LAUNCHER STUB that `posix_spawn`s the real interpreter at
 * `…/Python.app/Contents/MacOS/Python`. That second exec is exactly what the
 * F1-narrowed `process-exec (literal …)` seatbelt rule blocks — so we must point
 * `spec.command` at the FINAL interpreter, not the launcher. Returns null when the
 * venv python is absent (→ graceful degradation to the stub).
 */
export function resolveInterpreter(
  venvPython: string = resolveImageFloorPaths().venvPython,
): string | null {
  if (!existsSync(venvPython)) return null
  let resolved: string
  try {
    resolved = realpathSync(venvPython)
  } catch {
    return null
  }
  // macOS Python framework launcher-stub → the real interpreter it re-execs.
  const framework = resolved.match(/^(.*\/Python3\.framework\/Versions\/[\d.]+)\/bin\/python[\d.]*$/)
  const frameworkRoot = framework?.[1]
  if (frameworkRoot != null) {
    const real = join(frameworkRoot, 'Resources/Python.app/Contents/MacOS/Python')
    if (existsSync(real)) return real
  }
  return resolved
}

/** Locate the vendored `sd_local.py` — sibling of this module in BOTH `src/` (vitest) and `dist/` (built). */
export function resolveSdLocalScript(): string | null {
  const here = dirname(fileURLToPath(import.meta.url))
  const candidates = [
    join(here, 'sd_local.py'), // vendored sibling (dist/runtime + src/runtime)
    join(here, '..', '..', 'src', 'runtime', 'sd_local.py'), // dist → source fallback
  ]
  for (const c of candidates) {
    if (existsSync(c)) return c
  }
  return null
}

/**
 * Build the CPU SD-1.5 engine if every required artifact is present AND the model
 * passes the cheap size pre-check (the full S6 digest verification runs lazily on
 * the first render — see {@link CpuImageFloorRuntime.invoke}), else null. Every
 * path is resolved from {@link resolveImageFloorPaths} (env-overridable, A1111
 * default) — NOT hard-coded to one operator's install. A wrong-SIZED checkpoint is
 * rejected here (fast); a wrong-DIGEST checkpoint is rejected at render time.
 *
 * The read-only paths are exactly what a transformers/diffusers import + a
 * single-file SD checkpoint load provably need on this host (verified against the
 * macOS sandbox-deny log): the venv, the model dir, the HF cache, `/etc` +
 * `~/.CFUserTextEncoding` (locale/cert config the python ML stack reads), and the
 * Xcode framework subtree the interpreter links. NONE is a credential store; the
 * SEC-7 build-time guard REFUSES any grant that is an ancestor of one.
 */
export function buildCpuSdEngine(
  paths: ImageFloorPaths = resolveImageFloorPaths(),
): ImageEngine | null {
  const interpreter = resolveInterpreter(paths.venvPython)
  const script = resolveSdLocalScript()
  if (interpreter == null || script == null) return null
  // Cheap pre-check: file present AND byte size matches the pin. The full sha256
  // digest verification is deferred to the first render (constant-memory stream
  // of 4 GB — too slow for the constructor hot path). A wrong-sized file is a
  // fast reject; a wrong-digested file fails closed at render time.
  if (!modelSizeMatches(paths.modelFile)) return null
  const SD_MODEL_PATH = paths.modelFile
  const SD_VENV = paths.venvRoot
  const HF_CACHE = paths.hfCache
  // The resolved interpreter is the framework binary (the F1 narrowing), NOT the
  // venv `python3` symlink — so running it does NOT auto-activate the venv. Inject
  // the venv's site-packages (where torch/diffusers live) onto the script's
  // sys.path via CAPABILITY_HOST_IMAGE_VENV_SITE. This is the configurable replacement for
  // the script's old HARD-CODED `…/python3.9/site-packages` injection (the A1111
  // coupling this PR removes). Null when the venv has no resolvable site-packages.
  const SD_VENV_SITE = resolveVenvSitePackages(SD_VENV)

  // The interpreter may live under /Applications (Xcode framework python) or
  // /usr,/Library (a homebrew/system python). Grant read of the interpreter's
  // install root so seatbelt can TRAVERSE down to it: a deep interpreter path
  // needs every ancestor dir-node readable, and the base profile only traverses a
  // fixed root set (not /Applications). `/Applications` holds no operator secrets;
  // the credential carve-out still denies any secret nested under it.
  const interpreterRoots: string[] = []
  if (interpreter.startsWith('/Applications/')) interpreterRoots.push('/Applications')
  const readOnlyPaths = [
    SD_VENV,
    dirname(SD_MODEL_PATH),
    HF_CACHE,
    '/etc',
    '/private/etc',
    join(homedir(), '.CFUserTextEncoding'),
    '/dev/dtracehelper',
    '/dev/autofs_nowait',
    ...interpreterRoots,
  ]

  return {
    id: 'cpu-sd15-floor',
    name: 'CPU Stable-Diffusion 1.5 (image CPU floor)',
    interpreter,
    script,
    modelFile: SD_MODEL_PATH,
    readOnlyPaths,
    buildArgs(core, outFile, steps, scriptPath) {
      // `-s` (no user site) + `-E` (ignore PYTHON* env): drops `~/Library/Python`
      // from sys.path (which the keychain carve-out makes unreadable) and prevents
      // env-injected import paths. Prompt/seed/size are FINAL argv elements.
      // `scriptPath` is the runtime's workDir-local copy of `this.script`.
      const args = [
        '-s',
        '-E',
        scriptPath,
        // Pass the DIGEST-VERIFIED model path explicitly via argv (a final element,
        // never shell-interpolated) so the confined script loads exactly the pinned
        // checkpoint — not whatever its env/default resolution would pick.
        '--model',
        SD_MODEL_PATH,
        '--prompt',
        core.prompt,
        '--out',
        outFile,
        '--width',
        String(core.size.w),
        '--height',
        String(core.size.h),
        '--steps',
        String(steps),
        '--seed',
        String(core.seed),
      ]
      // The negative prompt rides `providerInputs` in the full contract; the thin
      // CORE has no negative field, so the floor renders with the script default.
      return args
    },
    buildEnv(workDir) {
      // A MINIMAL env: keep what the interpreter + the script's home-relative venv/
      // model resolution need, scrub everything else, point ALL scratch at the
      // write-only work dir, and force HF/transformers OFFLINE (local checkpoint,
      // zero network — matches the no-egress sandbox). NO credential is named.
      const home = homedir()
      const scratch = join(workDir, 'scratch')
      return {
        HOME: home,
        PATH: '/usr/bin:/bin',
        // The interpreter reads this on macOS; absent → a CoreFoundation warning.
        __CF_USER_TEXT_ENCODING: process.env.__CF_USER_TEXT_ENCODING ?? '0x0:0:0',
        // Scratch → the write-only work dir (the inherited /var/folders TMPDIR is
        // read-only under confinement; tempfile/torch fault on it).
        TMPDIR: scratch,
        TMP: scratch,
        TEMP: scratch,
        // HF/transformers: OFFLINE + cache pinned to the read-only real cache.
        HF_HOME: HF_CACHE,
        TRANSFORMERS_CACHE: HF_CACHE,
        HF_HUB_OFFLINE: '1',
        TRANSFORMERS_OFFLINE: '1',
        // Belt-and-suspenders with the `-s` flag — drop user-site (~/Library/Python
        // is unreadable under the keychain carve-out).
        PYTHONNOUSERSITE: '1',
        // Inject the venv site-packages (torch/diffusers) onto the script's path —
        // the configurable replacement for the script's old hard-coded A1111
        // `python3.9/site-packages` injection. `-E` ignores PYTHON* env, NOT this
        // (the script reads it via os.environ). Omitted when no venv site-packages
        // resolved (then the interpreter must already have torch importable).
        ...(SD_VENV_SITE != null ? { CAPABILITY_HOST_IMAGE_VENV_SITE: SD_VENV_SITE } : {}),
      }
    },
  }
}

/** Build the provider manifest the runtime announces for its image engine (SEC-6: no credential field). */
function imageProviderManifest(engineId: string, engineName: string): ProviderManifest {
  return {
    manifestVersion: 2,
    id: engineId,
    name: engineName,
    version: '0.0.0',
    kind: 'local',
    capability: ['image'],
    inputSchemaRef: null,
    // SEC-6: no credential field anywhere on the manifest — by construction.
    engineLicense: { role: 'engine', spdx: 'Apache-2.0', commercialUse: 'yes' },
    // SD-1.5 weights are CreativeML-OpenRAIL-M — a USE-RESTRICTED license, NOT a
    // clean commercial `yes`. Declared `unknown` so the membrane's MIN-over-
    // components gate stays honest (a future MIT/CC0 floor model lifts this).
    weightsLicense: [{ role: 'weights', spdx: 'CreativeML-OpenRAIL-M', commercialUse: 'unknown' }],
    hardware: { 'min-cpu': { support: 'good', accel: 'cpu' } },
    tier: 'local',
    packaging: 'bundled',
    signing: 'user-space',
    outputRights: { ownership: 'operator', copyrightable: 'unknown' },
    flavor: 'cpu-floor',
  }
}

/** A produced-image size cap for inlining as a base64 data URI (the membrane's 8 MB data-URI cap). */
export const MAX_INLINE_IMAGE_BYTES = 8 * 1024 * 1024

/** Options for the CPU image runtime (injectable sandbox + engine for tests/cross-platform). */
export interface CpuImageFloorRuntimeOptions {
  /** The OS-native sandbox the engine is confined by (defaults to the host's). */
  sandbox?: Sandbox
  /**
   * The image engine. Defaults to the CPU SD floor when present + opted-in, else
   * undefined → the runtime falls back to the deterministic reference stub.
   */
  engine?: ImageEngine | null
  /**
   * The credential/DEK store paths to confine against (forwarded to the sandbox
   * spec's denyPaths — the platform's default set is always merged on top). Tests
   * inject a planted-secret path here to PROVE the no-reach contract.
   */
  denyPaths?: readonly string[]
  /**
   * Force-enable the real engine even without the `CAPABILITY_HOST_IMAGE_REAL` opt-in
   * (tests inject a stub sandbox + a fake engine to exercise the real path fast).
   */
  forceReal?: boolean
}

/** Whether the real CPU render is opted-in (it is MINUTES — off by default so CI/bridge stay fast). */
export function realImageOptedIn(env: NodeJS.ProcessEnv = process.env): boolean {
  assertNoLegacyOperationalVariables(env)
  const v = env.CAPABILITY_HOST_IMAGE_REAL
  return v === '1' || v === 'true'
}

/**
 * The real CPU image-floor runtime. Hosts `image`; when armed (engine present +
 * opted-in) every Invoke spawns the diffusers generator as a REAL subprocess
 * CONFINED by the OS-native sandbox, reads the produced PNG, and returns it as an
 * `ImageArtifact` (base64 data-URI, honoring the 8 MB cap). When NOT armed it
 * delegates to the deterministic reference stub (graceful degradation).
 */
export class CpuImageFloorRuntime implements RuntimeHost {
  private readonly sandbox: Sandbox
  private readonly engine: ImageEngine | null
  private readonly denyPaths: readonly string[]
  private readonly stub = new ReferenceImageRuntime()
  private degraded = false
  /**
   * Cached S6 digest-pin verification of the engine's model (run lazily on the
   * first real render — the full 4 GB sha256 stream is too slow for the
   * constructor hot path). `undefined` until first checked; then the cached
   * verdict (null model-file = no gate, trivially ok). A mismatch fails the
   * render closed — the floor NEVER loads a tampered/unpinned checkpoint.
   */
  private modelVerified?: boolean
  /** jobIds the membrane has asked to cancel (observable for tests). */
  readonly cancelledJobs: string[] = []
  /** The sandbox specs this runtime actually ran (observable for tests). */
  readonly ranSpecs: SandboxSpec[] = []

  constructor(options: CpuImageFloorRuntimeOptions = {}) {
    this.sandbox = options.sandbox ?? createSandbox()
    this.denyPaths = options.denyPaths ?? []
    // Resolve the engine: explicit override wins; otherwise the CPU floor IFF
    // present AND opted-in (or force-real for tests).
    const armed = options.forceReal === true || realImageOptedIn()
    this.engine =
      options.engine !== undefined ? options.engine : armed ? buildCpuSdEngine() : null
  }

  /** Whether this runtime will render for real (vs. degrade to the stub). */
  get usesRealEngine(): boolean {
    return this.engine != null
  }

  /** The provider id this runtime resolves to (real engine id, or the stub's). */
  get providerId(): string {
    return this.engine?.id ?? 'reference-stub-image'
  }

  /** Force the next health probe to report `degraded` (tri-state demonstration). */
  setDegraded(degraded: boolean): void {
    this.degraded = degraded
    this.stub.setDegraded(degraded)
  }

  announce(): RuntimeManifest {
    const provider =
      this.engine != null
        ? imageProviderManifest(this.engine.id, this.engine.name)
        : STUB_IMAGE_PROVIDER
    return {
      runtimeId: CPU_IMAGE_RUNTIME_ID,
      name: 'Capability CPU Image Floor Runtime',
      contractVersion: CAPABILITY_CONTRACT_VERSION,
      capabilities: [
        {
          capabilityId: 'image',
          schemaVersion: IMAGE_SCHEMA_VERSION,
          providers: [provider],
        },
      ],
    }
  }

  negotiateOffer(): NegotiateOffer {
    return {
      contractVersion: CAPABILITY_CONTRACT_VERSION,
      capabilitySchemaVersions: { image: IMAGE_SCHEMA_VERSION },
    }
  }

  health(kind: ProbeKind): HealthProbe {
    const state: HealthState = this.degraded ? 'degraded' : 'up'
    const detail = this.degraded
      ? 'image floor throttled'
      : this.engine != null
        ? `ready (${this.engine.id})`
        : 'ready (reference stub — real CPU floor unavailable)'
    return { kind, state, detail }
  }

  async invoke(request: InvokeRequest): Promise<CapabilityResult> {
    const jobId = `job:${CPU_IMAGE_RUNTIME_ID}:${request.correlationId}`

    // The runtime hosts only `image`; anything else is an input fault.
    if (request.capabilityId !== 'image') {
      return failure(jobId, {
        faultDomain: 'input',
        retryable: false,
        code: 'input.capability_not_hosted',
        message: `image runtime hosts only 'image', not '${request.capabilityId}'`,
      })
    }

    // GRACEFUL DEGRADATION: no real engine → the deterministic reference stub.
    if (this.engine == null) {
      return this.stub.invoke(request)
    }

    // S6 DIGEST-PIN GATE (lazy, cached): before the FIRST real render, verify the
    // engine's model bytes against the pinned sha256. A tampered/unpinned (but
    // right-sized) checkpoint that slipped past the constructor's size pre-check is
    // rejected here — the floor fails closed rather than loading it. An engine with
    // no `modelFile` (the test fake) has no model to pin → the gate is a no-op.
    if (this.engine.modelFile != null && this.modelVerified !== true) {
      const verification = await verifyImageFloorModel(this.engine.modelFile)
      this.modelVerified = verification.ok
      if (!verification.ok) {
        return failure(jobId, {
          faultDomain: 'provider',
          retryable: false,
          code: 'provider.image_model_unverified',
          message: `image floor model failed the S6 digest pin (${verification.reason}): ${verification.detail}`,
        })
      }
    }

    const core = request.core as ImageCore
    const steps = renderSteps(core)
    // A per-Invoke work dir is the ONLY path the confined engine may write.
    const workDir = mkdtempSync(join(tmpdir(), 'capability-image-'))
    const outFile = join(workDir, `out.${core.format}`)
    // Copy the generator script INTO the work dir. The vendored script lives under
    // the fleet tree, whose ancestor dirs the deny-default sandbox cannot traverse
    // (only a fixed root set + the work dir are reachable). Running the work-dir
    // copy keeps the script's ancestors inside the traversable temp root.
    const scriptPath = join(workDir, basename(this.engine.script))
    const environmentHelper = resolvePythonOperationalEnvironmentHelper()
    try {
      copyFileSync(this.engine.script, scriptPath)
      if (environmentHelper == null) throw new Error('Python operational-environment guard not found')
      copyFileSync(environmentHelper, join(workDir, basename(environmentHelper)))
      // The engine's env points scratch (TMPDIR/torch/HF) at workDir/scratch —
      // create it so tempfile finds a writable dir under confinement.
      mkdirSync(join(workDir, 'scratch'), { recursive: true })
    } catch (err) {
      rmSync(workDir, { recursive: true, force: true })
      return failure(jobId, {
        faultDomain: 'membrane',
        retryable: false,
        code: 'membrane.image_script_unavailable',
        message: `could not stage image engine script: ${err instanceof Error ? err.message : String(err)}`,
      })
    }

    const spec: SandboxSpec = {
      command: this.engine.interpreter,
      args: this.engine.buildArgs(core, outFile, steps, scriptPath),
      workDir,
      // Read-only access the diffusers/torch import + the SD checkpoint load need.
      // NO credential path is named; the carve-out denies any secret under these.
      readOnlyPaths: this.engine.readOnlyPaths,
      // Tests plant a secret here; production callers pass the real store paths.
      denyPaths: this.denyPaths,
      // (c) NO egress — the model is a LOCAL single-file checkpoint; zero network.
      allowedEgress: [],
      // CPU render takes MINUTES — honor the membrane-owned CORE deadline (the UI
      // sends a generous one); default to 10 min if the CORE under-specifies.
      timeoutMs: typeof core.timeout === 'number' && core.timeout > 0 ? core.timeout : 600_000,
      // A scrubbed env: scratch → the write-only work dir, HF offline, HOME kept
      // real (venv/model resolution). The credential carve-out still denies any
      // secret under HOME.
      env: this.engine.buildEnv(workDir),
    }
    this.ranSpecs.push(spec)

    try {
      const result = await this.sandbox.run(spec)
      if (!result.ok) {
        rmSync(workDir, { recursive: true, force: true })
        return failure(jobId, {
          faultDomain: 'provider',
          retryable: true,
          code: 'provider.image_engine_failed',
          message: `image engine '${this.engine.id}' exited ${
            result.exitCode ?? 'signal:' + result.signal
          }: ${result.stderr.trim().slice(-500)}`,
        })
      }

      // Confirm the engine actually produced a non-empty PNG — the proof the REAL
      // subprocess ran to completion INSIDE the sandbox (not a stub).
      if (!existsSync(outFile) || statSync(outFile).size <= 0) {
        rmSync(workDir, { recursive: true, force: true })
        return failure(jobId, {
          faultDomain: 'provider',
          retryable: true,
          code: 'provider.image_no_output',
          message: `image engine '${this.engine.id}' produced no image bytes`,
        })
      }

      const { artifact, reapDir } = this.toArtifact(outFile, core)
      // Reap the work dir UNLESS we returned an over-cap `file://` uri that still
      // needs the bytes on disk for the caller to load.
      if (reapDir) rmSync(workDir, { recursive: true, force: true })
      return {
        jobId,
        status: 'succeeded',
        progress: 1,
        artifacts: [artifact],
        usage: { unit: 'image', quantity: core.count, tier: 'local' },
        error: null,
      }
    } catch (err) {
      rmSync(workDir, { recursive: true, force: true })
      // A sandbox-unsupported / spawn fault surfaces as a membrane fault.
      return failure(jobId, {
        faultDomain: 'membrane',
        retryable: false,
        code: 'membrane.sandbox_fault',
        message: err instanceof Error ? err.message : String(err),
      })
    }
  }

  /**
   * Read the produced PNG + return it as an `ImageArtifact` with a base64 `data:`
   * URI so the Tauri webview can render it with ZERO filesystem-scope grant
   * (mirrors the bridge's audio-inlining). Honors the 8 MB data-URI cap: an
   * over-cap image falls back to a `file://` uri and signals the caller to KEEP
   * the work dir (`reapDir:false`) so the bytes stay readable. A CPU 256² PNG is
   * ~100-200 KB — the cap is a guard for a future high-res provider.
   */
  private toArtifact(
    outFile: string,
    core: ImageCore,
  ): { artifact: ImageArtifact; reapDir: boolean } {
    const mime = `image/${core.format}`
    const bytes = readFileSync(outFile)
    if (bytes.length > MAX_INLINE_IMAGE_BYTES) {
      return {
        artifact: { kind: 'image', uri: `file://${outFile}`, mime, w: core.size.w, h: core.size.h },
        reapDir: false,
      }
    }
    const b64 = bytes.toString('base64')
    return {
      artifact: {
        kind: 'image',
        uri: `data:${mime};base64,${b64}`,
        mime,
        w: core.size.w,
        h: core.size.h,
      },
      reapDir: true,
    }
  }

  cancel(request: CancelRequest): void {
    this.cancelledJobs.push(request.jobId)
  }
}

/** Clamp the render step count: honor an explicit small CORE-derived value, default the CPU floor. */
function renderSteps(_core: ImageCore): number {
  // The thin CORE has no `steps` field (it lives in providerInputs). The CPU floor
  // uses a small fixed step count for the fast 256² path.
  return CPU_IMAGE_DEFAULT_STEPS
}

/** Build a uniform failed-envelope (keeps the envelope shape on the runtime side). */
function failure(jobId: string, error: NonNullable<CapabilityResult['error']>): CapabilityResult {
  return {
    jobId,
    status: 'failed',
    progress: 0,
    artifacts: [],
    usage: { unit: 'call', quantity: 0, tier: 'local' },
    error,
  }
}
