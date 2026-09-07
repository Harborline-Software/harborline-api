/**
 * Unit tests for the S6 digest-pin + configurable acquisition seam
 * (`image-floor-model.ts`). These run in CI on every platform — no host gating,
 * no real model — by hashing a small temp file against an injected pin.
 */

import { createHash } from 'node:crypto'
import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { describe, expect, it } from 'vitest'

import {
  IMAGE_FLOOR_MODEL,
  type ImageFloorModel,
  modelSizeMatches,
  resolveImageFloorPaths,
  sha256File,
  verifyImageFloorModel,
} from './image-floor-model.js'

const DIR = mkdtempSync(join(tmpdir(), 'capability-model-test-'))

/** Write `content` to a temp file + return its path + a pin matching its bytes/digest. */
function plantModel(content: string, name = 'model.safetensors'): { file: string; pin: ImageFloorModel } {
  const file = join(DIR, name)
  writeFileSync(file, content)
  const bytes = Buffer.byteLength(content)
  const sha256 = createHash('sha256').update(content).digest('hex')
  const pin: ImageFloorModel = {
    id: 'test-model',
    fileName: name,
    sha256,
    bytes,
    sourceUrl: 'https://example.invalid/model',
    weightsSpdx: 'MIT',
  }
  return { file, pin }
}

describe('image-floor-model — configurable path resolution', () => {
  it('defaults to the A1111/Forge layout when no env override is set', () => {
    const home = '/home/op'
    const webUi = join(home, 'stable-diffusion-webui')
    const paths = resolveImageFloorPaths({}, home)
    expect(paths.modelDir).toBe(join(webUi, 'models', 'Stable-diffusion'))
    expect(paths.modelFile).toBe(join(webUi, 'models', 'Stable-diffusion', IMAGE_FLOOR_MODEL.fileName))
    expect(paths.venvPython).toBe(join(webUi, 'venv', 'bin', 'python3'))
    expect(paths.hfCache).toBe(join(home, '.cache', 'huggingface'))
  })

  it('honors every env override (the decoupling from any one install)', () => {
    const paths = resolveImageFloorPaths(
      {
        CAPABILITY_HOST_IMAGE_MODEL_DIR: '/fleet/models',
        CAPABILITY_HOST_IMAGE_PYTHON: '/opt/py/bin/python3',
        CAPABILITY_HOST_IMAGE_VENV: '/opt/venv',
        CAPABILITY_HOST_IMAGE_HF_CACHE: '/fleet/hf',
      },
      '/home/op',
    )
    expect(paths.modelDir).toBe('/fleet/models')
    expect(paths.modelFile).toBe(join('/fleet/models', IMAGE_FLOOR_MODEL.fileName))
    expect(paths.venvPython).toBe('/opt/py/bin/python3')
    expect(paths.venvRoot).toBe('/opt/venv')
    expect(paths.hfCache).toBe('/fleet/hf')
  })

  it('the pinned floor model is the SD-1.5 emaonly checkpoint (S6 pin present)', () => {
    expect(IMAGE_FLOOR_MODEL.fileName).toBe('v1-5-pruned-emaonly.safetensors')
    expect(IMAGE_FLOOR_MODEL.sha256).toMatch(/^[0-9a-f]{64}$/)
    expect(IMAGE_FLOOR_MODEL.bytes).toBeGreaterThan(4_000_000_000)
    expect(IMAGE_FLOOR_MODEL.weightsSpdx).toBe('CreativeML-OpenRAIL-M')
  })
})

describe('image-floor-model — S6 digest verification', () => {
  it('verifies a model whose bytes match the pin', async () => {
    const { file, pin } = plantModel('the-pinned-weights-bytes', 'ok.safetensors')
    const v = await verifyImageFloorModel(file, pin)
    expect(v.ok).toBe(true)
    if (v.ok) expect(v.sha256).toBe(pin.sha256)
  })

  it('rejects an ABSENT model (graceful-degradation trigger)', async () => {
    const v = await verifyImageFloorModel(join(DIR, 'nope.safetensors'), IMAGE_FLOOR_MODEL)
    expect(v.ok).toBe(false)
    if (!v.ok) expect(v.reason).toBe('absent')
  })

  it('rejects a WRONG-SIZED model fast (before hashing)', async () => {
    const { file, pin } = plantModel('short', 'short.safetensors')
    const v = await verifyImageFloorModel(file, { ...pin, bytes: pin.bytes + 999 })
    expect(v.ok).toBe(false)
    if (!v.ok) expect(v.reason).toBe('size_mismatch')
  })

  it('rejects a TAMPERED (right-sized, wrong-digest) model — the load-bearing pin', async () => {
    const { file, pin } = plantModel('exactly-32-bytes-of-payload!!!!!', 'tamper.safetensors')
    // Same byte count, different content → a digest mismatch the size check misses.
    const tamperedPin = { ...pin, sha256: 'f'.repeat(64) }
    const v = await verifyImageFloorModel(file, tamperedPin)
    expect(v.ok).toBe(false)
    if (!v.ok) expect(v.reason).toBe('digest_mismatch')
  })

  it('the size-only fast path skips the hash (acquisition resume)', async () => {
    const { file, pin } = plantModel('content', 'fast.safetensors')
    const wrongDigest = { ...pin, sha256: 'a'.repeat(64) }
    // hash:false → size matches → ok WITHOUT noticing the wrong digest.
    const v = await verifyImageFloorModel(file, wrongDigest, { hash: false })
    expect(v.ok).toBe(true)
  })

  it('modelSizeMatches is the cheap engine-build pre-check', () => {
    const { file, pin } = plantModel('sized', 'sized.safetensors')
    expect(modelSizeMatches(file, pin)).toBe(true)
    expect(modelSizeMatches(file, { ...pin, bytes: 1 })).toBe(false)
    expect(modelSizeMatches(join(DIR, 'absent'), pin)).toBe(false)
  })

  it('sha256File streams a hex digest matching node:crypto', async () => {
    const { file } = plantModel('stream-me', 'stream.safetensors')
    const expected = createHash('sha256').update('stream-me').digest('hex')
    expect(await sha256File(file)).toBe(expected)
  })
})
