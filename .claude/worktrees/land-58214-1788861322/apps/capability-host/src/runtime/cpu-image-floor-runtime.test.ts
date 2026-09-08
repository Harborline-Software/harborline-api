/**
 * Gate test for the REAL CPU image-floor runtime (inc-4).
 *
 * A real CPU SD-1.5 render is MINUTES — far too slow for CI. So this suite MOCKS
 * the heavy subprocess with a stub sandbox (writes a tiny fake PNG, returns ok)
 * and asserts the load-bearing wiring instead of pixel fidelity:
 *   - the argv MAPPING (ImageCore → final argv elements, NEVER shell-interpolated)
 *   - the produced file → base64 `data:` URI artifact decode (8 MB cap honored)
 *   - the S7 sandbox spec (command = the engine interpreter literal; no egress;
 *     write-only the per-Invoke work dir; the credential carve-out forwarded)
 *   - GRACEFUL DEGRADATION to the deterministic reference stub when no engine
 *
 * The REAL spawn-confined-by-sandbox path is exercised by a host-gated round-trip
 * (`cpu-image-round-trip.test.ts`) that runs only when armed + the model present.
 */

import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { describe, expect, it } from 'vitest'

import type { ImageCore, InvokeRequest } from '@harborline-software/api-contracts'

import type { Sandbox, SandboxResult, SandboxSpec } from '../sandbox/index.js'
import {
  CpuImageFloorRuntime,
  CPU_IMAGE_DEFAULT_STEPS,
  CPU_IMAGE_RUNTIME_ID,
  type ImageEngine,
  realImageOptedIn,
} from './cpu-image-floor-runtime.js'

// A 1x1 PNG (real PNG header) the stub sandbox "produces" so the artifact decode
// has genuine bytes to base64.
const TINY_PNG = Buffer.from(
  '89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000d4944415478da6364f80f00010101001b0b0210000000004945',
  'hex',
)

function imageCore(overrides: Partial<ImageCore> = {}): ImageCore {
  return {
    prompt: 'a carrier at dawn',
    size: { w: 256, h: 256 },
    seed: 42,
    count: 1,
    format: 'png',
    timeout: 600_000,
    ...overrides,
  }
}

