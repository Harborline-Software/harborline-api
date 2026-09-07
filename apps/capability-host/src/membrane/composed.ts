/**
 * Host composition roots for the public Capability entry points.
 *
 * Credential issuance is kept in this trusted host module. The package barrel exposes only
 * operations that compose the issuer with the PEP; it never exposes the principal or confirmation
 * constructors themselves.
 */

import type { CapabilityResult, InvokeRequest } from '@harborline-software/api-contracts'

import { principalAttribution } from './credential-boundary.js'
import { TrustedConfirmationBroker } from './credential-issuer.js'
import { currentHostPrincipal } from './host-principal.js'
import { secureInvoke } from './pep.js'

/** Execute Capability's no-effect CP demonstration through the real host-owned PEP. */
export async function runDemoCpOperation(note = 'demo-cp-op from the Harborline App UI'): Promise<CapabilityResult> {
  const principal = currentHostPrincipal()
  const attribution = principalAttribution(principal)
  const broker = new TrustedConfirmationBroker()
  const command = 'demo-cp-op'
  const token = broker.propose(command)
  const confirmation = broker.confirm(token, command, principal)
  const correlationId = `harborline-demo-cp-op-${globalThis.crypto?.randomUUID?.() ?? Date.now()}`
  const request: InvokeRequest = {
    capabilityId: 'tts',
    core: { text: note, voice: null, format: 'aiff', timeout: 30_000 } as never,
    providerInputs: {},
    attachments: [],
    idempotencyKey: `idem-${correlationId}`,
    correlationId,
    transport: 'sync',
  }

  return secureInvoke(
    { command, capabilityId: 'tts', request, confirmation },
    principal,
    async () => ({
      jobId: `job:demo-cp-op:${correlationId}`,
      status: 'succeeded',
      progress: 1,
      artifacts: [],
      usage: { unit: 'call', quantity: 1, tier: 'local' },
      error: null,
      meta: {
        command,
        confirmed: true,
        note,
        executedAt: new Date().toISOString(),
        // INERT attribution. `secureInvoke` returns the executor's result unmodified, so anything
        // placed here reaches every caller — and the credential is a bearer token the PEP would
        // accept back for any command. Attribution is what a result needs; authority is not.
        proposedBy: attribution,
        confirmedBy: attribution,
      },
    }),
    {},
    broker,
  )
}
