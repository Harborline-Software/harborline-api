#!/usr/bin/env node
/**
 * acquire-image-floor-model.mjs — the documented, digest-pinned acquisition
 * SCAFFOLD for the Capability CPU image floor model (ADR 0123 §S6).
 *
 * The ~4 GB SD-1.5 weights cannot live in git, so the floor acquires them
 * deterministically + verifies them against the pin in `image-floor-model.ts`.
 * This script is the download-on-first-run channel: fetch (resumable) → verify
 * the sha256 against the single-sourced pin → place under the model dir.
 *
 * DISTRIBUTION-CHANNEL NOTE (flagged for CIC — see the PR body): whether the fleet
 * ships the floor model via (a) THIS download-on-first-run, (b) an installer-bundled
 * asset, or (c) git-LFS is a fleet decision. This script implements channel (a) as
 * the working default + the channel-independent VERIFY half (`--verify-only`) that
 * every channel reuses. The pin itself (`IMAGE_FLOOR_MODEL.sha256`) is the integrity
 * floor regardless of channel.
 *
 * Usage:
 *   node scripts/acquire-image-floor-model.mjs            # fetch (resume) + verify
 *   node scripts/acquire-image-floor-model.mjs --verify-only   # verify on-disk only
 *   CAPABILITY_HOST_IMAGE_MODEL_DIR=/models node scripts/acquire-image-floor-model.mjs
 *
 * Honors CAPABILITY_HOST_IMAGE_MODEL_DIR (the same env the runtime resolves). Idempotent: a
 * present + verified model is a no-op; a partial download resumes via HTTP Range.
 */

import { createHash } from 'node:crypto'
import { createReadStream, createWriteStream, existsSync, mkdirSync, statSync } from 'node:fs'
import { readFile } from 'node:fs/promises'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { assertNoLegacyOperationalVariables } from './operational-environment.mjs'

const HERE = dirname(fileURLToPath(import.meta.url))

/**
 * Single-source the pin from `src/runtime/image-floor-model.ts` (no build needed,
 * no duplicated constant). A tiny field-parse over the source — if the shape ever
 * drifts, this throws loudly rather than acquiring against a stale pin.
 */
async function loadPin() {
  const src = await readFile(join(HERE, '..', 'src', 'runtime', 'image-floor-model.ts'), 'utf8')
  const field = (name) => {
    const m = src.match(new RegExp(`${name}:\\s*'([^']+)'`))
    if (m == null) throw new Error(`acquire: could not parse '${name}' from image-floor-model.ts`)
    return m[1]
  }
  const bytesMatch = src.match(/bytes:\s*([\d_]+)/)
  if (bytesMatch == null) throw new Error("acquire: could not parse 'bytes' from image-floor-model.ts")
  return {
    fileName: field('fileName'),
    sha256: field('sha256'),
    bytes: Number(bytesMatch[1].replace(/_/g, '')),
    sourceUrl: field('sourceUrl'),
  }
}

function modelDir() {
  return (
    process.env.CAPABILITY_HOST_IMAGE_MODEL_DIR ??
    join(homedir(), 'stable-diffusion-webui', 'models', 'Stable-diffusion')
  )
}

function sha256File(file) {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256')
    const stream = createReadStream(file)
    stream.on('error', reject)
    stream.on('data', (c) => hash.update(c))
    stream.on('end', () => resolve(hash.digest('hex')))
  })
}

async function verify(dest, pin) {
  if (!existsSync(dest)) return { ok: false, reason: `absent: ${dest}` }
  const size = statSync(dest).size
  if (size !== pin.bytes) return { ok: false, reason: `size ${size} != pinned ${pin.bytes}` }
  process.stdout.write('  hashing (4 GB, constant memory)… ')
  const actual = await sha256File(dest)
  if (actual !== pin.sha256) return { ok: false, reason: `sha256 ${actual} != pinned ${pin.sha256}` }
  return { ok: true }
}

/** Resumable HTTP download via Range. Streams to disk — never buffers the 4 GB. */
async function download(url, dest, pin) {
  const have = existsSync(dest) ? statSync(dest).size : 0
  if (have >= pin.bytes) return // already complete (verify decides if it's valid)
  const headers = have > 0 ? { Range: `bytes=${have}-` } : {}
  if (have > 0) console.log(`  resuming from byte ${have}…`)
  const res = await fetch(url, { headers })
  if (!res.ok && res.status !== 206) {
    throw new Error(`acquire: download failed — HTTP ${res.status} ${res.statusText}`)
  }
  const out = createWriteStream(dest, { flags: have > 0 ? 'a' : 'w' })
  const { Readable } = await import('node:stream')
  const { pipeline } = await import('node:stream/promises')
  await pipeline(Readable.fromWeb(res.body), out)
}

async function main() {
  assertNoLegacyOperationalVariables()
  const verifyOnly = process.argv.includes('--verify-only')
  const pin = await loadPin()
  const dir = modelDir()
  const dest = join(dir, pin.fileName)

  console.log(`Capability CPU image floor model: ${pin.fileName}`)
  console.log(`  dir:    ${dir}`)
  console.log(`  pin:    sha256 ${pin.sha256} (${pin.bytes} bytes)`)

  const pre = await verify(dest, pin)
  if (pre.ok) {
    console.log('  ✓ already present + digest-verified — nothing to do.')
    return
  }
  if (verifyOnly) {
    console.error(`  ✗ verify-only: ${pre.reason}`)
    process.exitCode = 1
    return
  }

  mkdirSync(dir, { recursive: true })
  console.log(`  acquiring from ${pin.sourceUrl}`)
  await download(pin.sourceUrl, dest, pin)

  const post = await verify(dest, pin)
  if (!post.ok) {
    console.error(`  ✗ post-download verification FAILED: ${post.reason}`)
    console.error('    (the floor will degrade to the reference stub — it never loads an unpinned model)')
    process.exitCode = 1
    return
  }
  console.log('  ✓ downloaded + digest-verified. The CPU image floor is armed (with CAPABILITY_HOST_IMAGE_REAL=1).')
}

main().catch((err) => {
  console.error(`acquire-image-floor-model: ${err instanceof Error ? err.message : String(err)}`)
  process.exitCode = 1
})
