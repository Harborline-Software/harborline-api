/**
 * The Secure-face PEP tests (ADR 0134 P0 + P1a + P1b-1). Prove the chokepoint:
 *   (a) authenticate REJECTS an anonymous principal (fail-closed);
 *   (b) authorize CONSULTS the PDP + RECORDS the decision (`always-allow` / `reject`)
 *       on every invoke;
 *   (c) the REJECTION path (P1a): an AP command allows; a CP command allows ONLY with
 *       a valid confirmation, and is REJECTED (before execute) without one;
 *   (d) the TOKEN-BINDING (P1b-1): a CP confirmation is only valid if its single-use
 *       broker token verifies + CONSUMES for THIS command via the injected
 *       `TokenConsumer` — a fabricated (no-token) confirmation is rejected, a replayed
 *       token is rejected on the second invoke, a wrong-command token is rejected, and
 *       a CP command with NO consumer wired fails-closed;
 *   (e) execute is reached with the principal carried, and the M3 promise holds
 *       (an executor fault is the uniform failed envelope, never a throw).
 */

import { localOsUserPrincipal, type CapabilityResult, type InvokeRequest, type Principal } from '@harborline-software/api-contracts'
import { describe, expect, it, vi } from 'vitest'

import {
  secureInvoke as secureInvokeActual,
  InMemoryDecisionSink,
  type MembranePrincipal,
  type ConfirmationEvidence,
  type PrincipalVerifier,
} from './pep.js'
import { TrustedConfirmationBroker } from './credential-issuer.js'
import { mintMembranePrincipal } from './host-principal.js'
import { NodeSigningKey, signPrincipal } from './host-principal-signer.js'
import { SeenNonceSet, verifySignedPrincipal } from './signed-principal.js'

const ALICE = mintMembranePrincipal(localOsUserPrincipal('alice'))
const ANON = mintMembranePrincipal({ id: '   ', displayName: 'anonymous', kind: 'local-os-user' } satisfies Principal) // blank id — anonymous

function ttsRequest(correlationId = 'corr-tts-1'): InvokeRequest {
  return {
    capabilityId: 'tts',
    core: { text: 'hello', voice: 'default', format: 'aiff', timeout: 30_000 } as never,
    providerInputs: {},
    attachments: [],
    idempotencyKey: 'idem-tts-1',
    correlationId,
    transport: 'sync',
  }
}

/** A succeeded uniform envelope (the executor's happy path). */
function okEnvelope(): CapabilityResult {
  return {
    jobId: 'job:test:ok',
    status: 'succeeded',
    progress: 1,
    artifacts: [],
    usage: { unit: 'call', quantity: 1, tier: 'local' },
    error: null,
  }
}

/**
 * A PDP classifying the COMMAND (ADR 0128 — the registry tags operations, not the
 * capability target): `invoke`/`resolve`/`health` are AP, `demo-cp-op` is CP, unknown
 * fail-closed to CP. The PEP passes the COMMAND (`invoke`), not the capability id.
 */
const testPdp = () => undefined

/**
 * A minimal in-test stand-in for the broker's single-use, command-bound token store —
 * the shape the Harborline App face injects as a `TokenConsumer` (backed by `ProposalBroker`).
 * `mint(token, command)` registers an outstanding token; the returned `consume` is the
 * `TokenConsumer` the PEP calls: it returns `true` ONLY for a known, unconsumed token
 * bound to the SAME command, and deletes it so a second consume returns `false`.
 */
function tokenStore() {
  const broker = new TrustedConfirmationBroker()
  const aliases = new Map<string, string>()
  const evidences = new Map<string, ConfirmationEvidence>()
  const consume = (token: string, command: string): boolean => {
    const actual = aliases.get(token) ?? token
    return broker.consume(actual, command)
  }
  brokerForConsumer.set(consume, { broker, aliases, evidences })
  return {
    broker,
    aliases,
    evidences,
    mint(token: string, command: string): void {
      const actual = broker.propose(command)
      evidences.set(token, broker.confirm(actual, command, ALICE))
      aliases.set(token, actual)
    },
    consume,
  }
}

const brokerForConsumer = new WeakMap<
  (token: string, command: string) => boolean,
  {
    broker: TrustedConfirmationBroker
    aliases: Map<string, string>
    evidences: Map<string, ConfirmationEvidence>
  }
