/**
 * Host-only principal minting. The renderer and wire caller receive the resulting value; they
 * do not construct the branded membrane principal themselves.
 */

import { userInfo } from 'node:os'

import { localOsUserPrincipal, type Principal } from '@harborline-software/api-contracts/principal'

import type { MembranePrincipal } from './pep.js'
import { registerMembranePrincipal } from './credential-boundary.js'

/**
 * Mint a membrane principal from a host-supplied shared Principal. This issuer is host-internal:
 * the result is registered in a private capability set as well as frozen, so a caller cannot
 * replace host issuance with an id-shaped object or a type assertion.
 */
export function mintMembranePrincipal(principal: Principal): MembranePrincipal {
  if (
    principal == null
    || typeof principal !== 'object'
    || typeof principal.id !== 'string'
    || typeof principal.displayName !== 'string'
    || (principal.kind !== 'local-os-user' && principal.kind !== 'service')
  ) {
    throw new TypeError('host principal must have id, displayName, and kind fields')
  }
  const minted = Object.freeze({
    id: principal.id,
    displayName: principal.displayName,
    kind: principal.kind,
  }) as MembranePrincipal
  registerMembranePrincipal(minted)
  return minted
}

/** Mint the host-stamped principal from the current OS identity. */
export function currentHostPrincipal(): MembranePrincipal {
  const username = userInfo().username.trim()
  if (username.length === 0) throw new Error('host OS username is unavailable')
  return mintMembranePrincipal(localOsUserPrincipal(username))
}
