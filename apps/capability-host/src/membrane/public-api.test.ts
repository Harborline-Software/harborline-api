import type { CapabilityResult, InvokeRequest } from '@harborline-software/api-contracts'
import { localOsUserPrincipal } from '@harborline-software/api-contracts/principal'
import { assertOutboundProtocolModel } from '@harborline-software/api-contracts/protocol'
import { describe, expect, it, vi } from 'vitest'

import * as publicCapability from '../index.js'
import * as publicResolution from '../resolution/index.js'
import { createSecuredCapabilityInvoke } from '../protocol/capability-port-adapter.js'
import { containsRegisteredCredential, isRegisteredMembranePrincipal } from './credential-boundary.js'
import { runDemoCpOperation } from './composed.js'
import { mintMembranePrincipal } from './host-principal.js'
import { NodeSigningKey, signPrincipal } from './host-principal-signer.js'
import { verifySignedPrincipal } from './signed-principal.js'
import { secureInvoke } from './pep.js'
import type { InvokeDecision } from './pep.js'

function request(): InvokeRequest {
  return {
    capabilityId: 'image',
    core: { prompt: 'x' } as never,
    providerInputs: {},
    attachments: [],
    idempotencyKey: 'idem-public-api',
    correlationId: 'corr-public-api',
    transport: 'sync',
  }
}

function success(): CapabilityResult {
  return {
    jobId: 'job:public-api',
    status: 'succeeded',
    progress: 1,
    artifacts: [],
    usage: { unit: 'call', quantity: 1, tier: 'local' },
    error: null,
  }
}

