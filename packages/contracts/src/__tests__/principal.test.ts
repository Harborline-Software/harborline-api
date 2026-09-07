/**
 * Contract test for the `principal` namespace — WHO is acting (the actor an op is
 * attributed to). The single type source the renderer + harborline-sdk broker + CLI
 * project (agent-client doctrine, CIC 2026-06-18; SPOT-CHECK 2026-06-18 item A).
 *
 * Two things matter and are pinned here:
 *  1. `localOsUserPrincipal` builds the SHARED `Principal` shape so every source
 *     (Rust host / node:os SDK+CLI) produces an identical principal.
 *  2. The principal is ALWAYS non-null + attributable (a blank username falls back
 *     to `unknown`) — the authority gate must never run anonymously.
 */

import { describe, it, expect } from 'vitest'

import {
  type Principal,
  type LocalOsUserPrincipal,
  type PrincipalKind,
  localOsUserPrincipal,
  isHumanPrincipal,
  OS_USER_ID_PREFIX,
} from '../principal.js'

describe('principal contract', () => {
  it('builds a source-explicit OS-user principal (id namespaced, kind marked)', () => {
    const p = localOsUserPrincipal('<user>')
    expect(p.id).toBe('os:<user>')
    expect(p.id.startsWith(OS_USER_ID_PREFIX)).toBe(true)
    expect(p.displayName).toBe('<user>')
    expect(p.kind).toBe('local-os-user')
  })

  it('uses a supplied display name but keeps the id from the username', () => {
    const p = localOsUserPrincipal('jdoe', 'Jane Doe')
    expect(p.id).toBe('os:jdoe')
    expect(p.displayName).toBe('Jane Doe')
  })

  it('NEVER yields an anonymous principal: a blank username falls back to `unknown`', () => {
    for (const blank of ['', '   ', '\t']) {
      const p = localOsUserPrincipal(blank)
      expect(p.id).toBe('os:unknown')
      expect(p.displayName).toBe('unknown')
      expect(p.kind).toBe('local-os-user')
    }
  })

  it('trims surrounding whitespace from the username', () => {
    const p = localOsUserPrincipal('  alice  ')
    expect(p.id).toBe('os:alice')
    expect(p.displayName).toBe('alice')
  })

  it('the kind discriminator carries the implemented sources (human + non-human)', () => {
    // A compile-time exhaustiveness guard: the implemented kinds are `local-os-user`
    // (human) and `service` (non-human agent host, ADR 0143 — the CP separation-of-duties
    // boundary). A future auth `kind` is ADDITIVE — adding one here is a deliberate,
    // reviewable change, not a silent widening.
    const kinds: PrincipalKind[] = ['local-os-user', 'service']
    expect(kinds).toEqual(['local-os-user', 'service'])
  })

  it('a LocalOsUserPrincipal IS a Principal (structural subtype)', () => {
    const os: LocalOsUserPrincipal = localOsUserPrincipal('bob')
    const p: Principal = os // assignable — narrowed kind still satisfies the base
    expect(p.kind).toBe('local-os-user')
  })

  // ── isHumanPrincipal — the CP separation-of-duties predicate (ADR 0143 D-INV-5) ──
  describe('isHumanPrincipal', () => {
    it('a local-os-user IS human (the accountable, interactive desktop user)', () => {
      expect(isHumanPrincipal(localOsUserPrincipal('alice'))).toBe(true)
    })

    it('a service/agent principal is NOT human (may propose a CP op, never confirm one)', () => {
      const agent: Principal = { id: 'svc:kg-agent', displayName: 'KG Agent', kind: 'service' }
      expect(isHumanPrincipal(agent)).toBe(false)
    })

    it('fails closed: an unknown/malformed kind is NOT human', () => {
      const bogus = { id: 'x:y', displayName: 'y', kind: 'totally-unknown' } as unknown as Principal
      expect(isHumanPrincipal(bogus)).toBe(false)
    })
  })
})
