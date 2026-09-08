import type { InvokeRequest } from '@harborline-software/api-contracts'
import { describe, expect, it } from 'vitest'

import {
  IdempotencyKeyRequiredError,
  assertIdempotencyKey,
  invokeCapability,
  normalizeToEnvelope,
} from './invoke.js'
import { InProcessTransport } from './in-process-transport.js'
import { InMemoryLogSink } from './observe.js'
import { ReferenceImageRuntime } from '../runtime/reference-image-runtime.js'

function bankImportRequest(idempotencyKey: string): InvokeRequest {
  return {
    capabilityId: 'bank-import',
    core: {
      source: { kind: 'file', attachmentId: 'a1', fileName: 's.csv' },
      accountId: 'acct-1',
      timeout: 30_000,
    },
    providerInputs: {},
    attachments: [{ attachmentId: 'a1', mime: 'text/csv' }],
    idempotencyKey,
    correlationId: 'corr-bank-1',
    transport: 'sync',
  }
}

function imageRequest(): InvokeRequest {
  return {
    capabilityId: 'image',
    core: {
      prompt: 'a lighthouse',
      size: { w: 512, h: 512 },
      seed: 7,
      count: 1,
      format: 'png',
      timeout: 30_000,
    },
    providerInputs: {},
    attachments: [],
    idempotencyKey: 'idem-img-1',
    correlationId: 'corr-img-1',
    transport: 'sync',
  }
}

describe('SEC-2 — idempotencyKey fail-closed on financial Invoke', () => {
  it('REJECTS a financial (bank-import) Invoke with a blank idempotencyKey', () => {
    expect(() => assertIdempotencyKey(bankImportRequest(''))).toThrow(
      IdempotencyKeyRequiredError,
    )
    expect(() => assertIdempotencyKey(bankImportRequest('   '))).toThrow(
      IdempotencyKeyRequiredError,
    )
  })

  it('ACCEPTS a financial Invoke with a non-blank idempotencyKey', () => {
    expect(() => assertIdempotencyKey(bankImportRequest('idem-real'))).not.toThrow()
  })

  it('does NOT gate a non-financial (image) Invoke on a blank key', () => {
    const req = imageRequest()
    req.idempotencyKey = ''
    expect(() => assertIdempotencyKey(req)).not.toThrow()
  })

  it('a financial Invoke with a blank key never reaches the runtime (fail-closed at the membrane)', async () => {
    const runtime = new ReferenceImageRuntime()
    const transport = new InProcessTransport('rt', runtime)
    await expect(invokeCapability(transport, bankImportRequest(''))).rejects.toBeInstanceOf(
      IdempotencyKeyRequiredError,
    )
  })
})

describe('M3 normalization — uniform envelope, even on fault', () => {
  it('normalizes a loose native response into the six-field envelope', () => {
    const env = normalizeToEnvelope({ status: 'succeeded' }, 'fallback-job')
    expect(env.jobId).toBe('fallback-job')
    expect(env.status).toBe('succeeded')
    expect(env.progress).toBe(1)
    expect(env.artifacts).toEqual([])
    expect(env.usage).toEqual({ unit: 'call', quantity: 1, tier: 'local' })
    expect(env.error).toBeNull()
  })

  it('redacts secrets out of a native error message at the M3 boundary (SEC-3)', () => {
    const env = normalizeToEnvelope(
      { status: 'failed', error: { message: 'auth failed: Bearer leakytoken123456' } },
      'job',
    )
    expect(env.error?.message).not.toContain('leakytoken123456')
  })

  it('surfaces a uniform membrane-domain error envelope when the transport throws', async () => {
    const throwingTransport = {
      runtimeId: 'rt',
      mode: 'in-process' as const,
      announce: () => Promise.reject(new Error('x')),
      negotiateOffer: () => Promise.reject(new Error('x')),
      health: () => Promise.reject(new Error('x')),
      invoke: () => Promise.reject(new Error('boom with apiKey=SECRETLEAK1234')),
      cancel: () => Promise.resolve(),
    }
    const log = new InMemoryLogSink()
    const env = await invokeCapability(throwingTransport, imageRequest(), { logSink: log })
    expect(env.status).toBe('failed')
    expect(env.error?.faultDomain).toBe('membrane')
    expect(env.error?.code).toBe('membrane.transport_fault')
    // the secret in the native error is redacted in BOTH the envelope and the log.
    expect(env.error?.message).not.toContain('SECRETLEAK1234')
    expect(JSON.stringify(log.records)).not.toContain('SECRETLEAK1234')
  })
})

describe('M3 fail-closed flip (Admiral ruling 2026-06-16) — missing/invalid status → failed', () => {
  it('a status-LESS native response normalizes to `failed`, not `succeeded`', () => {
    // The load-bearing flip: an empty/truncated native payload must NOT pass as
    // success. A malformed MCP/CLI/worker reply with no status is a fault.
    const env = normalizeToEnvelope({ artifacts: [] }, 'job-no-status')
    expect(env.status).toBe('failed')
    expect(env.progress).toBe(0) // progress follows failed
  })

  it('an INVALID status value normalizes to `failed`', () => {
    const env = normalizeToEnvelope({ status: 'done' }, 'job-bad-status')
    expect(env.status).toBe('failed')
    expect(env.progress).toBe(0)
  })

  it('a fail-closed `failed` envelope is self-explaining (synthesized membrane error, never null)', () => {
    const env = normalizeToEnvelope({ artifacts: [] }, 'job')
    expect(env.error).not.toBeNull()
    expect(env.error?.faultDomain).toBe('membrane')
    expect(env.error?.code).toBe('membrane.invalid_native_status')
  })

  it('does NOT override an explicit native error when failing closed', () => {
    const env = normalizeToEnvelope(
      { error: { faultDomain: 'provider', code: 'provider.oom', message: 'out of memory' } },
      'job',
    )
    expect(env.status).toBe('failed') // status missing → failed
    // the native error is preserved (not replaced by the synthesized one)
    expect(env.error?.code).toBe('provider.oom')
  })

  it('an empty `{}` native response fails closed (the truncated-payload case)', () => {
    const env = normalizeToEnvelope({}, 'job-empty')
    expect(env.status).toBe('failed')
    expect(env.error?.code).toBe('membrane.invalid_native_status')
  })

  it('a valid `succeeded` status is still honored (the flip does not break the happy path)', () => {
    const env = normalizeToEnvelope({ status: 'succeeded' }, 'job-ok')
    expect(env.status).toBe('succeeded')
    expect(env.progress).toBe(1)
    expect(env.error).toBeNull()
  })
})
