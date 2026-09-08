import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

import { describe, expect, it } from 'vitest'

import { devNodeSigningKey, resetDevNodeSigningKeyForTests } from '../membrane/host-principal-signer.js'
import { realImageOptedIn } from './cpu-image-floor-runtime.js'
import { resolveImageFloorPaths } from './image-floor-model.js'
import { realKgEmbedOptedIn, resolveKgEmbedPaths } from './kg-embed-model.js'
import { realKgGenerateOptedIn, resolveKgGeneratePaths } from './kg-generate-model.js'
import { assertNoLegacyOperationalVariables } from './operational-environment.js'

const LEGACY_PREFIX = 'HULL' + '_'
const LEGACY_NAMES = [
  'DEV',
  'IMAGE_HF_CACHE',
  'IMAGE_MODEL',
  'IMAGE_MODEL_DIR',
  'IMAGE_PYTHON',
  'IMAGE_REAL',
  'IMAGE_VENV',
  'IMAGE_VENV_SITE',
  'KG_EMBED_HF_CACHE',
  'KG_EMBED_PYTHON',
  'KG_EMBED_REAL',
  'KG_EMBED_REPO',
  'KG_EMBED_VENV_SITE',
  'KG_GENERATE_CACHE',
  'KG_GENERATE_PYTHON',
  'KG_GENERATE_REAL',
  'KG_GENERATE_REPO',
  'KG_GENERATE_VENV_SITE',
  'KG_RERANK_REPO',
  'MEMBRANE_TRACE_ID',
  'NODE_SEED_HEX',
].map((suffix) => LEGACY_PREFIX + suffix)

const TS_ENVIRONMENT_READERS: ReadonlyArray<readonly [string, (env: NodeJS.ProcessEnv) => unknown]> = [
  ['image path resolver', (env) => resolveImageFloorPaths(env, '/ticket-245')],
  ['image real opt-in', (env) => realImageOptedIn(env)],
  ['KG embed path resolver', (env) => resolveKgEmbedPaths(env, '/ticket-245')],
  ['KG embed real opt-in', (env) => realKgEmbedOptedIn(env)],
  ['KG generation path resolver', (env) => resolveKgGeneratePaths(env, '/ticket-245')],
  ['KG generation real opt-in', (env) => realKgGenerateOptedIn(env)],
  ['host principal signer', (env) => {
    resetDevNodeSigningKeyForTests()
    return devNodeSigningKey(env)
  }],
]

const PYTHON_WORKERS = ['sd_local.py', 'kg_embed.py', 'kg_eval.py', 'kg_generate.py']
  .map((name) => fileURLToPath(new URL(name, import.meta.url)))

function environmentWith(legacyName: string): NodeJS.ProcessEnv {
  const environment = Object.fromEntries(
    Object.entries(process.env).filter(([name]) => !name.startsWith(LEGACY_PREFIX)),
  ) as NodeJS.ProcessEnv
  return { ...environment, [legacyName]: '1' }
}

function diagnostic(legacyName: string): string {
  return `Legacy capability-host environment variable ${legacyName} is not supported; use CAPABILITY_HOST_${legacyName.slice(LEGACY_PREFIX.length)}.`
}

describe('capability-host operational environment clean break', () => {
  it('refuses an old operational name and identifies its replacement', () => {
    const legacyName = 'HULL_' + 'IMAGE_REAL'
    expect(() => assertNoLegacyOperationalVariables({ [legacyName]: '1' })).toThrow(
      `Legacy capability-host environment variable ${legacyName} is not supported; use CAPABILITY_HOST_IMAGE_REAL.`,
    )
  })

  it('accepts the CAPABILITY_HOST replacement surface', () => {
    expect(() =>
      assertNoLegacyOperationalVariables({ CAPABILITY_HOST_IMAGE_REAL: '1' }),
    ).not.toThrow()
  })

  describe.each(TS_ENVIRONMENT_READERS)('%s', (_name, readEnvironment) => {
    it.each(LEGACY_NAMES)('refuses %s before reading defaults', (legacyName) => {
      expect(() => readEnvironment({ [legacyName]: '1' })).toThrow(diagnostic(legacyName))
    })
  })

  it.each(LEGACY_NAMES)('acquisition entry exits non-zero for %s', (legacyName) => {
    const entry = fileURLToPath(new URL('../../scripts/acquire-image-floor-model.mjs', import.meta.url))
    const result = spawnSync(process.execPath, [entry, '--verify-only'], {
      encoding: 'utf8',
      env: environmentWith(legacyName),
    })
    expect(result.status).not.toBe(0)
    expect(result.stderr).toContain(diagnostic(legacyName))
  })

  // One row per worker (21 interpreter spawns each) rather than 84 spawns in one 30 s test: under a loaded
  // machine the single test timed out in the exact-clone gate twice on 2026-09-04 while passing natively.
  it.each(PYTHON_WORKERS)('standalone Python worker %s refuses every legacy name before --help', (worker) => {
    const python = process.platform === 'win32' ? 'python' : 'python3'
    for (const legacyName of LEGACY_NAMES) {
      const result = spawnSync(python, [worker, '--help'], {
        encoding: 'utf8',
        env: environmentWith(legacyName),
      })
      expect(result.status, `${worker} accepted ${legacyName}`).not.toBe(0)
      expect(result.stderr, `${worker} diagnostic for ${legacyName}`).toContain(diagnostic(legacyName))
    }
  }, 120_000)
})
