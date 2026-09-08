import { describe, expect, it } from 'vitest'

import { isFinancialCapability } from './financial.js'

describe('isFinancialCapability', () => {
  it('recognizes the exact financial capability', () => {
    expect(isFinancialCapability('bank-import')).toBe(true)
  })

  it.each(['image', 'tts/quality', 'llm'])(
    'rejects representative non-financial capability %s',
    (capabilityId) => {
      expect(isFinancialCapability(capabilityId)).toBe(false)
    },
  )

  it.each(['bank-import/', 'bank-import/preview'])(
    'rejects slash-qualified bank-import derivative %s',
    (capabilityId) => {
      expect(isFinancialCapability(capabilityId)).toBe(false)
    },
  )

  it('keeps the documented future payment-post capability outside the v0 allow-list', () => {
    expect(isFinancialCapability('payment-post')).toBe(false)
  })

  it.each([
    '',
    ' ',
    'bank-impor',
    'bank-imports',
    'BANK-IMPORT',
    ' bank-import',
    'bank-import ',
    'unknown-capability',
  ])('fails closed for empty, boundary, or invalid capability %j', (capabilityId) => {
    expect(isFinancialCapability(capabilityId)).toBe(false)
  })
})