>()

function secureInvoke(
  target: Parameters<typeof secureInvokeActual>[0],
  principal: MembranePrincipal,
  _legacyPolicy: unknown,
  execute: Parameters<typeof secureInvokeActual>[2],
  opts: Parameters<typeof secureInvokeActual>[3] & { consumeToken?: (token: string, command: string) => boolean } = {},
) {
  const consumer = opts.consumeToken
  const store = consumer == null ? undefined : brokerForConsumer.get(consumer)
  const confirmation = target.confirmation
  const actualToken = confirmation == null ? undefined : store?.aliases.get(confirmation.token)
  const actualConfirmation = confirmation != null
    && actualToken != null
    && typeof confirmation.confirmedBy?.id === 'string'
    && confirmation.confirmedBy.id.trim().length > 0
    ? store!.evidences.get(confirmation.token)
    : confirmation
  const safeTarget = actualConfirmation == null ? target : { ...target, confirmation: actualConfirmation }
  return secureInvokeActual(
    safeTarget,
    principal,
    execute,
    { decisionSink: opts.decisionSink, verifyPrincipal: opts.verifyPrincipal },
    store?.broker,
  )
}

describe('Secure-face PEP — authenticate (ADR 0134 P0)', () => {
  it('REJECTS an anonymous principal fail-closed (never reaches execute)', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const result = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest() },
      ANON,
      testPdp,
      execute,
    )
    expect(execute).not.toHaveBeenCalled() // authenticate short-circuited
    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('membrane.anonymous_principal')
    expect(result.error?.faultDomain).toBe('membrane')
  })

  it('ACCEPTS a non-anonymous principal and reaches execute', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const result = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest() },
      ALICE,
      testPdp,
      execute,
    )
    expect(execute).toHaveBeenCalledTimes(1)
    expect(result.status).toBe('succeeded')
  })
})

describe('Secure-face PEP — authorize: PDP consulted + decision RECORDED on every invoke', () => {
  it('records the AP classification + the principal for a tts (AP) invoke', async () => {
    const sink = new InMemoryDecisionSink()
    await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest('corr-rec-ap') },
      ALICE,
      testPdp,
      async () => okEnvelope(),
      { decisionSink: sink },
    )
    expect(sink.decisions).toHaveLength(1)
    const d = sink.decisions[0]!
    expect(d.command).toBe('invoke') // the op classified
    expect(d.capabilityId).toBe('tts') // the capability targeted
    expect(d.principal.id).toBe('os:alice') // WHO was recorded
    expect(d.decision.authority).toBe('AP')
    expect(d.outcome).toBe('always-allow')
    expect(d.correlationId).toBe('corr-rec-ap')
  })

  it('records the AP outcome `always-allow` for an AP invoke', async () => {
    const sink = new InMemoryDecisionSink()
    await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest() },
      ALICE,
      testPdp,
      async () => okEnvelope(),
      { decisionSink: sink },
    )
    expect(sink.decisions[0]!.decision.authority).toBe('AP')
    expect(sink.decisions[0]!.outcome).toBe('always-allow')
  })
})

describe('Secure-face PEP — caller seams fail closed', () => {
  it('does not consult or accept a caller-supplied allow-everything policy', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const allowAll = vi.fn(() => ({ authority: 'AP', summary: 'caller allow-all' }))
    const result = await secureInvokeActual(
      { command: 'demo-cp-op', capabilityId: 'tts', request: ttsRequest('corr-policy-forge') },
      ALICE,
      execute,
      { policyDecisionPoint: allowAll, consumeToken: () => true } as never,
    )

    expect(result.error?.code).toBe('membrane.authority_rejected')
    expect(execute).not.toHaveBeenCalled()
    expect(allowAll).not.toHaveBeenCalled()
  })

  it('does not let a fabricated token consumer open a CP command', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const result = await secureInvokeActual(
      {
        command: 'demo-cp-op',
        capabilityId: 'tts',
        request: ttsRequest('corr-token-forge'),
        confirmation: { token: 'fabricated', command: 'demo-cp-op', confirmedBy: ALICE },
      },
      ALICE,
      execute,
      { consumeToken: () => true } as never,
    )

    expect(result.error?.code).toBe('membrane.authority_rejected')
    expect(execute).not.toHaveBeenCalled()
  })

  it('accepts only a confirmation minted and consumed by the trusted broker', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const broker = new TrustedConfirmationBroker()
    const token = broker.propose('demo-cp-op')
    const confirmation = broker.confirm(token, 'demo-cp-op', ALICE)
    const result = await secureInvokeActual(
      { command: 'demo-cp-op', capabilityId: 'tts', request: ttsRequest('corr-broker'), confirmation },
      ALICE,
      execute,
      {},
      broker,
    )

    expect(result.status).toBe('succeeded')
    expect(execute).toHaveBeenCalledOnce()
  })
})

