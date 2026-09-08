import { localOsUserPrincipal, type Principal } from '@harborline-software/api-contracts/principal'
import { describe, expect, it } from 'vitest'

import { mintMembranePrincipal } from './host-principal.js'
import { createPolicyEvaluator, loadPolicyStore } from './authority-registry.js'

describe('membrane credential mints', () => {
  it('loads the real registry and preserves AP classification', () => {
    const store = loadPolicyStore()
    const evaluate = createPolicyEvaluator(store)

    expect(Object.isFrozen(store)).toBe(true)
    expect(Object.isFrozen(store.commands)).toBe(true)
    expect(store.readable).toBe(true)
    expect(evaluate('invoke').authority).toBe('AP')
    expect(evaluate('demo-cp-op').authority).toBe('CP')
    expect(evaluate('not-in-the-registry').authority).toBe('CP')
  })

  it('refuses to create an evaluator when every policy source is missing', () => {
    const store = loadPolicyStore([])

    expect(store.readable).toBe(false)
    expect(() => createPolicyEvaluator(store)).toThrow(
      'command authority registry is unreadable or invalid',
    )
  })

  it('refuses to create an evaluator from a corrupt policy source', () => {
    const store = loadPolicyStore([new URL('./credential-mints.test.ts', import.meta.url)])

    expect(store.readable).toBe(false)
    expect(() => createPolicyEvaluator(store)).toThrow(
      'command authority registry is unreadable or invalid',
    )
  })

  it('snapshots a valid host principal before applying the membrane brand', () => {
    const source: Principal = localOsUserPrincipal('alice')
    const principal = mintMembranePrincipal(source)

    source.id = 'os:changed'

    expect(principal).toEqual({ id: 'os:alice', displayName: 'alice', kind: 'local-os-user' })
    expect(Object.isFrozen(principal)).toBe(true)
  })

  it('rejects a runtime value that is not a Principal-shaped object', () => {
    expect(() => mintMembranePrincipal({ id: 'os:alice' } as never)).toThrow(
      'host principal must have id, displayName, and kind fields',
    )
  })
})
