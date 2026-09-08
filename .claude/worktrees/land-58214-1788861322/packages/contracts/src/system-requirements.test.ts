import { describe, expect, it } from 'vitest'
import {
  type SystemRequirementsResult,
  parseSystemRequirementsResult,
} from './system-requirements.js'

describe('parseSystemRequirementsResult', () => {
  it('returns a complete result unchanged', () => {
    const input = {
      overall: 'WarnOnly',
      dimensions: [{ dimension: 'Network', policy: 'Recommended', outcome: 'Fail' }],
      operatorRecoveryAction: { actionKey: 'check-network', argumentMap: { retry: 'true' } },
      evaluatedAt: '2026-05-12T00:00:00+00:00',
    }

    expect(parseSystemRequirementsResult(input)).toEqual(input)
  })

  it('accepts empty strings and an empty dimensions list when required field types are present', () => {
    const input = { overall: '', dimensions: [], evaluatedAt: '' }

    expect(parseSystemRequirementsResult(input)).toEqual(input)
  })

  it.each([
    [undefined, 'expected object'],
    [null, 'expected object'],
    ['result', 'expected object'],
    [{ dimensions: [], evaluatedAt: '2026-05-12T00:00:00+00:00' }, 'missing required field "overall"'],
    [{ overall: 'Pass', dimensions: {}, evaluatedAt: '2026-05-12T00:00:00+00:00' }, 'missing required field "dimensions"'],
    [{ overall: 'Pass', dimensions: [], evaluatedAt: 0 }, 'missing required field "evaluatedAt"'],
  ])('rejects malformed input %#', (input, message) => {
    expect(() => parseSystemRequirementsResult(input)).toThrow(new TypeError(`SystemRequirementsResult: ${message}`))
  })

  it('returns the declared wire-format type', () => {
    const result: SystemRequirementsResult = parseSystemRequirementsResult({
      overall: 'Pass',
      dimensions: [],
      evaluatedAt: '2026-05-12T00:00:00+00:00',
    })

    expect(result.overall).toBe('Pass')
  })
})
