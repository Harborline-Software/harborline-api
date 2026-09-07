import { describe, expect, it } from 'vitest'

import { REDACTION_MARKER, redact, redactDeep } from './redaction.js'

describe('SEC-3 — redaction at the M3 normalization boundary', () => {
  it('scrubs a caller-registered secret literal by exact substring', () => {
    const out = redact('connecting with token tok_supersecretvalue now', {
      secrets: ['tok_supersecretvalue'],
    })
    expect(out).not.toContain('tok_supersecretvalue')
    expect(out).toContain(REDACTION_MARKER)
  })

  it('scrubs credential SHAPES without registration (bearer / key= / sk- / Password=)', () => {
    expect(redact('Authorization: Bearer abcDEF123456789xyz')).not.toContain(
      'abcDEF123456789xyz',
    )
    expect(redact('apiKey=ABCD1234EFGH')).not.toContain('ABCD1234EFGH')
    expect(redact('using sk-ABCDEFGHIJKLMNOPQRST')).not.toContain(
      'sk-ABCDEFGHIJKLMNOPQRST',
    )
    expect(redact('Server=db;Password=hunter2secret;Db=x')).not.toContain('hunter2secret')
  })

  it('ignores too-short registered secrets (avoids scrubbing everything)', () => {
    const out = redact('the a b c quick fox', { secrets: ['a'] })
    expect(out).toBe('the a b c quick fox')
  })

  it('redactDeep walks nested structures but PRESERVES correlationId (trace id, not a secret)', () => {
    const record = {
      correlationId: 'corr-12345-trace',
      message: 'failed with Bearer leakedtoken12345',
      nested: { detail: 'apiKey=NESTEDLEAK1234', correlationId: 'corr-inner' },
      list: ['Bearer anotherleak98765'],
    }
    const out = redactDeep(record)

    // correlationId survives at every depth.
    expect(out.correlationId).toBe('corr-12345-trace')
    expect(out.nested.correlationId).toBe('corr-inner')

    // secrets are gone.
    expect(JSON.stringify(out)).not.toContain('leakedtoken12345')
    expect(JSON.stringify(out)).not.toContain('NESTEDLEAK1234')
    expect(JSON.stringify(out)).not.toContain('anotherleak98765')
  })
})
