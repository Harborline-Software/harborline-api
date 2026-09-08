/**
 * Phase-4 ACCEPTANCE (inc-4) — the reference edition's `image` capability round-trips through
 * the membrane to a REAL CPU SD-1.5 subprocess running INSIDE the OS-native
 * sandbox, producing REAL pixels, with the uniform envelope + correlation-id.
 *
 * This is the image analogue of the TTS round-trip: NOT the in-memory reference
 * stub, but a genuinely spawned diffusers subprocess (the image CPU floor) confined
 * by the seatbelt sandbox, with the uniform @harborline-software/api-contracts envelope holding
 * end-to-end and a real PNG coming back as a base64 `data:` uri.
 *
 * Host- AND opt-in-gated: a real CPU render is MINUTES, so this runs ONLY when
 * armed (`CAPABILITY_HOST_IMAGE_REAL=1`) on darwin WITH the SD model + venv present. The
 * default CI run skips it (the mocked-subprocess gate test covers the wiring fast).
 * Run locally with: `CAPABILITY_HOST_IMAGE_REAL=1 npm test -- cpu-image-round-trip`.
 */

import type { CapabilityResult, ImageArtifact, InvokeRequest } from '@harborline-software/api-contracts'
import { describe, expect, it } from 'vitest'

import { REFERENCE_APP_COMPOSITION } from '../editions/harborline.js'
import { InProcessTransport } from '../membrane/in-process-transport.js'
import {
  buildCpuSdEngine,
  CpuImageFloorRuntime,
  CPU_IMAGE_RUNTIME_ID,
} from '../runtime/cpu-image-floor-runtime.js'
import { defaultShellNegotiationProfile, CapabilityShell } from './capability-shell.js'

const armed = process.platform === 'darwin' && process.env.CAPABILITY_HOST_IMAGE_REAL === '1'
// Only meaningful if the real engine is actually buildable (model + venv present).
const engineReady = armed && buildCpuSdEngine() != null

function imageRequest(correlationId: string): InvokeRequest {
  return {
    capabilityId: 'image',
    core: {
      prompt: 'a red lighthouse on a green cliff, photo',
      size: { w: 256, h: 256 },
      seed: 1,
      count: 1,
      format: 'png',
      timeout: 600_000,
    },
    providerInputs: {},
    attachments: [],
    idempotencyKey: `idem-${correlationId}`,
    correlationId,
    transport: 'sync',
  }
}

describe.runIf(engineReady)('Phase-4 ACCEPTANCE — real CPU image subprocess inside the sandbox', () => {
  it(
    'renders REAL pixels through the membrane, confined by the S7 sandbox',
    { timeout: 600_000 },
    async () => {
      const runtime = new CpuImageFloorRuntime({ forceReal: true })
      expect(runtime.usesRealEngine).toBe(true)

      const shell = new CapabilityShell({
        composition: REFERENCE_APP_COMPOSITION,
        negotiationProfile: defaultShellNegotiationProfile(),
      })
      const connection = await shell.connect(new InProcessTransport(CPU_IMAGE_RUNTIME_ID, runtime))
      expect(connection.negotiation.acceptedCapabilities).toContain('image')

      const resolution = shell.resolve('image')
      expect(resolution.resolutionState).toBe('available')
      expect(resolution.chosenProviderId).toBe('cpu-sd15-floor')

      const result: CapabilityResult = await shell.invoke(imageRequest('corr-cpu-img-1'))
      expect(result.status).toBe('succeeded')
      expect(result.error).toBeNull()
      expect(result.usage.unit).toBe('image')

      const artifact = result.artifacts[0] as ImageArtifact
      expect(artifact.kind).toBe('image')
      expect(artifact.w).toBe(256)
      expect(artifact.mime).toBe('image/png')
      // REAL pixels inlined as a base64 PNG data uri — the proof the confined
      // subprocess ran to completion (not a stub `stub://` descriptor).
      expect(artifact.uri).toMatch(/^data:image\/png;base64,/)
      const bytes = Buffer.from(artifact.uri.replace(/^data:image\/png;base64,/, ''), 'base64')
      // PNG magic: 89 50 4E 47.
      expect(bytes.subarray(0, 4).toString('hex')).toBe('89504e47')
      expect(bytes.length).toBeGreaterThan(1000)

      // the runtime routed its spawn THROUGH the sandbox (never unconfined), and
      // the command is the resolved interpreter LITERAL (F1 narrowing).
      expect(runtime.ranSpecs).toHaveLength(1)
      const ranSpec = runtime.ranSpecs[0]
      if (ranSpec === undefined) throw new Error('expected the runtime to have recorded one spec')
      expect(ranSpec.command).toMatch(/python|Python/)
      expect(ranSpec.allowedEgress).toEqual([])
    },
  )
})
