/**
 * The CPU image-floor MODEL contract — the S6 digest-pin + the configurable
 * acquisition seam that decouples the floor from any one operator's A1111/Forge
 * install (ADR 0123 §S6: "mandatory bundleable LOCAL FLOOR … + content-digest
 * pinning + load-time validation").
 *
 * BEFORE this module the floor hard-coded the model + interpreter + venv to
 * `~/stable-diffusion-webui/…` (the author's A1111 install). That made the floor
 * non-self-contained — it only worked on a host with A1111 already installed at
 * that exact absolute path, and it loaded WHATEVER `.safetensors` sat there with
 * no integrity check (a tampered/swapped checkpoint would have rendered silently).
 *
 * This module fixes both:
 *   1. CONFIGURABILITY — every path is resolved from an env var with a documented
 *      default, so an operator points the floor at a fleet-managed model dir / a
 *      bundled interpreter without editing code. The A1111 layout remains the
 *      fallback default so existing dev hosts keep working with zero config.
 *   2. DIGEST-PIN (S6) — the floor model is pinned to a known sha256. The runtime
 *      verifies the on-disk bytes against this pin at engine-build time; a missing
 *      OR mismatching model means the engine is NOT built (→ graceful degradation
 *      to the deterministic reference stub). The floor NEVER renders with an
 *      unpinned or tampered checkpoint.
 *
 * DISTRIBUTION CHANNEL (flagged for CIC — NOT decided here): the ~4 GB weights
 * cannot live in git. This module pins the digest + a canonical source URL and
 * ships an acquisition SCAFFOLD (`scripts/acquire-image-floor-model.mjs`) that
 * download-on-first-run-fetches + verifies against {@link IMAGE_FLOOR_MODEL}.
 * Whether the fleet ships via (a) that download-on-first-run, (b) an
 * installer-bundled asset, or (c) git-LFS is a fleet decision left to CIC. The
 * VERIFICATION half is implemented now regardless of which channel wins — the
 * digest pin is the channel-independent integrity floor.
 */

