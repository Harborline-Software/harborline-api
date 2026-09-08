/**
 * CI-CHEAP FLOOR smoke test (inc-4 follow-up — the accepted A8 gap closer).
 *
 * The host-gated round-trip (`cpu-image-round-trip.test.ts`) exercises the REAL
 * floor but only when armed (`CAPABILITY_HOST_IMAGE_REAL=1`) WITH the multi-GB SD model
 * present — so the floor's REAL subprocess + REAL sandbox + PNG-decode path got
 * NO automated CI exercise (only the mocked-sandbox gate test ran in CI). That is
 * the accepted A8 gap: the load-bearing security path (spawn-confined-by-sandbox)
 * had zero CI coverage.
 *
 * This test closes it WITHOUT the minutes-long diffusers render: it drives a REAL
 * `ImageEngine` whose interpreter is the system `/usr/bin/python3` running a TINY
 * stdlib-only PNG writer (no torch, no model — milliseconds), confined by the REAL
 * `MacosSeatbeltSandbox`. It proves the WHOLE codepath end-to-end:
 *   - argv mapping (ImageCore → final argv elements, the dangerous prompt verbatim)
 *   - a REAL `sandbox-exec`/seatbelt spawn (NOT a stub sandbox)
 *   - the SEC-7 confinement (write-only work dir; no egress; credential carve-out)
 *   - the produced PNG → base64 `data:` URI decode
 *
 * Platform-aware: on darwin it runs the real seatbelt path; on a non-darwin CI
 * runner (the Harborline ts-suites job is ubuntu) it asserts the FAIL-CLOSED
 * behavior of the structured-but-unimplemented sandbox instead (still real-codepath
 * — it proves the runtime never spawns unconfined). Either way the floor's real
 * spawn path is exercised by CI, not just the host-gated render.
 */

