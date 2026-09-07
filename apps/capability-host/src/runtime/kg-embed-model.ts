/**
 * The KG-search embedding/rerank MODEL contract + path resolution (ADR 0123
 * amendment 2026-06-24 §S4/§S6, grounded in the ONR survey earlier repository ticket #1367 + the
 * de-risk spike earlier repository ticket #1368).
 *
 * Slice 1a is the model-inference BOUNDARY only: the `embeddings` floor (BGE-M3,
 * MIT) and the `rerank` floor (bge-reranker-v2-m3, MIT), on-device CPU, run by a
 * Python worker the runtime spawns CONFINED by the OS-native S7 sandbox (the G-4
 * obligation). NO sqlite-vec / index here (that is Slice 1b); NO generation (Slice
 * 2). This module declares:
 *
 *   1. The S4 LICENSE declarations for both floors — BOTH MIT, so the S4 gate
 *      (`evaluateLicenseGate`, @harborline-software/api-contracts) returns commercial `yes`,
 *      not blocked. A CC-BY-NC / Llama-community multilingual model would be
 *      blocked fail-closed — which is WHY BGE-M3 (MIT) is the floor.
 *   2. The model+version that RIDES the result artifact (G-5 — the KG consumer
 *      pins it per index row so a model change forces a full re-embed, never a
 *      silent mixed-dimension index).
 *   3. The path resolution that decouples the floor from any one operator's
 *      install (env-overridable HF cache + interpreter), mirroring the image
 *      floor's `resolveImageFloorPaths`.
 *
 * The models are loaded OFFLINE from the local HF cache (transformers, no
 * network) — consistent with the no-egress sandbox (G-4(c)). The spike measured
 * real BGE-M3 / bge-reranker-v2-m3 inference on these exact cached models on a CPU
 * floor; this reuses that approach. ONNX desktop / Core ML iOS / ONNX-Mobile
 * Android packaging + content-digest pinning are a LATER sub-slice (this floor
 * runs the cached PyTorch weights via transformers on the CPU floor, which is the
 * S6 "the floor IS the floor, not an accelerator" baseline).
 */

