import type { CancelRequest, InvokeRequest } from '@harborline-software/api-contracts'
import { localOsUserPrincipal } from '@harborline-software/api-contracts/principal'
import { describe, expect, it, vi } from 'vitest'

import { createPolicyEvaluator, loadPolicyStore } from './authority-registry.js'
import {
  consumeTrustedConfirmation,
  isRegisteredConfirmationEvidence,
} from './credential-boundary.js'
import { isFinancialCapability } from './financial.js'
import { mintMembranePrincipal } from './host-principal.js'
import { InProcessTransport } from './in-process-transport.js'
import { invokeCapability, normalizeToEnvelope } from './invoke.js'
import { LoopbackHttpTransport } from './loopback-transport.js'
import { isContractVersionCompatible } from './negotiate.js'
import { secureInvoke } from './pep.js'
import { REDACTION_MARKER, redact } from './redaction.js'
import { TrustedConfirmationBroker } from './credential-issuer.js'
import type { RuntimeHost } from '../runtime/runtime-host.js'

function imageRequest(): InvokeRequest {
  return {
    capabilityId: 'image',
    core: { prompt: 'test', size: { w: 1, h: 1 }, seed: 1, count: 1, format: 'png', timeout: 1 },
    providerInputs: {}, attachments: [], idempotencyKey: 'key', correlationId: 'corr', transport: 'sync',
  }
}