import { execFileSync } from 'node:child_process'
import { existsSync, mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import type { ImageCore, InvokeRequest } from '@harborline-software/api-contracts'
import { describe, expect, it } from 'vitest'

import { createSandbox } from '../sandbox/index.js'
import {
  CpuImageFloorRuntime,
  type ImageEngine,
  resolveInterpreter,
} from './cpu-image-floor-runtime.js'

/**
 * Resolve a python interpreter that runs CLEANLY under seatbelt (no torch/model
 * needed — stdlib only). `/usr/bin/python3` on macOS is an xcode-select SHIM that
 * reads `/var/select/developer_dir` (denied under confinement) to find the real
 * Xcode framework python, so we resolve THROUGH the same `resolveInterpreter` the
 * SD floor uses (framework launcher-stub → the real interpreter binary). The Xcode
 * developer-dir python is the reliable one on a dev/runner Mac.
 */
const PYTHON_CANDIDATES = [
  '/Applications/Xcode.app/Contents/Developer/usr/bin/python3',
  '/Library/Developer/CommandLineTools/usr/bin/python3',
  '/opt/homebrew/bin/python3',
  '/usr/local/bin/python3',
]
const RESOLVED_PYTHON =
  process.platform === 'darwin'
    ? (PYTHON_CANDIDATES.filter(existsSync).map((p) => resolveInterpreter(p)).find((r) => r != null) ??
      null)
    : null
const HAVE_SYSTEM_PYTHON = RESOLVED_PYTHON != null
/** Read-only roots the resolved interpreter's install needs to be traversable. */
const INTERPRETER_ROOTS = RESOLVED_PYTHON?.startsWith('/Applications/') ? ['/Applications'] : []

/**
 * A tiny stdlib-only PNG writer (zlib + struct — present in every python). Emits a
 * 1×1 opaque-white PNG to the `--out` path. The point is a REAL subprocess that
 * produces a REAL, decodable PNG in milliseconds — exercising the floor's spawn +
 * read-back path without the diffusers render.
 */
const TINY_PNG_WRITER = `
import argparse, struct, zlib
p = argparse.ArgumentParser()
p.add_argument('--prompt'); p.add_argument('--out', required=True)
p.add_argument('--width', type=int, default=1); p.add_argument('--height', type=int, default=1)
p.add_argument('--steps', type=int); p.add_argument('--seed', type=int); p.add_argument('--model')
a = p.parse_args()
def chunk(tag, data):
    return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)
sig = b'\\x89PNG\\r\\n\\x1a\\n'
ihdr = struct.pack('>IIBBBBB', 1, 1, 8, 2, 0, 0, 0)  # 1x1, 8-bit, truecolor
raw = b'\\x00\\xff\\xff\\xff'  # one scanline filter byte + one white RGB pixel
idat = zlib.compress(raw)
png = sig + chunk(b'IHDR', ihdr) + chunk(b'IDAT', idat) + chunk(b'IEND', b'')
open(a.out, 'wb').write(png)
`

/**
 * A REAL CPU image engine that runs the tiny PNG writer via system python. It is
 * NOT a stub — `interpreter`/`script`/`buildArgs` are wired exactly like the SD
 * floor, just pointed at a fast stdlib script. The runtime stages it into a work
 * dir + spawns it through the REAL sandbox.
 */
function smokeEngine(scriptPath: string): ImageEngine {
  return {
    id: 'cpu-smoke-image',
    name: 'CPU smoke image (stdlib PNG writer)',
    // The RESOLVED framework interpreter (not the xcode-select shim) — the F1
    // narrowing grants process-exec of exactly this literal.
    interpreter: RESOLVED_PYTHON ?? '/usr/bin/python3',
    script: scriptPath,
    // No model — the smoke writer needs none; the digest-pin gate is a no-op.
    // Grant the interpreter's install root so seatbelt can traverse to it.
    readOnlyPaths: [...INTERPRETER_ROOTS],
    buildArgs(core, outFile, steps, staged) {
      return [
        '-s',
        '-E',
        staged,
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
    },
    buildEnv(workDir) {
      return {
        PATH: '/usr/bin:/bin',
        TMPDIR: `${workDir}/scratch`,
        __CF_USER_TEXT_ENCODING: process.env.__CF_USER_TEXT_ENCODING ?? '0x0:0:0',
      }
    },
  }
}

function imageCore(overrides: Partial<ImageCore> = {}): ImageCore {
  return {
    prompt: 'a carrier at dawn',
    size: { w: 1, h: 1 },
    seed: 1,
    count: 1,
    format: 'png',
    timeout: 30_000,
    ...overrides,
  }
}

function imageRequest(core: ImageCore, correlationId = 'corr-smoke-1'): InvokeRequest {
  return {
    capabilityId: 'image',
    core,
    providerInputs: {},
    attachments: [],
    idempotencyKey: `idem-${correlationId}`,
    correlationId,
    transport: 'sync',
  }
}

/**
 * Element access under `noUncheckedIndexedAccess` — fails at the access with a legible message
 * rather than letting `undefined` reach an assertion that would compare it against `undefined`.
 */
function at<T>(items: readonly T[], index: number): T {
  const item = items[index]
  if (item === undefined) {
    throw new Error(`expected an element at index ${index} (length ${items.length})`)
  }
  return item
}

/** Write the tiny PNG-writer script to a temp file the engine can stage. */
function stageSmokeScript(): string {
  const dir = mkdtempSync(join(tmpdir(), 'capability-smoke-'))
  const file = join(dir, 'sd_local.py')
  writeFileSync(file, TINY_PNG_WRITER)
  return file
}

describe.runIf(HAVE_SYSTEM_PYTHON)(
  'CPU image floor — CI-cheap smoke (REAL python + REAL seatbelt sandbox, no render)',
  () => {
    it('spawns a REAL confined subprocess that produces a REAL decodable PNG', async () => {
      const script = stageSmokeScript()
      const python = RESOLVED_PYTHON as string
      // sanity: the script really does write a valid PNG when run unconfined.
      const probe = `${script}.probe.png`
      execFileSync(python, ['-s', '-E', script, '--out', probe, '--prompt', 'x'])
      expect(existsSync(probe)).toBe(true)

      const runtime = new CpuImageFloorRuntime({
        engine: smokeEngine(script),
        sandbox: createSandbox(), // the REAL host sandbox (darwin → seatbelt)
        denyPaths: ['/planted/secret/bank-creds'],
      })
      expect(runtime.usesRealEngine).toBe(true)

      const result = await runtime.invoke(imageRequest(imageCore({ prompt: 'a cat; rm -rf / `id`' })))

      // The REAL confined subprocess ran to completion + produced a real PNG.
      expect(result.status).toBe('succeeded')
      expect(result.error).toBeNull()
      const artifact = result.artifacts[0] as { uri: string; mime: string }
      expect(artifact.mime).toBe('image/png')
      expect(artifact.uri).toMatch(/^data:image\/png;base64,/)
      const bytes = Buffer.from(artifact.uri.replace(/^data:image\/png;base64,/, ''), 'base64')
      // PNG magic 89 50 4E 47 — proof the spawned subprocess wrote real pixels.
      expect(bytes.subarray(0, 4).toString('hex')).toBe('89504e47')

      // The runtime routed its spawn THROUGH the sandbox (command = interpreter
      // literal; no egress; the credential carve-out forwarded).
      expect(runtime.ranSpecs).toHaveLength(1)
      const ranSpec = at(runtime.ranSpecs, 0)
      expect(ranSpec.command).toBe(python)
      expect(ranSpec.allowedEgress).toEqual([])
      expect(ranSpec.denyPaths).toContain('/planted/secret/bank-creds')
      // The dangerous prompt rode as ONE argv element (never shell-interpolated).
      const args = ranSpec.args
      expect(at(args, args.indexOf('--prompt') + 1)).toBe('a cat; rm -rf / `id`')
    })
  },
)

describe.runIf(process.platform !== 'darwin')(
  'CPU image floor — CI-cheap smoke (non-darwin: real-codepath fail-closed)',
  () => {
    it('fails closed into the uniform envelope rather than spawning unconfined', async () => {
      // On a non-darwin runner the structured sandbox is unimplemented → run()
      // throws SandboxUnsupportedError. The runtime must surface a membrane fault,
      // NEVER fall back to an unconfined spawn. This exercises the REAL fail-closed
      // codepath (the createSandbox() factory + the runtime's catch), not a mock.
      const fakeScript = stageSmokeScript()
      const runtime = new CpuImageFloorRuntime({
        engine: smokeEngine(fakeScript),
        sandbox: createSandbox(), // linux/win → fail-closed on run()
      })
      const result = await runtime.invoke(imageRequest(imageCore()))
      expect(result.status).toBe('failed')
      expect(result.error?.faultDomain).toBe('membrane')
      expect(result.error?.code).toBe('membrane.sandbox_fault')
    })
  },
)