import { createHash } from 'node:crypto'
import { createReadStream, existsSync, readdirSync, statSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

import { assertNoLegacyOperationalVariables } from './operational-environment.js'

/**
 * The pinned CPU image-floor model (S6 digest-pin). v1 floor = Stable-Diffusion
 * 1.5 `v1-5-pruned-emaonly.safetensors` — the canonical CreativeML-OpenRAIL-M
 * checkpoint (the `weightsLicense` arm declared `unknown` so the membrane's
 * MIN-over-components license gate stays honest; a future MIT/CC0 floor model
 * lifts that AND swaps this pin).
 *
 * `sha256` is the canonical RunwayML/published digest of the emaonly checkpoint,
 * verified against the on-disk bytes on this host (2026-06-18). It is the
 * channel-independent integrity floor: however the bytes are acquired (download /
 * installer / LFS), they MUST match this digest or the floor degrades to the stub.
 */
export interface ImageFloorModel {
  /** Human-readable model id (for logs / the acquisition scaffold). */
  readonly id: string
  /** The canonical filename the floor looks for under the model dir. */
  readonly fileName: string
  /** The pinned sha256 (hex) of the model bytes — the S6 integrity pin. */
  readonly sha256: string
  /** The expected byte size (a cheap pre-check before the full hash). */
  readonly bytes: number
  /** A canonical source URL for the acquisition scaffold (NOT fetched by the runtime). */
  readonly sourceUrl: string
  /** The SPDX license of the weights (drives the membrane MIN-over-components gate). */
  readonly weightsSpdx: string
}

/** The pinned v1 CPU image floor model. */
export const IMAGE_FLOOR_MODEL: ImageFloorModel = Object.freeze({
  id: 'sd15-v1-5-pruned-emaonly',
  fileName: 'v1-5-pruned-emaonly.safetensors',
  sha256: '6ce0161689b3853acaa03779ec93eafe75a02f4ced659bee03f50797806fa2fa',
  bytes: 4_265_146_304,
  // Canonical mirror of the emaonly checkpoint (Hugging Face). The acquisition
  // scaffold resolves the actual download URL; this is the provenance pointer.
  sourceUrl:
    'https://huggingface.co/stable-diffusion-v1-5/stable-diffusion-v1-5/resolve/main/v1-5-pruned-emaonly.safetensors',
  weightsSpdx: 'CreativeML-OpenRAIL-M',
})

/**
 * The resolved, configurable filesystem locations the CPU image floor needs.
 * Every field defaults to the A1111/Forge layout (so existing dev hosts keep
 * working with zero config) but is overridable via an env var — the decoupling
 * the directive asks for. NONE of these is a credential store; the SEC-7
 * build-time guard still refuses any grant that is an ancestor of one.
 *
 * Env vars (all optional; documented in the floor README):
 *   CAPABILITY_HOST_IMAGE_MODEL_DIR  — dir holding the `.safetensors` checkpoint
 *   CAPABILITY_HOST_IMAGE_PYTHON     — the venv `python3` symlink (resolved to the real binary)
 *   CAPABILITY_HOST_IMAGE_VENV       — the venv root (granted read-only for site-packages)
 *   CAPABILITY_HOST_IMAGE_HF_CACHE   — the Hugging Face cache (granted read-only, used OFFLINE)
 */
export interface ImageFloorPaths {
  /** Absolute path to the model `.safetensors` file. */
  readonly modelFile: string
  /** Absolute path to the directory the model file lives in (the read-only grant). */
  readonly modelDir: string
  /** Absolute path to the venv `python3` symlink (resolved later to the real binary). */
  readonly venvPython: string
  /** Absolute path to the venv root (read-only grant for site-packages). */
  readonly venvRoot: string
  /** Absolute path to the Hugging Face cache (read-only grant, used OFFLINE). */
  readonly hfCache: string
}

/** The A1111/Forge default install root (the fallback when no env override is set). */
function a1111Root(home: string): string {
  return join(home, 'stable-diffusion-webui')
}

/**
 * Resolve the venv's `site-packages` dir (where torch/diffusers live) under a venv
 * root — i.e. `lib/python3.X/site-packages` beneath `venvRoot`. The capability runtime
 * resolves the venv `python3` symlink to the REAL framework binary for the F1
 * seatbelt narrowing — but running that raw binary does NOT auto-activate the venv
 * (no `pyvenv.cfg` detection), so the venv's `site-packages` must be injected onto
 * `sys.path` explicitly. This finds it so the runtime can pass it via
 * `CAPABILITY_HOST_IMAGE_VENV_SITE`. Returns null when no such dir exists under the venv root.
 */
export function resolveVenvSitePackages(venvRoot: string): string | null {
  const libDir = join(venvRoot, 'lib')
  if (!existsSync(libDir)) return null
  let entries: string[]
  try {
    entries = readdirSync(libDir)
  } catch {
    return null
  }
  // Prefer the highest `python3.X` dir (a venv has exactly one in practice).
  const pyDirs = entries.filter((e) => /^python3(\.\d+)?$/.test(e)).sort().reverse()
  for (const py of pyDirs) {
    const sp = join(libDir, py, 'site-packages')
    if (existsSync(sp)) return sp
  }
  return null
}

/**
 * Resolve the CPU image floor's filesystem paths from the environment, defaulting
 * to the A1111/Forge layout. Pure: reads `env` + `home`, returns the resolved set
 * (no filesystem mutation, no existence check — that is the engine builder's job).
 */
export function resolveImageFloorPaths(
  env: NodeJS.ProcessEnv = process.env,
  home: string = homedir(),
): ImageFloorPaths {
  assertNoLegacyOperationalVariables(env)
  const root = a1111Root(home)
  const modelDir =
    env.CAPABILITY_HOST_IMAGE_MODEL_DIR ?? join(root, 'models', 'Stable-diffusion')
  const venvRoot = env.CAPABILITY_HOST_IMAGE_VENV ?? join(root, 'venv')
  const venvPython = env.CAPABILITY_HOST_IMAGE_PYTHON ?? join(venvRoot, 'bin', 'python3')
  const hfCache = env.CAPABILITY_HOST_IMAGE_HF_CACHE ?? join(home, '.cache', 'huggingface')
  return {
    modelFile: join(modelDir, IMAGE_FLOOR_MODEL.fileName),
    modelDir,
    venvPython,
    venvRoot,
    hfCache,
  }
}

/** The outcome of verifying the on-disk model against the digest pin. */
export type ModelVerification =
  | { readonly ok: true; readonly sha256: string }
  | { readonly ok: false; readonly reason: 'absent' | 'size_mismatch' | 'digest_mismatch'; readonly detail: string }

/**
 * Cheap pre-check: the model file exists AND its byte size matches the pin. A
 * size mismatch is a fast reject (no point hashing 4 GB to confirm it differs).
 * Used both as the fast path in {@link verifyImageFloorModel} and standalone by
 * the acquisition scaffold's resume logic.
 */
export function modelSizeMatches(modelFile: string, model: ImageFloorModel = IMAGE_FLOOR_MODEL): boolean {
  if (!existsSync(modelFile)) return false
  try {
    return statSync(modelFile).size === model.bytes
  } catch {
    return false
  }
}

/**
 * Verify the on-disk model bytes against the {@link IMAGE_FLOOR_MODEL} digest pin
 * (S6 load-time validation). Streams the file through sha256 (constant memory —
 * never buffers the 4 GB). Returns a discriminated result so the engine builder
 * can degrade gracefully (absent/mismatch → stub) and a caller can log WHY.
 *
 * `expensive` is opt-out-able for the size-only fast path: the default does the
 * full hash (the integrity guarantee); passing `{ hash: false }` does only the
 * existence + size check (the acquisition scaffold uses this to decide whether to
 * re-download before paying the full hash).
 */
export async function verifyImageFloorModel(
  modelFile: string,
  model: ImageFloorModel = IMAGE_FLOOR_MODEL,
  opts: { readonly hash?: boolean } = {},
): Promise<ModelVerification> {
  if (!existsSync(modelFile)) {
    return { ok: false, reason: 'absent', detail: `model file not found: ${modelFile}` }
  }
  let size: number
  try {
    size = statSync(modelFile).size
  } catch (err) {
    return { ok: false, reason: 'absent', detail: `cannot stat model: ${err instanceof Error ? err.message : String(err)}` }
  }
  if (size !== model.bytes) {
    return {
      ok: false,
      reason: 'size_mismatch',
      detail: `model size ${size} != pinned ${model.bytes} (${modelFile})`,
    }
  }
  if (opts.hash === false) {
    // Size-only fast path (acquisition resume): treat a size match as provisionally
    // ok WITHOUT the full hash. NOT the integrity guarantee — callers that need the
    // pin must call with the default (hash on).
    return { ok: true, sha256: model.sha256 }
  }
  const actual = await sha256File(modelFile)
  if (actual !== model.sha256) {
    return {
      ok: false,
      reason: 'digest_mismatch',
      detail: `model sha256 ${actual} != pinned ${model.sha256} — refusing to load a tampered/unpinned checkpoint (${modelFile})`,
    }
  }
  return { ok: true, sha256: actual }
}

/** Stream a file through sha256, hex digest. Constant memory (never buffers the file). */
export function sha256File(file: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256')
    const stream = createReadStream(file)
    stream.on('error', reject)
    stream.on('data', (chunk) => hash.update(chunk))
    stream.on('end', () => resolve(hash.digest('hex')))
  })
}
