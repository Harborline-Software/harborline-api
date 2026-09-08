/**
 * The KG-search GENERATION model contract + path resolution (ADR 0135 KG-search
 * Slice 2-foundation — the safe interim generative GraphRAG: proposal-only /
 * human-CP-gated / §2.8.4-firewall-bound).
 *
 * Slice 2-foundation hosts the `generate` capability — a grounded TEXT proposal
 * produced from a permission-clipped grounding subgraph by a local LLM, run by a
 * Python worker the runtime spawns CONFINED by the OS-native S7 sandbox (the G-4
 * obligation). This is the NEXT instance of the exact pattern the embedding +
 * rerank floors already follow (`kg-embed-model.ts`), except the worker emits
 * TEXT (a proposal) instead of a vector/score — and the worker reads UNTRUSTED
 * RETRIEVED grounding (a stored injection may have detonated at generation), so
 * its confinement is the load-bearing firewall (§2.8.4 extended to retrieved
 * text).
 *
 * This module declares:
 *
 *   1. The S4 LICENSE declaration for the `generate` floor — Qwen2.5 weights are
 *      Apache-2.0 + a permissive engine (llama.cpp / transformers), so the S4 gate
 *      (`evaluateLicenseGate`, @harborline-software/api-contracts) returns commercial `yes`. A
 *      Llama-community-license model would BLOCK fail-closed — which is WHY the
 *      floor is Qwen2.5 (Apache-2.0), NOT Llama ("don't drift the floor to Llama";
 *      the AI-weight-license sweep 2026-06-20 + ADR 0132). NO new license decision
 *      — the `llm` slot + its floor are pre-declared by ADR 0123 / 0132.
 *   2. The model+version that RIDES the result artifact (so a non-armed host's
 *      degraded stub self-identifies — a fake proposal can never pin as a genuine
 *      model's output; the no-mock-crypto family).
 *   3. The path resolution that decouples the floor from any one operator's
 *      install (env-overridable cache + interpreter), mirroring the embed floor's
 *      `resolveKgEmbedPaths`.
 *
 * The model is loaded OFFLINE from the local cache — consistent with the
 * no-egress sandbox. NO new capability KIND is added (`generate` reuses the ADR
 * 0123 `llm` slot vocabulary); NO autonomous form is built (that stays
 * broker-PEP-gated by ratified design).
 */

