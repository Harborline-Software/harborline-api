/**
 * The Capability v0 REAL bundled-floor TTS runtime — the runtime SIDE of the membrane,
 * hosting the `tts` capability by driving a REAL spawned subprocess CONFINED by
 * the OS-native S7 sandbox (ADR 0123 S7 / ADR 0125 D9 / council SEC-7).
 *
 * This replaces the Phase-2 in-memory reference-image STUB as the reference edition's proof
 * capability with a REAL local capability through a REAL subprocess runtime: it
 * spawns a TTS engine as a child process, confined by the sandbox, and returns
 * the uniform @harborline-software/api-contracts envelope. The POINT is the real
 * spawn-confined-by-sandbox path + cross-edition reuse (`tts` is a flight-deck-
 * domain capability; the reference edition composes it off the shared substrate), not the
 * fidelity of the synthesized speech.
 *
 * ENGINE: the directive's first choice is Piper (`tts/fast` CPU floor). Piper is
 * NOT installed on this host (`which piper` → not found), so per the directive
 * fallback ("the simplest REAL local subprocess runtime that exercises the same
 * path") this uses macOS `/usr/bin/say` — a genuinely-spawned external process
 * that produces a real AIFF audio file. The runtime is engine-pluggable: a
 * `TtsEngine` abstracts "command + args + output extension", so swapping in
 * Piper (when installed) is one engine object, not a runtime rewrite.
 *
 * The runtime NEVER spawns the engine directly — every spawn goes through the
 * injected `Sandbox`. A runtime that spawned a capability engine UNCONFINED would
 * defeat SEC-7; this runtime cannot (it has no direct `spawn` of the engine).
 */