/**
 * The REJECTION path + TOKEN-BINDING (ADR 0134 P1a + P1b-1 — the in-process, TS
 * authorize half of P1). A CP command executes ONLY with a valid confirmation whose
 * single-use broker TOKEN verifies + consumes for THIS command; without one it is
 * REFUSED before SEC-2/execute. `demo-cp-op` (Harborline App's only CP command) is the proof
 * surface; AP commands are unaffected. (P1b-2 — the Ed25519 cross-process verifiable
 * confirmation — is NOT here.)
 */
describe('Secure-face PEP — authorize: the REJECTION path + token-binding (CP, P1a+P1b-1)', () => {
  const BOB = mintMembranePrincipal(localOsUserPrincipal('bob'))

  it('REJECTS a CP `demo-cp-op` invoke that carries NO confirmation (before execute)', async () => {
    const sink = new InMemoryDecisionSink()
    const execute = vi.fn(async () => okEnvelope())
    const store = tokenStore()
    const result = await secureInvoke(
      { command: 'demo-cp-op', capabilityId: 'tts', request: ttsRequest('corr-cp-noconf') },
      ALICE,
      testPdp,
      execute,
      { decisionSink: sink, consumeToken: store.consume },
    )
    expect(execute).not.toHaveBeenCalled() // refused BEFORE execute
    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('membrane.authority_rejected')
    expect(result.error?.faultDomain).toBe('membrane')
    // The reject outcome + the CP class are RECORDED (the durable-layer feed).
    expect(sink.decisions).toHaveLength(1)
    expect(sink.decisions[0]!.decision.authority).toBe('CP')
    expect(sink.decisions[0]!.outcome).toBe('reject')
  })

  it('ALLOWS a CP `demo-cp-op` invoke carrying a confirmation with a VALID broker token', async () => {
    const sink = new InMemoryDecisionSink()
    const execute = vi.fn(async () => okEnvelope())
    const store = tokenStore()
    store.mint('tok-1', 'demo-cp-op') // the broker minted this token, bound to the command
    const confirmation: ConfirmationEvidence = {
      token: 'tok-1',
      command: 'demo-cp-op',
      confirmedBy: BOB,
    }
    const result = await secureInvoke(
      { command: 'demo-cp-op', capabilityId: 'tts', request: ttsRequest('corr-cp-conf'), confirmation },
      ALICE,
      testPdp,
      execute,
      { decisionSink: sink, consumeToken: store.consume },
    )
    expect(execute).toHaveBeenCalledTimes(1) // valid token → executes
    expect(result.status).toBe('succeeded')
    expect(sink.decisions[0]!.decision.authority).toBe('CP')
    expect(sink.decisions[0]!.outcome).toBe('always-allow')
  })

  it('REJECTS a FABRICATED confirmation — shape is valid but the token was never minted', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const store = tokenStore() // empty: nothing minted, so no token can verify
    const result = await secureInvoke(
      {
        command: 'demo-cp-op',
        capabilityId: 'tts',
        request: ttsRequest('corr-cp-forged'),
        // A caller fabricates a confirmation WITHOUT the broker — right shape, no real token.
        confirmation: { token: 'i-made-this-up', command: 'demo-cp-op', confirmedBy: BOB },
      },
      ALICE,
      testPdp,
      execute,
      { consumeToken: store.consume },
    )
    expect(execute).not.toHaveBeenCalled()
    expect(result.error?.code).toBe('membrane.authority_rejected')
  })

  it('REJECTS a REPLAYED token — the SECOND invoke with the same token fails (single-use)', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const store = tokenStore()
    store.mint('tok-replay', 'demo-cp-op')
    const confirmation: ConfirmationEvidence = {
      token: 'tok-replay',
      command: 'demo-cp-op',
      confirmedBy: BOB,
    }
    // First invoke: token consumed → allowed.
    const first = await secureInvoke(
      { command: 'demo-cp-op', capabilityId: 'tts', request: ttsRequest('corr-replay-1'), confirmation },
      ALICE,
      testPdp,
      execute,
      { consumeToken: store.consume },
    )
    expect(first.status).toBe('succeeded')
    expect(execute).toHaveBeenCalledTimes(1)
    // Replay the SAME confirmation: the token is already consumed → rejected.
    const second = await secureInvoke(
      { command: 'demo-cp-op', capabilityId: 'tts', request: ttsRequest('corr-replay-2'), confirmation },
      ALICE,
      testPdp,
      execute,
      { consumeToken: store.consume },
    )
    expect(second.status).toBe('failed')
    expect(second.error?.code).toBe('membrane.authority_rejected')
    expect(execute).toHaveBeenCalledTimes(1) // execute NOT called a second time
  })

  it('REJECTS a token minted for a DIFFERENT command (token binding is command-scoped)', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const store = tokenStore()
    store.mint('tok-other', 'other-cp-op') // valid token, but bound to ANOTHER command
    const result = await secureInvoke(
      {
        command: 'demo-cp-op',
        capabilityId: 'tts',
        request: ttsRequest('corr-cp-wrongcmd'),
        confirmation: { token: 'tok-other', command: 'demo-cp-op', confirmedBy: BOB },
      },
      ALICE,
      testPdp,
      execute,
      { consumeToken: store.consume },
    )
    expect(execute).not.toHaveBeenCalled()
    expect(result.error?.code).toBe('membrane.authority_rejected')
  })

  it('REJECTS when the confirmation NAMES a different command (shape mismatch, no token burned)', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const store = tokenStore()
    store.mint('tok-keep', 'demo-cp-op')
    const consume = vi.fn(store.consume)
    const result = await secureInvoke(
      {
        command: 'demo-cp-op',
        capabilityId: 'tts',
        request: ttsRequest('corr-cp-mismatch'),
        // Confirmation's `command` names a different op → fails the shape check first.
        confirmation: { token: 'tok-keep', command: 'other-cp-op', confirmedBy: BOB },
      },
      ALICE,
      testPdp,
      execute,
      { consumeToken: consume },
    )
    expect(execute).not.toHaveBeenCalled()
    expect(result.error?.code).toBe('membrane.authority_rejected')
    // The shape check short-circuited BEFORE consume — the real token was NOT burned.
    expect(consume).not.toHaveBeenCalled()
    expect(store.consume('tok-keep', 'demo-cp-op')).toBe(true) // still live
  })

  it('REJECTS when the confirmation has an anonymous confirmedBy (never confirmed)', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const store = tokenStore()
    store.mint('tok-anon', 'demo-cp-op')
    const result = await secureInvoke(
      {
        command: 'demo-cp-op',
        capabilityId: 'tts',
        request: ttsRequest('corr-cp-anon'),
        // A FORGED confirmedBy — a bare id-shaped object, never minted. That is the whole subject
        // of this test: the PEP must reject an anonymous confirmer, so the value has to be one no
        // mint would ever hand out.
        confirmation: {
          token: 'tok-anon',
          command: 'demo-cp-op',
          confirmedBy: { id: '   ' } as MembranePrincipal,
        },
      },
      ALICE,
      testPdp,
      execute,
      { consumeToken: store.consume },
    )
    expect(execute).not.toHaveBeenCalled()
    expect(result.error?.code).toBe('membrane.authority_rejected')
  })

  it('FAIL-CLOSED: a CP invoke with a token but NO consumer wired is rejected', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const result = await secureInvoke(
      {
        command: 'demo-cp-op',
        capabilityId: 'tts',
        request: ttsRequest('corr-cp-noconsumer'),
        confirmation: { token: 'tok-x', command: 'demo-cp-op', confirmedBy: BOB },
      },
      ALICE,
      testPdp,
      execute,
      // No `consumeToken` — the face wired no verifier; the gate cannot pass a CP command.
    )
    expect(execute).not.toHaveBeenCalled()
    expect(result.error?.code).toBe('membrane.authority_rejected')
  })

  it('an AP `invoke` is UNAFFECTED — allowed with no confirmation, token never consumed', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const store = tokenStore()
    store.mint('tok-untouched', 'invoke')
    const consume = vi.fn(store.consume)
    // Default command is `invoke` (AP); no confirmation supplied.
    const result = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest('corr-ap-unaffected') },
      ALICE,
      testPdp,
      execute,
      { consumeToken: consume },
    )
    expect(execute).toHaveBeenCalledTimes(1)
    expect(result.status).toBe('succeeded')
    expect(consume).not.toHaveBeenCalled() // AP short-circuits before the token check
  })
})