function imageRequest(core: ImageCore, correlationId = 'corr-img-1'): InvokeRequest {
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

// A real on-disk script the runtime can stage into the work dir (its CONTENTS are
// never run — the stub sandbox doesn't execute it — it just needs to exist to copy).
const FAKE_SCRIPT_DIR = mkdtempSync(join(tmpdir(), 'capability-img-test-'))
const FAKE_SCRIPT = join(FAKE_SCRIPT_DIR, 'sd_local.py')
writeFileSync(FAKE_SCRIPT, '# fake generator script (not executed in these tests)\n')

/** A fake CPU image engine pointing at a fake interpreter — no real python needed. */
const FAKE_ENGINE: ImageEngine = {
  id: 'fake-cpu-image',
  name: 'Fake CPU Image (test)',
  interpreter: '/usr/bin/fake-python',
  script: FAKE_SCRIPT,
  readOnlyPaths: ['/opt/fake/model', '/opt/fake/venv'],
  buildArgs(core, outFile, steps, scriptPath) {
    return [
      '-s',
      '-E',
      scriptPath,
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
    return { HOME: '/home/fake', TMPDIR: join(workDir, 'scratch'), HF_HUB_OFFLINE: '1' }
  },
}

/**
 * Element access under `noUncheckedIndexedAccess`.
 *
 * Fails the test at the access with a legible message instead of letting `undefined` flow into an
 * assertion — `expect(undefined).toBe(undefined)` is the shape that lets a missing argv element
 * read as a pass.
 */
function at<T>(items: readonly T[], index: number): T {
  const item = items[index]
  if (item === undefined) {
    throw new Error(`expected an element at index ${index} (length ${items.length})`)
  }
  return item
}

/** A stub sandbox that records the spec and writes a fake PNG to the work dir. */
function stubSandbox(opts: { recorded: SandboxSpec[]; bytes?: Buffer; fail?: boolean }): Sandbox {
  return {
    platform: process.platform,
    implemented: true,
    async run(spec: SandboxSpec): Promise<SandboxResult> {
      opts.recorded.push(spec)
      if (opts.fail === true) {
        return { exitCode: 1, signal: null, stdout: '', stderr: 'boom', ok: false }
      }
      const { writeFileSync } = await import('node:fs')
      // The engine's last `--out <file>` arg is where the image lands.
      const outIdx = spec.args.indexOf('--out')
      const outFile = spec.args[outIdx + 1] as string
      writeFileSync(outFile, opts.bytes ?? TINY_PNG)
      return { exitCode: 0, signal: null, stdout: '', stderr: '', ok: true }
    },
  }
}

describe('CpuImageFloorRuntime — argv mapping + artifact decode (mocked subprocess)', () => {
  it('maps ImageCore → final argv elements (no shell interpolation) + a base64 data-URI artifact', async () => {
    const recorded: SandboxSpec[] = []
    const runtime = new CpuImageFloorRuntime({
      engine: FAKE_ENGINE,
      sandbox: stubSandbox({ recorded }),
    })

    // A prompt with shell metacharacters MUST ride as a single argv element.
    const core = imageCore({ prompt: 'a cat; rm -rf / && echo $(whoami)`id`', seed: 7 })
    const result = await runtime.invoke(imageRequest(core))

    expect(result.status).toBe('succeeded')
    expect(result.error).toBeNull()
    expect(result.usage.unit).toBe('image')

    // The spec ran once; command is the engine interpreter LITERAL (F1 narrowing).
    expect(recorded).toHaveLength(1)
    const spec = at(recorded, 0)
    expect(spec.command).toBe('/usr/bin/fake-python')

    // The dangerous prompt is ONE argv element, verbatim — never split/interpolated.
    const promptIdx = spec.args.indexOf('--prompt')
    expect(at(spec.args, promptIdx + 1)).toBe('a cat; rm -rf / && echo $(whoami)`id`')
    // the script is the WORKDIR-LOCAL staged copy (not the fleet-tree original),
    // so its ancestors are the traversable temp root under the deny-default sandbox.
    expect(at(spec.args, 2)).toContain(spec.workDir)
    expect(at(spec.args, 2).endsWith('sd_local.py')).toBe(true)
    // seed + size ride as their own argv elements.
    expect(at(spec.args, spec.args.indexOf('--seed') + 1)).toBe('7')
    expect(at(spec.args, spec.args.indexOf('--width') + 1)).toBe('256')
    expect(at(spec.args, spec.args.indexOf('--steps') + 1)).toBe(String(CPU_IMAGE_DEFAULT_STEPS))

    // The produced PNG decodes into a base64 `data:` uri artifact (8 MB cap path).
    const artifact = result.artifacts[0] as { kind: string; uri: string; w: number; mime: string }
    expect(artifact.kind).toBe('image')
    expect(artifact.mime).toBe('image/png')
    expect(artifact.w).toBe(256)
    expect(artifact.uri).toMatch(/^data:image\/png;base64,/)
    // The base64 round-trips back to the original bytes.
    const b64 = artifact.uri.replace(/^data:image\/png;base64,/, '')
    expect(Buffer.from(b64, 'base64').equals(TINY_PNG)).toBe(true)

    // jobId carries the correlationId; minted off the CPU runtime id.
    expect(result.jobId).toContain(CPU_IMAGE_RUNTIME_ID)
    expect(result.jobId).toContain('corr-img-1')
  })

  it('confines the engine: no egress, write-only the per-Invoke work dir, carve-out forwarded', async () => {
    const recorded: SandboxSpec[] = []
    const runtime = new CpuImageFloorRuntime({
      engine: FAKE_ENGINE,
      sandbox: stubSandbox({ recorded }),
      denyPaths: ['/planted/secret/bank-creds'],
    })
    await runtime.invoke(imageRequest(imageCore()))
    const spec = at(recorded, 0)
    // (c) a LOCAL single-file checkpoint floor declares ZERO egress.
    expect(spec.allowedEgress).toEqual([])
    // (b) writes ONLY its per-Invoke work dir.
    expect(spec.workDir).toContain('capability-image-')
    // the planted-secret deny path is forwarded to the sandbox (carve-out).
    expect(spec.denyPaths).toContain('/planted/secret/bank-creds')
    // the engine's read-only paths are forwarded (model/venv), no credential path.
    expect(spec.readOnlyPaths).toContain('/opt/fake/model')
  })

  it('honors the CORE timeout (CPU render needs minutes, not the 30s TTS default)', async () => {
    const recorded: SandboxSpec[] = []
    const runtime = new CpuImageFloorRuntime({ engine: FAKE_ENGINE, sandbox: stubSandbox({ recorded }) })
    await runtime.invoke(imageRequest(imageCore({ timeout: 600_000 })))
    expect(at(recorded, 0).timeoutMs).toBe(600_000)
  })

  it('an over-cap image falls back to a file:// uri (the 8 MB data-URI cap holds)', async () => {
    const recorded: SandboxSpec[] = []
    const huge = Buffer.alloc(9 * 1024 * 1024, 1) // > 8 MB
    const runtime = new CpuImageFloorRuntime({
      engine: FAKE_ENGINE,
      sandbox: stubSandbox({ recorded, bytes: huge }),
    })
    const result = await runtime.invoke(imageRequest(imageCore()))
    expect(result.status).toBe('succeeded')
    const artifact = result.artifacts[0] as { uri: string }
    // Over cap → NOT inlined; a file:// uri (the bridge/asset-shim loads it).
    expect(artifact.uri).toMatch(/^file:\/\//)
    expect(artifact.uri).not.toMatch(/^data:/)
  })

  it('an engine failure surfaces the uniform provider error envelope (no fork, no crash)', async () => {
    const recorded: SandboxSpec[] = []
    const runtime = new CpuImageFloorRuntime({
      engine: FAKE_ENGINE,
      sandbox: stubSandbox({ recorded, fail: true }),
    })
    const result = await runtime.invoke(imageRequest(imageCore()))
    expect(Object.keys(result).sort()).toEqual(
      ['artifacts', 'error', 'jobId', 'progress', 'status', 'usage'].sort(),
    )
    expect(result.status).toBe('failed')
    expect(result.error?.faultDomain).toBe('provider')
    expect(result.error?.retryable).toBe(true)
    expect(result.error?.code).toBe('provider.image_engine_failed')
  })

  it('a sandbox-unsupported platform fails closed into the uniform envelope (no unconfined spawn)', async () => {
    const failingSandbox: Sandbox = {
      platform: 'linux',
      implemented: false,
      run: () =>
        Promise.reject(
          Object.assign(new Error('SEC-7: not implemented'), {
            code: 'sandbox.platform_unsupported',
          }),
        ),
    }
    const runtime = new CpuImageFloorRuntime({ engine: FAKE_ENGINE, sandbox: failingSandbox })
    const result = await runtime.invoke(imageRequest(imageCore()))
    expect(result.status).toBe('failed')
    expect(result.error?.faultDomain).toBe('membrane')
    expect(result.error?.code).toBe('membrane.sandbox_fault')
  })
})

describe('CpuImageFloorRuntime — graceful degradation to the reference stub', () => {
  it('with no engine, invoke delegates to the deterministic reference stub (no subprocess)', async () => {
    const recorded: SandboxSpec[] = []
    // engine:null disables the real path; the sandbox MUST NOT be called.
    const runtime = new CpuImageFloorRuntime({ engine: null, sandbox: stubSandbox({ recorded }) })
    expect(runtime.usesRealEngine).toBe(false)
    expect(runtime.providerId).toBe('reference-stub-image')

    const result = await runtime.invoke(imageRequest(imageCore()))
    expect(result.status).toBe('succeeded')
    expect(result.usage.unit).toBe('image')
    // the deterministic stub returns a `stub://` descriptor, NOT real bytes.
    expect((result.artifacts[0] as { uri: string }).uri).toMatch(/^stub:\/\//)
    // the sandbox was NEVER invoked (no real subprocess on the degraded path).
    expect(recorded).toHaveLength(0)
  })

  it('announces the stub provider when degraded, the real engine provider when armed', async () => {
    const degraded = new CpuImageFloorRuntime({ engine: null })
    expect(at(at(degraded.announce().capabilities, 0).providers, 0).id).toBe('reference-stub-image')

    const armed = new CpuImageFloorRuntime({ engine: FAKE_ENGINE })
    expect(at(at(armed.announce().capabilities, 0).providers, 0).id).toBe('fake-cpu-image')
    expect(armed.usesRealEngine).toBe(true)
  })

  it('health is up + tri-state degraded works on both paths', () => {
    const armed = new CpuImageFloorRuntime({ engine: FAKE_ENGINE })
    expect(armed.health('readiness').state).toBe('up')
    armed.setDegraded(true)
    expect(armed.health('liveness').state).toBe('degraded')

    const stub = new CpuImageFloorRuntime({ engine: null })
    expect(stub.health('readiness').state).toBe('up')
    expect(stub.health('readiness').detail).toContain('reference stub')
  })

  it('the real-render opt-in reads CAPABILITY_HOST_IMAGE_REAL (off by default → fast CI/bridge)', () => {
    expect(realImageOptedIn({})).toBe(false)
    expect(realImageOptedIn({ CAPABILITY_HOST_IMAGE_REAL: '1' })).toBe(true)
    expect(realImageOptedIn({ CAPABILITY_HOST_IMAGE_REAL: 'true' })).toBe(true)
    expect(realImageOptedIn({ CAPABILITY_HOST_IMAGE_REAL: '0' })).toBe(false)
  })

  it('a non-image capability is an input fault (uniform envelope)', async () => {
    const runtime = new CpuImageFloorRuntime({ engine: FAKE_ENGINE, sandbox: stubSandbox({ recorded: [] }) })
    const req: InvokeRequest = { ...imageRequest(imageCore()), capabilityId: 'tts' }
    const result = await runtime.invoke(req)
    expect(result.status).toBe('failed')
    expect(result.error?.faultDomain).toBe('input')
    expect(result.error?.code).toBe('input.capability_not_hosted')
  })
})