import { mkdtempSync, rmSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import type {
  AudioArtifact,
  CancelRequest,
  CapabilityResult,
  HealthProbe,
  HealthState,
  InvokeRequest,
  NegotiateOffer,
  ProviderManifest,
  TtsCore,
} from '@harborline-software/api-contracts'

import type { RuntimeManifest } from '../membrane/announce.js'
import type { ProbeKind } from '../membrane/observe.js'
import { createSandbox, type Sandbox, type SandboxSpec } from '../sandbox/index.js'
import { CAPABILITY_CONTRACT_VERSION } from './reference-image-runtime.js'
import type { RuntimeHost } from './runtime-host.js'

/** The TTS runtime's stable id. */
export const TTS_RUNTIME_ID = 'capability-tts-floor-runtime'

/** The `tts` capability schema version the runtime hosts. */
export const TTS_SCHEMA_VERSION = '1.0.0'

/**
 * A pluggable TTS engine: maps a synthesis request to a confined subprocess
 * command + the produced audio's container. Swapping Piper for `say` (or a real
 * Piper install) is one of these.
 */
export interface TtsEngine {
  /** Engine id (becomes the provider id). */
  readonly id: string
  /** Human-readable engine name. */
  readonly name: string
  /** The absolute path to the engine binary (the confined subprocess). */
  readonly binary: string
  /** The audio container the engine produces (its output file extension, no dot). */
  readonly outputExt: string
  /** The artifact MIME for the produced audio. */
  readonly mime: string
  /**
   * Build the engine argv that synthesizes `core.text` (and optional `core.voice`)
   * into `outFile`. Pure — no spawning here; the runtime spawns it via the sandbox.
   */
  buildArgs(core: TtsCore, outFile: string): string[]
}

/**
 * The macOS `say` engine — the v0 floor when Piper is not installed. A REAL
 * external subprocess producing a real AIFF file. `say -o <file> [-v <voice>] <text>`.
 */
export const SAY_ENGINE: TtsEngine = {
  id: 'macos-say-floor',
  name: 'macOS say (TTS CPU floor)',
  binary: '/usr/bin/say',
  outputExt: 'aiff',
  mime: 'audio/x-aiff',
  buildArgs(core, outFile) {
    const args = ['-o', outFile]
    if (core.voice != null && core.voice.length > 0) {
      args.push('-v', core.voice)
    }
    args.push(core.text)
    return args
  },
}

/** Build the provider manifest the runtime announces for its TTS engine (SEC-6: no credential field). */
function ttsProviderManifest(engine: TtsEngine): ProviderManifest {
  return {
    manifestVersion: 2,
    id: engine.id,
    name: engine.name,
    version: '0.0.0',
    kind: 'local',
    capability: ['tts'],
    inputSchemaRef: null,
    // SEC-6: no credential field anywhere on the manifest — by construction.
    engineLicense: { role: 'engine', spdx: 'MIT', commercialUse: 'yes' },
    weightsLicense: [{ role: 'weights', spdx: 'CC0-1.0', commercialUse: 'yes' }],
    hardware: { 'min-cpu': { support: 'good', accel: 'cpu' } },
    tier: 'local',
    packaging: 'bundled',
    signing: 'user-space',
    outputRights: { ownership: 'operator', copyrightable: 'unknown' },
    flavor: 'cpu-floor',
  }
}

/** Options for the TTS runtime (injectable sandbox + engine for tests/cross-platform). */
export interface SayTtsRuntimeOptions {
  /** The OS-native sandbox the engine is confined by (defaults to the host's). */
  sandbox?: Sandbox
  /** The TTS engine (defaults to the macOS `say` floor). */
  engine?: TtsEngine
  /**
   * The credential/DEK store paths to confine against (forwarded to the sandbox
   * spec's denyPaths — the platform's default set is always merged on top).
   * Tests inject a planted-secret path here to PROVE the no-reach contract.
   */
  denyPaths?: readonly string[];
}

/**
 * The real TTS runtime. Hosts `tts`; every Invoke spawns the engine as a REAL
 * subprocess CONFINED by the OS-native sandbox, then maps the produced audio file
 * into an `AudioArtifact` on the uniform envelope.
 */
export class SayTtsRuntime implements RuntimeHost {
  private readonly sandbox: Sandbox
  private readonly engine: TtsEngine
  private readonly denyPaths: readonly string[]
  private degraded = false
  /** jobIds the membrane has asked to cancel (observable for tests). */
  readonly cancelledJobs: string[] = []
  /** The sandbox specs this runtime actually ran (observable for tests). */
  readonly ranSpecs: SandboxSpec[] = []

  constructor(options: SayTtsRuntimeOptions = {}) {
    this.sandbox = options.sandbox ?? createSandbox()
    this.engine = options.engine ?? SAY_ENGINE
    this.denyPaths = options.denyPaths ?? []
  }

  /** Force the next health probe to report `degraded` (tri-state demonstration). */
  setDegraded(degraded: boolean): void {
    this.degraded = degraded
  }

  announce(): RuntimeManifest {
    return {
      runtimeId: TTS_RUNTIME_ID,
      name: 'Capability TTS Floor Runtime',
      contractVersion: CAPABILITY_CONTRACT_VERSION,
      capabilities: [
        {
          capabilityId: 'tts',
          schemaVersion: TTS_SCHEMA_VERSION,
          providers: [ttsProviderManifest(this.engine)],
        },
      ],
    }
  }

  negotiateOffer(): NegotiateOffer {
    return {
      contractVersion: CAPABILITY_CONTRACT_VERSION,
      capabilitySchemaVersions: { tts: TTS_SCHEMA_VERSION },
    }
  }

  health(kind: ProbeKind): HealthProbe {
    const state: HealthState = this.degraded ? 'degraded' : 'up'
    return {
      kind,
      state,
      detail: this.degraded ? 'tts floor throttled' : `ready (${this.engine.id})`,
    }
  }

  async invoke(request: InvokeRequest): Promise<CapabilityResult> {
    const jobId = `job:${TTS_RUNTIME_ID}:${request.correlationId}`

    // The runtime hosts only `tts`; anything else is an input fault.
    if (request.capabilityId !== 'tts') {
      return failure(jobId, {
        faultDomain: 'input',
        retryable: false,
        code: 'input.capability_not_hosted',
        message: `tts runtime hosts only 'tts', not '${request.capabilityId}'`,
      })
    }

    const core = request.core as TtsCore
    // A per-Invoke work dir is the ONLY path the confined engine may write.
    const workDir = mkdtempSync(join(tmpdir(), 'capability-tts-'))
    const outFile = join(workDir, `out.${this.engine.outputExt}`)

    const spec: SandboxSpec = {
      command: this.engine.binary,
      args: this.engine.buildArgs(core, outFile),
      workDir,
      // The engine needs read access to system frameworks/voices only (the
      // platform impl adds /usr,/System,/Library); no credential path is named.
      readOnlyPaths: [],
      // Tests plant a secret here; production callers pass the real store paths.
      denyPaths: this.denyPaths,
      // (c) NO egress — a local TTS floor needs zero network.
      allowedEgress: [],
      timeoutMs: typeof core.timeout === 'number' ? core.timeout : 30_000,
    }
    this.ranSpecs.push(spec)

    try {
      const result = await this.sandbox.run(spec)
      if (!result.ok) {
        return failure(jobId, {
          faultDomain: 'provider',
          retryable: true,
          code: 'provider.tts_engine_failed',
          message: `tts engine '${this.engine.id}' exited ${result.exitCode ?? 'signal:' + result.signal}: ${result.stderr.trim()}`,
        })
      }

      // Confirm the engine actually produced a non-empty audio file — the proof
      // the REAL subprocess ran to completion INSIDE the sandbox (not a stub).
      const size = statSync(outFile).size
      if (size <= 0) {
        return failure(jobId, {
          faultDomain: 'provider',
          retryable: true,
          code: 'provider.tts_no_output',
          message: `tts engine '${this.engine.id}' produced no audio bytes`,
        })
      }
      // NB: the workDir is intentionally LEFT on disk so the `file://` uri is
      // readable by the caller. A real runtime hands the bytes to IBlobStore; v0
      // leaves the file in the per-Invoke temp dir (the OS reaps it).
      const artifact: AudioArtifact = {
        kind: 'audio',
        uri: `file://${outFile}`,
        mime: this.engine.mime,
        durationMs: null,
      }
      return {
        jobId,
        status: 'succeeded',
        progress: 1,
        artifacts: [artifact],
        usage: { unit: 'audio', quantity: 1, tier: 'local' },
        error: null,
      }
    } catch (err) {
      rmSync(workDir, { recursive: true, force: true })
      // A sandbox-unsupported / spawn fault surfaces as a provider/membrane fault.
      return failure(jobId, {
        faultDomain: 'membrane',
        retryable: false,
        code: 'membrane.sandbox_fault',
        message: err instanceof Error ? err.message : String(err),
      })
    }
  }

  cancel(request: CancelRequest): void {
    this.cancelledJobs.push(request.jobId)
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