describe('Capability public credential boundary', () => {
  it('does not export a subject or confirmation issuer through the root barrel', () => {
    const issuerNames = [
      'mintMembranePrincipal',
      'currentHostPrincipal',
      'TrustedConfirmationBroker',
      'NodeSigningKey',
      'signPrincipal',
      'devNodeSigningKey',
    ]

    for (const publicSurface of [publicCapability, publicResolution]) {
      expect(issuerNames.filter((name) => name in publicSurface)).toEqual([])
    }
  })

  it('rejects a shape-only principal even when the PEP call-site validation is removed', async () => {
    const execute = vi.fn(async () => success())
    const forged = { id: 'os:root', displayName: 'root', kind: 'local-os-user' }

    const result = await secureInvoke(
      { capabilityId: 'image', request: request() },
      forged as never,
      execute,
    )

    expect(result.error?.code).toBe('membrane.anonymous_principal')
    expect(execute).not.toHaveBeenCalled()
  })

  it('rejects a shape-only approval artifact and fake broker capability', async () => {
    const principal = mintMembranePrincipal(localOsUserPrincipal('root'))
    const execute = vi.fn(async () => success())
    const fakeBroker = { consume: vi.fn(() => true) }

    // On the pre-fix public surface this exercised the actual self-mint path. Keeping the
    // compatibility probe makes the regression falsifiable against that implementation while
    // the fixed surface has no constructor to call.
    const legacyBroker = (publicCapability as unknown as Record<string, unknown>).TrustedConfirmationBroker
    if (typeof legacyBroker === 'function') {
      const broker = new (legacyBroker as new () => {
        propose(command: string): string
        confirm(token: string, command: string, confirmedBy: typeof principal): {
          token: string
          command: string
          confirmedBy: typeof principal
        }
      })()
      const token = broker.propose('demo-cp-op')
      const evidence = broker.confirm(token, 'demo-cp-op', principal)
      const legacyResult = await secureInvoke(
        { command: 'demo-cp-op', capabilityId: 'image', request: request(), confirmation: evidence },
        principal,
        execute,
        {},
        broker,
      )
      expect(legacyResult.error?.code).toBe('membrane.authority_rejected')
    }

    const result = await secureInvoke(
      {
        command: 'demo-cp-op',
        capabilityId: 'image',
        request: request(),
        confirmation: {
          token: 'attacker-token',
          command: 'demo-cp-op',
          confirmedBy: principal,
        },
      },
      principal,
      execute,
      {},
      fakeBroker,
    )

    expect(result.error?.code).toBe('membrane.authority_rejected')
    expect(fakeBroker.consume).not.toHaveBeenCalled()
    expect(execute).not.toHaveBeenCalled()
  })

  // ── The OUTBOUND half ──────────────────────────────────────────────────────
  //
  // Every test above asks whether a caller can CONSTRUCT a credential. None asked whether one can
  // ESCAPE, and that asymmetry is why the same defect recurred across four remediation rounds: the
  // mints were made private while the minted values still reached callers through a decision sink,
  // a shell's invoke context and a result's metadata. Since the PEP authenticates by object
  // identity, a leaked credential is a bearer token good for any command.
  //
  // `containsRegisteredCredential` walks the whole object graph rather than a known list of
  // fields, so a FOURTH egress fails these tests without anyone remembering to add it here.

  it('hands a caller-supplied decision sink attribution, never the credential', async () => {
    const principal = mintMembranePrincipal(localOsUserPrincipal('root'))
    const recorded: InvokeDecision[] = []

    await secureInvoke(
      { capabilityId: 'image', request: request() },
      principal,
      async () => success(),
      { decisionSink: { record: (d) => void recorded.push(d) } },
    )

    expect(recorded).toHaveLength(1)
    // Attribution is still carried — the fix must not blind the audit trail.
    expect(recorded[0]!.principal.id).toBe(principal.id)
    expect(containsRegisteredCredential(recorded[0])).toBe(false)
    expect(isRegisteredMembranePrincipal(recorded[0]!.principal)).toBe(false)
  })

  it('hands a caller-supplied shell attribution, never the credential', async () => {
    const contexts: unknown[] = []
    const shell = {
      invoke: async (_r: InvokeRequest, ctx?: unknown) => {
        contexts.push(ctx)
        return success()
      },
    }

    const invoke = createSecuredCapabilityInvoke({ shell: shell as never })
    await invoke(request())

    expect(contexts).toHaveLength(1)
    expect(containsRegisteredCredential(contexts[0])).toBe(false)
  })

  it('returns a composed operation result carrying no credential', async () => {
    const result = await runDemoCpOperation('outbound-guard')

    expect(result.status).toBe('succeeded')
    expect(containsRegisteredCredential(result)).toBe(false)
  })

  it('keeps the composed result valid on the wire after the credential is projected out', async () => {
    // The seam between the two halves of this work, and the one thing neither half could check
    // alone. The contract half validates `meta.proposedBy` as a `Principal`, which REQUIRES id,
    // displayName and kind. This half replaced the credential there with inert attribution, which
    // copies those fields only when they are present.
    //
    // They are present today only because `mintMembranePrincipal` validates all three at runtime,
    // while `MembranePrincipal` declares displayName and kind as OPTIONAL. Relax that mint and the
    // attribution silently loses a required field, the strict assert throws on the success path,
    // and a confirmed operation reports itself rejected — exactly the defect the contract half
    // just fixed. This test is what makes that regression loud.
    const result = await runDemoCpOperation('wire-shape')

    expect(() => assertOutboundProtocolModel(
      'CpDemoResult',
      'carrier.host.capabilityCpDemoExecute',
      result as never,
    )).not.toThrow()
  })

  it('refuses a credential observed through the public surface when replayed at another command', async () => {
    // The end-to-end property: whatever a caller can OBSERVE, feeding it back must be refused.
    const principal = mintMembranePrincipal(localOsUserPrincipal('root'))
    const recorded: InvokeDecision[] = []

    await secureInvoke(
      { capabilityId: 'image', request: request() },
      principal,
      async () => success(),
      { decisionSink: { record: (d) => void recorded.push(d) } },
    )

    const observed = recorded[0]!.principal
    const execute = vi.fn(async () => success())

    // Replayed at an AP command deliberately. A CP replay is refused by the confirmation gate even
    // when the credential authenticates, so it would go red for the wrong reason and understate the
    // impact. At AP, authenticate is the ONLY thing standing between a leaked credential and
    // execution — so `execute` not being called is the property under test, not the error code.
    const replay = await secureInvoke(
      { command: 'invoke', capabilityId: 'image', request: request() },
      observed as never,
      execute,
    )

    expect(execute).not.toHaveBeenCalled()
    expect(replay.error?.code).toBe('membrane.anonymous_principal')
  })

  it('detects a credential anywhere in a graph, not only at a known field', () => {
    // Guards the guard. If the walk stopped at the top level, every assertion above would pass
    // vacuously against a credential nested one level down.
    const principal = mintMembranePrincipal(localOsUserPrincipal('root'))

    expect(containsRegisteredCredential({ a: { b: [{ c: principal }] } })).toBe(true)
    expect(containsRegisteredCredential(new Map([['k', { p: principal }]]))).toBe(true)
    expect(containsRegisteredCredential(new Set([[principal]]))).toBe(true)
    expect(containsRegisteredCredential({ a: { b: [{ c: { id: principal.id } }] } })).toBe(false)

    // A Map KEY, not just a value.
    expect(containsRegisteredCredential(new Map([[principal, 'v']]))).toBe(true)

    // Symbol-keyed.
    expect(containsRegisteredCredential({ [Symbol('s')]: principal })).toBe(true)

    // Non-enumerable own property.
    const hidden = {}
    Object.defineProperty(hidden, 'p', { value: principal, enumerable: false })
    expect(containsRegisteredCredential(hidden)).toBe(true)

    // Class instance — own field, and behind a PROTOTYPE accessor. The second is the shape an
    // own-enumerable-only walk misses, and a class is an entirely ordinary thing for a result or a
    // context object to be. A guard that only understood object literals would have caught the
    // three channels already found and nothing else.
    class OwnField { readonly p = principal }
    expect(containsRegisteredCredential(new OwnField())).toBe(true)

    class BehindAccessor {
      readonly #p = principal
      get principal(): typeof principal { return this.#p }
    }
    expect(containsRegisteredCredential(new BehindAccessor())).toBe(true)

    // Inherited data field.
    const base = { p: principal }
    expect(containsRegisteredCredential(Object.create(base))).toBe(true)

    // A throwing accessor must not break the walk — the credential beside it is still found.
    const throwing = { get bad(): never { throw new Error('nope') }, p: principal }
    expect(containsRegisteredCredential(throwing)).toBe(true)

    const cyclic: Record<string, unknown> = {}
    cyclic.self = cyclic
    expect(containsRegisteredCredential(cyclic)).toBe(false)
  })

  it('refuses a validly-signed principal when the verifier pinned no trusted issuer', async () => {
    // A structurally-valid signature proves the envelope was not tampered with. It says nothing
    // about whether the signer is anyone we trust — `trustedIssuers` is what says that, and it is
    // optional with an "accept ANY structurally-valid signature" default.
    //
    // That default was harmless while a verification result was inert data. Registering the
    // principal made it dangerous: an unpinned verifier would turn a self-signing caller into full
    // authority, and the PEP's own registration re-check could not catch it, having been satisfied
    // during verification. Here the attacker signs with its OWN key and no issuer is pinned.
    const attacker = NodeSigningKey.fromSeedHex(Buffer.alloc(32, 9).toString('hex'))
    const subject = localOsUserPrincipal('root')
    const signed = signPrincipal(subject as never, attacker)
    const execute = vi.fn(async () => success())

    const result = await secureInvoke(
      { capabilityId: 'image', request: request(), signedPrincipal: signed },
      mintMembranePrincipal(subject),
      execute,
      // No `trustedIssuers` — the permissive default.
      { verifyPrincipal: (envelope) => verifySignedPrincipal(envelope) as never },
    )

    expect(execute).not.toHaveBeenCalled()
    expect(result.error?.code).toBe('membrane.principal_unverified')
  })

  it('rejects a verifier that returns an unregistered authenticated subject', async () => {
    const principal = mintMembranePrincipal(localOsUserPrincipal('root'))
    const execute = vi.fn(async () => success())
    const forged = { id: 'os:root', displayName: 'root', kind: 'local-os-user' }

    const result = await secureInvoke(
      {
        capabilityId: 'image',
        request: request(),
        signedPrincipal: { issuerId: 'attacker', issuedAt: Date.now(), nonce: 'n', signature: 's', principal: forged } as never,
      },
      principal,
      execute,
      { verifyPrincipal: () => ({ ok: true, principal: forged as never }) },
    )

    expect(result.error?.code).toBe('membrane.principal_unverified')
    expect(execute).not.toHaveBeenCalled()
  })
})