import { existsSync, readdirSync, realpathSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

import type { LicenseComponent } from '@harborline-software/api-contracts'

import { assertNoLegacyOperationalVariables } from './operational-environment.js'

/**
 * The KG-search GENERATION model floor — the S6 mandatory local floor for the
 * `generate` capability. Qwen2.5 weights are Apache-2.0 (the S4 gate's first
 * generation `weightsLicense ∧ engineLicense` entry that PASSES commercial
 * intent). A value, never a per-provider field.
 */
export interface KgGenerateFloor {
  /** The capability id this floor supplies (always `generate`). */
  readonly capabilityId: 'generate'
  /** The provider id (becomes the manifest id + the artifact `model`). */
  readonly id: string
  /** Human-readable name. */
  readonly name: string
  /** The upstream model repo this floor loads (offline, from the local cache). */
  readonly hfRepo: string
  /** The model version/revision that RIDES the result artifact. */
  readonly modelVersion: string
  /** The S4 engine-license component (the llama.cpp / transformers runtime that runs the weights). */
  readonly engineLicense: LicenseComponent
  /** The S4 weights-license component (the model weights). Apache-2.0 for the Qwen2.5 floor. */
  readonly weightsLicense: LicenseComponent
}

/**
 * The `generate` floor — Qwen2.5-7B-Instruct (Qwen/Qwen2.5-7B-Instruct),
 * Apache-2.0. A chat/instruct LLM that produces a grounded TEXT proposal. The
 * floor, NOT an accelerator (GPU/cloud LLMs are accelerators). The engine =
 * llama.cpp / transformers (permissive — declared commercial-`yes`; the gate's
 * MIN holds as long as engine is commercial-`yes`).
 *
 * Apache-2.0 is the load-bearing S4 PASS — a Llama-community-license or CC-BY-NC
 * model would BLOCK fail-closed here. The instruct variant (vs the embedding
 * floor's coder variant) is the quality pick for grounded Q&A; both are
 * Apache-2.0 (no license fork). Phi-3 (MIT) / Mistral-7B (Apache-2.0) are clean
 * alt floors swappable behind the manifest without a license decision.
 */
export const QWEN25_GENERATE_FLOOR: KgGenerateFloor = Object.freeze({
  capabilityId: 'generate',
  id: 'qwen2.5-7b-instruct',
  name: 'Qwen2.5-7B-Instruct (generate CPU floor)',
  hfRepo: 'Qwen/Qwen2.5-7B-Instruct',
  modelVersion: '1.0',
  // The inference engine (llama.cpp / transformers). A permissive commercial-`yes`
  // license; declared `yes` so the MIN holds.
  engineLicense: { role: 'engine', spdx: 'Apache-2.0', commercialUse: 'yes' } satisfies LicenseComponent,
  // Qwen2.5 weights are Apache-2.0 (the load-bearing S4 PASS — a Llama-community
  // or CC-BY-NC model would block fail-closed here).
  weightsLicense: { role: 'weights', spdx: 'Apache-2.0', commercialUse: 'yes' } satisfies LicenseComponent,
})

/**
 * The SELF-IDENTIFYING stub model sentinel a NON-armed runtime stamps on its
 * deterministic placeholder proposal — NEVER a registered floor id. Mirrors the
 * embed floor's `STUB_EMBED_MODEL` (bug-1358): a degraded host's stub proposal
 * can never be mistaken for a genuine LLM-grounded answer (the no-mock-crypto /
 * no-fake-as-real family). A consumer that needs a REAL grounded answer treats a
 * stub-tagged proposal as "floor unavailable", never as a real answer.
 */
export const STUB_GENERATE_MODEL = 'stub-qwen2.5'

/** Resolved paths the KG generate worker needs (env-overridable; cache-default). */
export interface KgGeneratePaths {
  /** The python interpreter that runs the worker script (must have the LLM runtime importable). */
  readonly interpreter: string
  /** The model cache root the LLM loads from OFFLINE (read-only under confinement). */
  readonly modelCache: string
  /** An optional extra venv site-packages dir injected onto the worker's sys.path. */
  readonly venvSite: string | null
}

/**
 * The default interpreter (the same torch/transformers venv the embed floor uses
 * — a later sub-slice bundles a self-contained llama.cpp runtime so the floor is
 * install-independent). An operator points {@link KgGeneratePaths.interpreter}
 * at a fleet-managed interpreter via `CAPABILITY_HOST_KG_GENERATE_PYTHON`.
 */
function defaultInterpreter(home: string): string {
  return join(home, 'stable-diffusion-webui', 'venv', 'bin', 'python3')
}

/**
 * Resolve a venv-python symlink to the REAL interpreter binary that gets exec'd
 * (mirrors `resolveWorkerInterpreter` in kg-embed-model.ts — the macOS framework
 * python is a launcher stub that re-execs the real interpreter, which the S7
 * exec-narrowing blocks; so `spec.command` MUST be the final binary).
 */
export function resolveGenerateInterpreter(interpreter: string): string {
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

/** Resolve a venv's `lib/pythonX.Y/site-packages` dir (where the LLM runtime lives). */
export function resolveGenerateVenvSitePackages(venvRoot: string): string | null {
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
 * Resolve the KG-generate worker paths. Every path is env-overridable with a
 * documented default; NONE is a credential store (the SEC-7 build-time guard
 * REFUSES any readOnlyPaths grant that is an ancestor of one, so this set is
 * proven safe by construction).
 */
export function resolveKgGeneratePaths(
  env: NodeJS.ProcessEnv = process.env,
  home: string = homedir(),
): KgGeneratePaths {
  assertNoLegacyOperationalVariables(env)
  const configuredPython = env.CAPABILITY_HOST_KG_GENERATE_PYTHON ?? defaultInterpreter(home)
  const interpreter = resolveGenerateInterpreter(configuredPython)
  const modelCache = env.CAPABILITY_HOST_KG_GENERATE_CACHE ?? join(home, '.cache', 'huggingface')
  const venvSiteEnv = env.CAPABILITY_HOST_KG_GENERATE_VENV_SITE
  let venvSite: string | null = null
  if (venvSiteEnv != null && existsSync(venvSiteEnv)) {
    venvSite = venvSiteEnv
  } else {
    const venvRoot = join(configuredPython, '..', '..')
    venvSite = resolveGenerateVenvSitePackages(venvRoot)
  }
  return { interpreter, modelCache, venvSite }
}

// (the embed-floor module owns the `realKgEmbedOptedIn` opt-in gate; this one mirrors it below for `generate`.)

/**
 * Whether the cached LLM is present (a cheap pre-check before arming the real
 * engine — a missing model ⇒ graceful degradation to the deterministic stub,
 * never a hang). Checks the HF-cache `models--<org>--<name>` dir for the floor.
 */
export function kgGenerateModelPresent(paths: KgGeneratePaths): boolean {
  const hubDir = join(paths.modelCache, 'hub')
  const cacheDir = join(hubDir, `models--${QWEN25_GENERATE_FLOOR.hfRepo.replace('/', '--')}`)
  return existsSync(cacheDir) && existsSync(paths.interpreter)
}

/** Whether the real KG-generate path is opted-in (real LLM inference is slow — off by default for CI). */
export function realKgGenerateOptedIn(env: NodeJS.ProcessEnv = process.env): boolean {
  assertNoLegacyOperationalVariables(env)
  const v = env.CAPABILITY_HOST_KG_GENERATE_REAL
  return v === '1' || v === 'true'
}