describe('silent-failure regression behaviours', () => {
  it('preserves the explicit CP policy summary for a known confirmation-required command', () => {
    const decision = createPolicyEvaluator(loadPolicyStore())('demo-cp-op')
    expect(decision).toEqual({
      authority: 'CP',
      summary: 'SYNTHETIC confirmation-required demo (no real effect) — proves the CP propose-then-confirm gate.',
    })
  })

  it('refuses malformed host principals before they can become membrane credentials', () => {
    expect(() => mintMembranePrincipal({ id: 'os:mal', displayName: 7, kind: 'local-os-user' } as never)).toThrow(TypeError)
    expect(() => mintMembranePrincipal({ id: 'os:mal', displayName: 'Mal', kind: 'robot' } as never)).toThrow(TypeError)
  })

  it('does not treat a confirmation-shaped object as broker-issued evidence', () => {
    const forged = { token: 'real-looking-token', command: 'demo-cp-op', confirmedBy: localOsUserPrincipal('mallory') }
    expect(isRegisteredConfirmationEvidence(forged)).toBe(false)
  })

  it('never calls an unregistered confirmation consumer or accepts its answer', () => {
    const fake = { consume: vi.fn(() => true) }
    expect(consumeTrustedConfirmation(fake, 'token', 'demo-cp-op')).toBe(false)
    expect(fake.consume).not.toHaveBeenCalled()
    expect(consumeTrustedConfirmation(undefined, 'token', 'demo-cp-op')).toBe(false)
  })

  it('refuses a copied confirmation even when its broker token and confirmer are genuine', async () => {
    const principal = mintMembranePrincipal(localOsUserPrincipal('alice'))
    const broker = new TrustedConfirmationBroker()
    const token = broker.propose('demo-cp-op')
    const issued = broker.confirm(token, 'demo-cp-op', principal)
    const copied = { ...issued }
    const execute = vi.fn(async () => ({ jobId: 'ok', status: 'succeeded' as const, progress: 1, artifacts: [], usage: { unit: 'call', quantity: 1, tier: 'local' as const }, error: null }))
    const result = await secureInvoke(
      { command: 'demo-cp-op', capabilityId: 'image', request: imageRequest(), confirmation: copied },
      principal,
      execute,
      {},
      broker,
    )
    expect(execute).not.toHaveBeenCalled()
    expect(result.error).toMatchObject({ code: 'membrane.authority_rejected', retryable: false })
  })

  it('classifies only bank-import as the financial v0 operation', () => {
    expect(isFinancialCapability('bank-import')).toBe(true)
    expect(isFinancialCapability('bank-import-preview')).toBe(false)
  })

  it('uses the minted job id, native progress, and conservative defaults for malformed native fields', () => {
    const env = normalizeToEnvelope({ status: 'running', jobId: '', progress: 0.37, usage: { unit: 'call' } }, 'minted-job')
    expect(env.jobId).toBe('minted-job')
    expect(env.progress).toBe(0.37)
    expect(env.usage).toEqual({ unit: 'call', quantity: 1, tier: 'local' })
    expect(normalizeToEnvelope({ status: 'running', progress: Number.NaN }, 'job').progress).toBe(0)
  })

  it('preserves valid native error semantics and redacts registered secrets', () => {
    const env = normalizeToEnvelope({
      status: 'failed',
      error: { faultDomain: 'input', retryable: true, code: 'input.bad', message: 'token=TOPSECRET42', retryAfter: 125 },
    }, 'job', ['TOPSECRET42'])
    expect(env.error).toMatchObject({ faultDomain: 'input', retryable: true, code: 'input.bad', retryAfter: 125 })
    expect(env.error?.message).not.toContain('TOPSECRET42')
    expect(normalizeToEnvelope({ status: 'failed', error: { faultDomain: 'provider', code: 'p', message: 'x' } }, 'job').error?.retryable).toBe(true)
  })

  it('redacts caller-provided secrets in a transport fault envelope', async () => {
    const transport = {
      runtimeId: 'rt', mode: 'in-process' as const,
      announce: async () => { throw new Error('unused') }, negotiateOffer: async () => { throw new Error('unused') },
      health: async () => ({ kind: 'liveness' as const, state: 'up' as const }),
      invoke: async () => { throw new Error('wire secret EXPLICIT_SECRET_123') }, cancel: async () => undefined,
    }
    const env = await invokeCapability(transport, imageRequest(), { secrets: ['EXPLICIT_SECRET_123'] })
    expect(env.error).toMatchObject({ faultDomain: 'membrane', retryable: false, code: 'membrane.transport_fault' })
    expect(env.error?.message).not.toContain('EXPLICIT_SECRET_123')
  })

  it.each([
    ['Bearer abcDEF123456789xyz', 'abcDEF123456789xyz'],
    ['api_key=ABCD1234EFGH', 'ABCD1234EFGH'],
    ['access_token = "ACCESS1234"', 'ACCESS1234'],
    ["client_secret:'CLIENT1234'", 'CLIENT1234'],
    ['sk-ABCDEFGHIJKLMNOPQRST', 'sk-ABCDEFGHIJKLMNOPQRST'],
    ['Password=hunter2secret', 'hunter2secret'],
  ])('redacts each supported credential spelling: %s', (text, secret) => {
    const out = redact(text)
    expect(out).toContain(REDACTION_MARKER)
    expect(out).not.toContain(secret)
  })

  it('redacts every occurrence of a four-character literal containing regex metacharacters', () => {
    const out = redact('first $e.c then $e.c', { secrets: ['$e.c'] })
    expect(out).toBe(`first ${REDACTION_MARKER} then ${REDACTION_MARKER}`)
  })

  it('rejects prefixed and suffixed semver strings instead of negotiating them', () => {
    expect(isContractVersionCompatible('1.0.0', 'v1.0.0')).toBe(false)
    expect(isContractVersionCompatible('1.0.0', '1.0.0-preview')).toBe(false)
  })

  it('forwards cancellation through both transport address modes', async () => {
    const cancel = vi.fn()
    const host = { cancel, announce: vi.fn(), negotiateOffer: vi.fn(), health: vi.fn(), invoke: vi.fn() } as unknown as RuntimeHost
    const request = { jobId: 'job-1', correlationId: 'cancel-1' } as CancelRequest
    await new InProcessTransport('rt', host).cancel(request)
    expect(cancel).toHaveBeenCalledWith(request)

    const fetchImpl = vi.fn(async () => new Response('{}', { status: 200 })) as unknown as typeof fetch
    await new LoopbackHttpTransport('rt', 'http://127.0.0.1:1', fetchImpl).cancel(request)
    expect(fetchImpl).toHaveBeenCalledOnce()
  })

  it('refuses a non-success HTTP response instead of treating its body as a result', async () => {
    const fetchImpl = vi.fn(async () => new Response('{"status":"succeeded"}', { status: 500 })) as unknown as typeof fetch
    const transport = new LoopbackHttpTransport('rt', 'http://127.0.0.1:1', fetchImpl)
    await expect(transport.invoke(imageRequest())).rejects.toThrow('HTTP 500')
  })
})
