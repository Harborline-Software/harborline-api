/**
 * Contract test for the SPINE-2 `governance` namespace (ADR 0140 D2).
 *
 * Mirrors packages/foundation-governance/Policy/* (the .NET model is canonical). Two layers:
 *  1. Compile-time (`satisfies`): a representative PolicyBinding + ResolvedFieldPolicy payload
 *     is asserted to satisfy the TS interfaces — a field added/removed on the .NET side breaks
 *     this build.
 *  2. Runtime: exhaustive guards over the closed EffectKind / Trigger unions — widening one on
 *     the TS side without the .NET side breaks the `satisfies` exhaustiveness.
 */

import { describe, it, expect } from 'vitest'

import type {
  EffectKind,
  PolicyBinding,
  PolicyEffect,
  ResolvedFieldPolicy,
  Trigger,
} from '../governance.js'
import type { Tag } from '../forms.js'

describe('governance — PolicyBinding authoring shape (mirrors foundation-governance)', () => {
  it('round-trips a representative pii binding', () => {
    const tag: Tag = { system: 'shipyard/data-classification', code: 'pii' }
    const encrypt: PolicyEffect = { kind: 'Encrypt', triggers: ['Store'], params: { subjectScoped: true } }
    const redact: PolicyEffect = { kind: 'Redact', triggers: ['Read', 'Export'], params: {} }
    const audit: PolicyEffect = { kind: 'Audit', triggers: ['Read'], params: {} }

    const binding = {
      tag,
      class: {
        name: 'pii',
        requiredEffects: [{ kind: 'Encrypt', trigger: 'Store' }, { kind: 'Audit' }],
      },
      effects: [encrypt, redact, audit],
    } satisfies PolicyBinding

    expect(binding.effects[0].kind).toBe('Encrypt')
    expect(binding.effects[0].triggers).toEqual(['Store'])
    expect(binding.class?.requiredEffects[0].trigger).toBe('Store')
  })

  it('round-trips a ResolvedFieldPolicy (effects keyed by trigger)', () => {
    const resolved = {
      field: 'ssn',
      tags: [{ system: 'shipyard/data-classification', code: 'pii' }],
      effectsByTrigger: {
        Store: [{ kind: 'Encrypt', triggers: ['Store'], params: { subjectScoped: true } }],
        Read: [
          { kind: 'Redact', triggers: ['Read'], params: {} },
          { kind: 'Audit', triggers: ['Read'], params: {} },
        ],
      },
    } satisfies ResolvedFieldPolicy

    expect(resolved.effectsByTrigger.Store?.[0].kind).toBe('Encrypt')
    expect(resolved.effectsByTrigger.Read).toHaveLength(2)
  })
})

describe('governance — closed enums match the .NET enums exactly', () => {
  it('EffectKind = { Encrypt, Redact, Mask, Audit, Retain, Erase, Reside, Consent }', () => {
    const exhaustive = {
      Encrypt: 1, Redact: 1, Mask: 1, Audit: 1, Retain: 1, Erase: 1, Reside: 1, Consent: 1,
    } satisfies Record<EffectKind, number>
    expect(Object.keys(exhaustive).sort()).toEqual(
      ['Audit', 'Consent', 'Encrypt', 'Erase', 'Mask', 'Redact', 'Reside', 'Retain'])
  })

  it('Trigger = { Store, Read, Export, EraseSubject }', () => {
    const exhaustive = { Store: 1, Read: 1, Export: 1, EraseSubject: 1 } satisfies Record<Trigger, number>
    expect(Object.keys(exhaustive).sort()).toEqual(['EraseSubject', 'Export', 'Read', 'Store'])
  })
})
