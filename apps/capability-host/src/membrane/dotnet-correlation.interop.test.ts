import { randomBytes } from 'node:crypto'

import type { InvokeRequest } from '@harborline-software/api-contracts'
import { expect, it } from 'vitest'

import {
  CORRELATION_HEADER,
  LoopbackHttpTransport,
} from './loopback-transport.js'

const dotnetRuntimeUrl = process.env.HARBORLINE_DOTNET_RUNTIME_URL
const interopTest = dotnetRuntimeUrl === undefined ? it.skip : it
const INTEROP_TEST_TIMEOUT_MS = 60_000

interopTest(
  'sends its correlation id through the production .NET invoke ingress',
  async () => {
    const correlationId = randomBytes(16).toString('hex')
    let traceIdAtMembrane: string | null = null
    const observingFetch: typeof fetch = async (input, init) => {
      traceIdAtMembrane = new Headers(init?.headers).get(CORRELATION_HEADER)
      return fetch(input, init)
    }
    const transport = new LoopbackHttpTransport(
      'dotnet-local-node',
      dotnetRuntimeUrl!,
      observingFetch,
    )
    const request: InvokeRequest = {
      capabilityId: 'image',
      core: {
        prompt: 'trace bridge interop',
        size: { w: 64, h: 64 },
        seed: 20,
        count: 1,
        format: 'png',
        timeout: 5_000,
      },
      providerInputs: {},
      attachments: [],
      idempotencyKey: `idem-${correlationId}`,
      correlationId,
      transport: 'sync',
    }

    await expect(transport.invoke(request)).resolves.toEqual({})
    expect(traceIdAtMembrane).toBe(correlationId)
    process.stdout.write(`CAPABILITY_HOST_MEMBRANE_TRACE_ID=${traceIdAtMembrane}\n`)
  },
  INTEROP_TEST_TIMEOUT_MS,
)