/**
 * The verifiable-principal authenticate path (ADR 0134 P1b-2). When a face injects a
 * `verifyPrincipal` verifier (a face that mints node-SIGNED principals), authenticate
 * REQUIRES a signed envelope that verifies (Ed25519 + ±replay window + fresh nonce).
 * The verified principal becomes the authenticated actor. Composes with P1a/P1b-1.
 */
describe('Secure-face PEP — verifiable principal (P1b-2)', () => {
  const now = Date.parse('2026-06-19T12:00:00.000Z')
  const key = NodeSigningKey.fromSeedHex(Buffer.alloc(32, 7).toString('hex'))

  /** A verifier wired exactly as the trusted host would: trust THIS node key + a seen-nonce set. */
  function hostVerifier(seen = new SeenNonceSet()): PrincipalVerifier {
    return (envelope) =>
      verifySignedPrincipal(envelope, {
        trustedIssuers: new Set([key.issuerId]),
        seenNonces: seen,
        now: () => now,
      })
  }

  it('ACCEPTS an invoke carrying a GENUINE node-signed principal and reaches execute', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const sink = new InMemoryDecisionSink()
    const signed = signPrincipal(ALICE, key, { now: () => now })
    const result = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest('corr-signed-ok'), signedPrincipal: signed },
      ALICE,
      testPdp,
      execute,
      { verifyPrincipal: hostVerifier(), decisionSink: sink },
    )
    expect(execute).toHaveBeenCalledTimes(1)
    expect(result.status).toBe('succeeded')
    // The VERIFIED principal is what the decision sink records.
    expect(sink.decisions[0]!.principal.id).toBe('os:alice')
  })

  it('REJECTS an invoke with NO signed envelope when a verifier is wired (fail-closed)', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const result = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest('corr-signed-missing') },
      ALICE,
      testPdp,
      execute,
      { verifyPrincipal: hostVerifier() },
    )
    expect(execute).not.toHaveBeenCalled()
    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('membrane.principal_unverified')
  })

  it('REJECTS a TAMPERED signed principal (mutated id → signature-fail)', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const signed = signPrincipal(ALICE, key, { now: () => now })
    const tampered = { ...signed, principal: { ...signed.principal, id: 'os:mallory' } }
    const result = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest('corr-signed-tamper'), signedPrincipal: tampered },
      ALICE,
      testPdp,
      execute,
      { verifyPrincipal: hostVerifier() },
    )
    expect(execute).not.toHaveBeenCalled()
    expect(result.error?.code).toBe('membrane.principal_unverified')
  })

  it('REJECTS a principal signed by an UNTRUSTED node key', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const attacker = NodeSigningKey.fromSeedHex(Buffer.alloc(32, 9).toString('hex'))
    const signed = signPrincipal(ALICE, attacker, { now: () => now })
    const result = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest('corr-signed-untrusted'), signedPrincipal: signed },
      ALICE,
      testPdp,
      execute,
      { verifyPrincipal: hostVerifier() },
    )
    expect(execute).not.toHaveBeenCalled()
    expect(result.error?.code).toBe('membrane.principal_unverified')
  })

  it('REJECTS a REPLAYED signed principal (same nonce, second invoke)', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const seen = new SeenNonceSet()
    const verifier = hostVerifier(seen) // shared across both invokes
    const signed = signPrincipal(ALICE, key, { now: () => now, nonce: 'replay-nonce-1' })
    const first = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest('corr-replay-a'), signedPrincipal: signed },
      ALICE,
      testPdp,
      execute,
      { verifyPrincipal: verifier },
    )
    expect(first.status).toBe('succeeded')
    const second = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest('corr-replay-b'), signedPrincipal: signed },
      ALICE,
      testPdp,
      execute,
      { verifyPrincipal: verifier },
    )
    expect(second.status).toBe('failed')
    expect(second.error?.code).toBe('membrane.principal_unverified')
    expect(execute).toHaveBeenCalledTimes(1) // execute NOT reached on the replay
  })

  it('REJECTS a STALE signed principal (issuedAt outside the ±30s window)', async () => {
    const execute = vi.fn(async () => okEnvelope())
    // Signed 31s in the past relative to the verifier clock.
    const signed = signPrincipal(ALICE, key, { now: () => now - 31_000 })
    const result = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest('corr-stale'), signedPrincipal: signed },
      ALICE,
      testPdp,
      execute,
      { verifyPrincipal: hostVerifier() },
    )
    expect(execute).not.toHaveBeenCalled()
    expect(result.error?.code).toBe('membrane.principal_unverified')
  })

  it('COMPOSES with P1b-1: a CP op with a GENUINE signed principal + a valid token executes', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const store = tokenStore()
    store.mint('cp-tok', 'demo-cp-op')
    const signed = signPrincipal(ALICE, key, { now: () => now })
    const result = await secureInvoke(
      {
        command: 'demo-cp-op',
        capabilityId: 'tts',
        request: ttsRequest('corr-signed-cp'),
        signedPrincipal: signed,
        confirmation: { token: 'cp-tok', command: 'demo-cp-op', confirmedBy: ALICE },
      },
      ALICE,
      testPdp,
      execute,
      { verifyPrincipal: hostVerifier(), consumeToken: store.consume },
    )
    expect(execute).toHaveBeenCalledTimes(1)
    expect(result.status).toBe('succeeded')
  })

  it('COMPOSES with P1b-1: a CP op with a genuine principal but NO token is still rejected', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const store = tokenStore()
    const signed = signPrincipal(ALICE, key, { now: () => now })
    const result = await secureInvoke(
      {
        command: 'demo-cp-op',
        capabilityId: 'tts',
        request: ttsRequest('corr-signed-cp-notoken'),
        signedPrincipal: signed,
      },
      ALICE,
      testPdp,
      execute,
      { verifyPrincipal: hostVerifier(), consumeToken: store.consume },
    )
    expect(execute).not.toHaveBeenCalled()
    // The principal verified, but the CP gate (P1b-1) rejects the missing confirmation.
    expect(result.error?.code).toBe('membrane.authority_rejected')
  })

  it('BACKWARD-COMPAT: with NO verifier wired, a host-stamped principal still authenticates (P0/P1b-1)', async () => {
    const execute = vi.fn(async () => okEnvelope())
    const result = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest('corr-nover') },
      ALICE,
      testPdp,
      execute,
      // No verifyPrincipal — the additive signed path is off; existing wiring intact.
    )
    expect(execute).toHaveBeenCalledTimes(1)
    expect(result.status).toBe('succeeded')
  })
})

describe('Secure-face PEP — execute delegation + M3 promise', () => {
  it('passes the request through to execute unchanged', async () => {
    const req = ttsRequest('corr-passthrough')
    let seen: InvokeRequest | null = null
    await secureInvoke(
      { capabilityId: 'tts', request: req },
      ALICE,
      testPdp,
      async (r) => {
        seen = r
        return okEnvelope()
      },
    )
    expect(seen).toBe(req) // the PEP does not mutate the request
  })

  it('an executor that returns a failed envelope surfaces it (M3 — no throw)', async () => {
    const failed: CapabilityResult = {
      ...okEnvelope(),
      status: 'failed',
      error: { faultDomain: 'provider', retryable: true, code: 'provider.boom', message: 'boom' },
    }
    const result = await secureInvoke(
      { capabilityId: 'tts', request: ttsRequest() },
      ALICE,
      testPdp,
      async () => failed,
    )
    expect(result.status).toBe('failed')
    expect(result.error?.code).toBe('provider.boom')
  })
})