import { existsSync, readdirSync, realpathSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

import type { LicenseComponent } from '@harborline-software/api-contracts'

import { assertNoLegacyOperationalVariables } from './operational-environment.js'

/**
 * A KG-search model floor — the S6 mandatory local floor for a capability kind.
 * Both floors are MIT (the S4 gate's first `weightsLicense ∧ engineLicense`
 * model-weights entries that PASS commercial intent).
 */
export interface KgModelFloor {
  /** The capability id this floor supplies (`embeddings` | `rerank`). */
  readonly capabilityId: 'embeddings' | 'rerank'
  /** The provider id (becomes the manifest id + the artifact `model`). */
  readonly id: string
  /** Human-readable name. */
  readonly name: string
  /** The upstream HF model repo this floor loads (offline, from the local cache). */
  readonly hfRepo: string
  /** The model version/revision that RIDES the result artifact (G-5). */
  readonly modelVersion: string
  /** The S4 engine-license component (the transformers/ONNX runtime that runs the weights). */
  readonly engineLicense: LicenseComponent
  /** The S4 weights-license component (the model weights). MIT for both floors. */
  readonly weightsLicense: LicenseComponent
}

/**
 * The `embeddings` floor — BGE-M3 (BAAI/bge-m3), MIT. ~560M-param XLM-RoBERTa;
 * produces 1024-dim dense vectors (the declared dimension the CORE asserts). The
 * floor, NOT an accelerator (GPU/ANE are accelerators). The engine = transformers
 * (Apache-2.0 upstream, but the inference RUNTIME we declare is the permissive
 * stack — declared MIT-equivalent permissive `yes`; the gate's MIN holds as long
 * as engine is commercial-`yes`, which transformers/ONNX both are).
 */
export const BGE_M3_FLOOR: KgModelFloor = Object.freeze({
  capabilityId: 'embeddings',
  id: 'bge-m3',
  name: 'BGE-M3 (embeddings CPU floor)',
  hfRepo: 'BAAI/bge-m3',
  modelVersion: '1.0',
  // The inference engine (transformers / ONNX runtime). Apache-2.0 is a
  // commercial-`yes` permissive license; declared `yes` so the MIN holds.
  engineLicense: { role: 'engine', spdx: 'Apache-2.0', commercialUse: 'yes' } satisfies LicenseComponent,
  // BGE-M3 weights are MIT (the load-bearing S4 PASS — a non-permissive model
  // would block fail-closed here).
  weightsLicense: { role: 'weights', spdx: 'MIT', commercialUse: 'yes' } satisfies LicenseComponent,
})

/**
 * The `rerank` floor — bge-reranker-v2-m3 (BAAI/bge-reranker-v2-m3), MIT. A
 * cross-encoder relevance scorer (XLM-RoBERTa sequence-classification head over
 * `[query, doc]` pairs). The dominant query-time CPU cost (~650 ms top-20 on the
 * Intel floor) — invoked only on explicit AI-ask, never the keystroke path.
 */
export const BGE_RERANKER_V2_M3_FLOOR: KgModelFloor = Object.freeze({
  capabilityId: 'rerank',
  id: 'bge-reranker-v2-m3',
  name: 'bge-reranker-v2-m3 (rerank CPU floor)',
  hfRepo: 'BAAI/bge-reranker-v2-m3',
  modelVersion: '1.0',
  engineLicense: { role: 'engine', spdx: 'Apache-2.0', commercialUse: 'yes' } satisfies LicenseComponent,
  // bge-reranker-v2-m3 weights are MIT.
  weightsLicense: { role: 'weights', spdx: 'MIT', commercialUse: 'yes' } satisfies LicenseComponent,
})

/** The declared embedding dimension of the BGE-M3 floor (the CORE's `dimension`). */
export const BGE_M3_DIMENSION = 1024

/**
 * The SELF-IDENTIFYING stub model sentinel a NON-armed runtime stamps on its deterministic embeddings —
 * NEVER a registered floor id (bug-1358). The .NET indexer's M1 no-fake-as-real gate
 * (`KgModelFloorGate.StubModelSentinel`) is byte-aligned to this value, so a stub artifact reaching the
 * durable index is REJECTED rather than pinned as genuine `bge-m3`.
 *
 * The latent gap the Slice-1a runtime shipped: `embeddingsResult` stamped `model: BGE_M3_FLOOR.id`
 * (`'bge-m3'`) on BOTH the real worker output AND the deterministic stub, so a stub vector was
 * indistinguishable from a real BGE-M3 vector by its label alone (the no-mock-crypto family — a fake that
 * wears a real label is a silent trust leak). The fix: the stub self-identifies at the SOURCE, complementing
 * the .NET indexer gate (defence in depth — the label is honest AND the durable boundary rejects a non-floor
 * id).
 */
export const STUB_EMBED_MODEL = 'stub-bge-m3'

/** The self-identifying stub model sentinel for the `rerank` floor (bug-1358 — same no-fake-as-real fix). */
export const STUB_RERANK_MODEL = 'stub-bge-reranker-v2-m3'

/** Resolved paths the KG embed worker needs (env-overridable; HF-cache-default). */
export interface KgEmbedPaths {
  /** The python interpreter that runs the worker script (must have torch+transformers). */
  readonly interpreter: string
  /** The HF cache root the models load from OFFLINE (read-only under confinement). */
  readonly hfCache: string
  /** An optional extra venv site-packages dir injected onto the worker's sys.path. */
  readonly venvSite: string | null
}

/**
 * The A1111/Forge venv python is the only interpreter on this host with
 * torch+transformers importable (same one the image floor uses). It is the
 * DEFAULT for the real path; an operator points {@link KgEmbedPaths.interpreter}
 * at a fleet-managed interpreter via `CAPABILITY_HOST_KG_EMBED_PYTHON`. A later sub-slice
 * bundles an ONNX-runtime interpreter so the floor is self-contained.
 */
function defaultInterpreter(home: string): string {
  return join(home, 'stable-diffusion-webui', 'venv', 'bin', 'python3')
}

/**
 * Resolve a venv-python symlink to the REAL interpreter binary that gets exec'd
 * (mirrors the image floor's `resolveInterpreter`). The macOS framework `python3`
 * is a LAUNCHER STUB that `posix_spawn`s the real interpreter at
 * `…/Python.app/Contents/MacOS/Python`; that SECOND exec is exactly what the S7
 * `process-exec (literal …)` rule blocks (the exec-narrowing). So `spec.command`
 * MUST point at the FINAL interpreter, not the launcher. Returns the input
 * unchanged when it cannot be resolved (the caller's pre-checks then degrade).
 */
export function resolveWorkerInterpreter(interpreter: string): string {
  if (!existsSync(interpreter)) return interpreter
  let resolved: string
  try {
    resolved = realpathSync(interpreter)
  } catch {
    return interpreter
  }
  const framework = resolved.match(
    /^(.*\/Python3?\.framework\/Versions\/[\d.]+)\/bin\/python[\d.]*$/,
  )
  const frameworkRoot = framework?.[1]
  if (frameworkRoot != null) {
    const real = join(frameworkRoot, 'Resources/Python.app/Contents/MacOS/Python')
    if (existsSync(real)) return real
  }
  return resolved
}

/**
 * Resolve a venv's `lib/pythonX.Y/site-packages` dir (where torch/transformers
 * live). The resolved framework interpreter does NOT auto-activate the venv, so
 * the worker injects this onto `sys.path` via `CAPABILITY_HOST_KG_EMBED_VENV_SITE`. Returns
 * null when the venv has no resolvable site-packages dir.
 */
export function resolveVenvSitePackages(venvRoot: string): string | null {
  const libDir = join(venvRoot, 'lib')
  if (!existsSync(libDir)) return null
  try {
    for (const entry of readdirSync(libDir)) {
      if (entry.startsWith('python')) {
        const site = join(libDir, entry, 'site-packages')
        if (existsSync(site)) return site
      }
    }
  } catch {
    // unreadable — fall through to null
  }
  return null
}

/**
 * Resolve the KG-embed worker paths. Every path is env-overridable with a
 * documented default; NONE is a credential store (the SEC-7 build-time guard
 * REFUSES any readOnlyPaths grant that is an ancestor of one, so this set is
 * proven safe by construction).
 */
export function resolveKgEmbedPaths(
  env: NodeJS.ProcessEnv = process.env,
  home: string = homedir(),
): KgEmbedPaths {
  assertNoLegacyOperationalVariables(env)
  // Resolve the configured/default python through any launcher-stub to the FINAL
  // interpreter binary (the exec-narrowing requires `spec.command` be the real
  // binary, not a re-exec'ing launcher).
  const configuredPython = env.CAPABILITY_HOST_KG_EMBED_PYTHON ?? defaultInterpreter(home)
  const interpreter = resolveWorkerInterpreter(configuredPython)
  const hfCache = env.CAPABILITY_HOST_KG_EMBED_HF_CACHE ?? join(home, '.cache', 'huggingface')
  // The venv site-packages (torch/transformers): the explicit env override wins;
  // otherwise auto-derive from the configured python's venv root (its parent's
  // parent — `<venv>/bin/python3` → `<venv>`). The configurable replacement for
  // hard-coding one install's site dir.
  const venvSiteEnv = env.CAPABILITY_HOST_KG_EMBED_VENV_SITE
  let venvSite: string | null = null
  if (venvSiteEnv != null && existsSync(venvSiteEnv)) {
    venvSite = venvSiteEnv
  } else {
    // `<venv>/bin/python3` → venv root is two dirs up from the CONFIGURED python
    // (not the resolved framework binary, which lives elsewhere).
    const venvRoot = join(configuredPython, '..', '..')
    venvSite = resolveVenvSitePackages(venvRoot)
  }
  return { interpreter, hfCache, venvSite }
}

/**
 * Whether the BOTH cached models are present (a cheap pre-check before arming the
 * real engine — a missing model ⇒ graceful degradation to the deterministic
 * stub, never a hang). Checks the HF-cache `models--<org>--<name>` dir for each.
 */
export function kgModelsPresent(paths: KgEmbedPaths): boolean {
  const hubDir = join(paths.hfCache, 'hub')
  for (const floor of [BGE_M3_FLOOR, BGE_RERANKER_V2_M3_FLOOR]) {
    const cacheDir = join(hubDir, `models--${floor.hfRepo.replace('/', '--')}`)
    if (!existsSync(cacheDir)) return false
  }
  return existsSync(paths.interpreter)
}

/** Whether the real KG-embed path is opted-in (real CPU inference is slow — off by default for CI). */
export function realKgEmbedOptedIn(env: NodeJS.ProcessEnv = process.env): boolean {
  assertNoLegacyOperationalVariables(env)
  const v = env.CAPABILITY_HOST_KG_EMBED_REAL
  return v === '1' || v === 'true'
}
