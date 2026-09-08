/**
 * Trusted host-only issuers for Secure-face credentials.
 *
 * This module is intentionally absent from the package exports map and from `src/index.ts`.
 * The composition root imports it directly; renderer and SDK callers receive only composed
 * operations that consume the resulting credentials.
 */

import type { MembranePrincipal, ConfirmationEvidence } from './pep.js'
import {
  registerConfirmationEvidence,
  registerTrustedConfirmationBroker,
} from './credential-boundary.js'

interface OutstandingConfirmation {
  readonly command: string
  confirmedBy?: MembranePrincipal
}

const brokerStates = new WeakMap<TrustedConfirmationBroker, Map<string, OutstandingConfirmation>>()

/** Host-owned, single-use issuer consumed by the PEP; never a public Capability API. */
export class TrustedConfirmationBroker {
  constructor() {
    brokerStates.set(this, new Map())
    registerTrustedConfirmationBroker(this)
  }

  propose(command: string): string {
    const token = globalThis.crypto?.randomUUID?.()
    if (token == null) throw new Error('confirmation broker requires Web Crypto randomUUID')
    brokerStates.get(this)!.set(token, { command })
    return token
  }

  confirm(token: string, command: string, confirmedBy: MembranePrincipal): ConfirmationEvidence {
    const entry = brokerStates.get(this)?.get(token)
    if (entry == null || entry.command !== command) {
      throw new Error('confirmation token is unknown or bound to another command')
    }
    entry.confirmedBy = confirmedBy
    const evidence = Object.freeze({ token, command, confirmedBy })
    registerConfirmationEvidence(evidence)
    return evidence
  }

  consume(token: string, command: string): boolean {
    const state = brokerStates.get(this)
    if (state == null) return false
    const entry = state.get(token)
    if (entry == null || entry.command !== command || entry.confirmedBy == null) return false
    state.delete(token)
    return true
  }
}
