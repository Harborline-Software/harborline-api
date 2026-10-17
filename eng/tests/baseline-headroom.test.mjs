import assert from 'node:assert/strict'
import test from 'node:test'
import {reviewedDebtBudgetHeadroom, flakeRegistryHeadroom, formatBaselineHeadroom} from '../baseline-headroom.mjs'

// Literal fixtures pin the report's arithmetic and labels independently of real baseline values.
test('a full reviewed debt budget prints its zero headroom explicitly', () => {
  const summary = reviewedDebtBudgetHeadroom('# reviewed-aggregate-debt-ceiling\t432\nHealth/A.cs\t400\t0\nHealth/B.cs\t30\t2\n')
  assert.deepEqual(summary, {label: 'reviewed debt-ledger budget', status: 'available', used: 432, ceiling: 432, headroom: 0})
  assert.equal(formatBaselineHeadroom(summary), 'reviewed debt-ledger budget: 432 of 432; headroom 0')
})

test('only the current header and budget columns contribute, with CRLF and historical comments', () => {
  const summary = reviewedDebtBudgetHeadroom('# 2026-09-28 prior ceiling 440\tretired\r\n# reviewed-aggregate-debt-ceiling\t17\r\nHealth/A.cs\t2\t1\tReviewed reason\r\nHealth/B.cs\t5\t0\r\n')
  assert.deepEqual(summary, {label: 'reviewed debt-ledger budget', status: 'available', used: 8, ceiling: 17, headroom: 9})
  assert.equal(formatBaselineHeadroom(summary), 'reviewed debt-ledger budget: 8 of 17; headroom 9')
})

test('an overfull debt budget retains negative headroom rather than clamping it', () => {
  const summary = reviewedDebtBudgetHeadroom('# reviewed-aggregate-debt-ceiling\t3\nHealth/A.cs\t3\t1\n')
  assert.deepEqual(summary, {label: 'reviewed debt-ledger budget', status: 'available', used: 4, ceiling: 3, headroom: -1})
  assert.equal(formatBaselineHeadroom(summary), 'reviewed debt-ledger budget: 4 of 3; headroom -1')
})

test('missing or duplicate ceiling metadata is unavailable rather than a fabricated zero', () => {
  for (const ledger of ['Health/A.cs\t1\t0', '# reviewed-aggregate-debt-ceiling\t3\n# reviewed-aggregate-debt-ceiling\t3']) {
    assert.deepEqual(reviewedDebtBudgetHeadroom(ledger), {
      label: 'reviewed debt-ledger budget', status: 'unavailable', reason: 'expected one reviewed ceiling header',
    })
  }
})

test('malformed and unsafe ceiling values are unavailable', () => {
  for (const ceiling of ['', '-1', '3.5', 'none', '9007199254740992', '3\textra']) {
    assert.deepEqual(reviewedDebtBudgetHeadroom(`# reviewed-aggregate-debt-ceiling\t${ceiling}\nHealth/A.cs\t1\t0`), {
      label: 'reviewed debt-ledger budget', status: 'unavailable', reason: 'invalid reviewed ceiling',
    })
  }
})

test('malformed or duplicate budget rows do not become a plausible count', () => {
  for (const rows of ['Health/A.cs\t1', 'Health/A.cs\t-1\t0', 'Health/A.cs\t1.5\t0', 'Health/A.cs\tx\t0', 'Health/A.cs\t1\t0\nHealth/A.cs\t2\t0']) {
    assert.deepEqual(reviewedDebtBudgetHeadroom(`# reviewed-aggregate-debt-ceiling\t17\n${rows}`), {
      label: 'reviewed debt-ledger budget', status: 'unavailable', reason: 'invalid or duplicate reviewed budget row',
    })
  }
})

test('an unsafe aggregate is unavailable even when each individual budget is safe', () => {
  assert.deepEqual(reviewedDebtBudgetHeadroom('# reviewed-aggregate-debt-ceiling\t17\nHealth/A.cs\t9007199254740991\t1'), {
    label: 'reviewed debt-ledger budget', status: 'unavailable', reason: 'reviewed budget total is not a safe count',
  })
})

test('an unreadable ledger is explicitly unavailable and the formatter preserves that distinction', () => {
  const summary = reviewedDebtBudgetHeadroom(undefined)
  assert.deepEqual(summary, {label: 'reviewed debt-ledger budget', status: 'unavailable', reason: 'reviewed debt ledger unavailable'})
  assert.equal(formatBaselineHeadroom(summary), 'reviewed debt-ledger budget: unavailable (reviewed debt ledger unavailable)')
})

test('zero registered flakes against a zero ceiling is reported without granting an allowance', () => {
  const rows = Object.freeze([])
  const summary = flakeRegistryHeadroom(rows, 0)
  assert.deepEqual(summary, {label: 'flake registrations', status: 'available', used: 0, ceiling: 0, headroom: 0})
  assert.equal(formatBaselineHeadroom(summary), 'flake registrations: 0 of 0; headroom 0')
  assert.deepEqual(rows, [])
})

test('flake headroom preserves both positive space and an overfull zero ceiling', () => {
  const rows = Object.freeze([Object.freeze({test: 'Literal.Test.Identity'})])
  assert.deepEqual(flakeRegistryHeadroom(rows, 3), {label: 'flake registrations', status: 'available', used: 1, ceiling: 3, headroom: 2})
  const overfull = flakeRegistryHeadroom(rows, 0)
  assert.deepEqual(overfull, {label: 'flake registrations', status: 'available', used: 1, ceiling: 0, headroom: -1})
  assert.equal(formatBaselineHeadroom(overfull), 'flake registrations: 1 of 0; headroom -1')
  assert.deepEqual(rows, [{test: 'Literal.Test.Identity'}])
})

test('an absent flake registry is unavailable rather than an observed empty registry', () => {
  const summary = flakeRegistryHeadroom(undefined, 0)
  assert.deepEqual(summary, {label: 'flake registrations', status: 'unavailable', reason: 'flake registry unavailable'})
  assert.equal(formatBaselineHeadroom(summary), 'flake registrations: unavailable (flake registry unavailable)')
})

test('unavailable flake rows and invalid ceilings do not report fabricated headroom', () => {
  assert.deepEqual(flakeRegistryHeadroom(null, 0), {label: 'flake registrations', status: 'unavailable', reason: 'flake registry unavailable'})
  for (const ceiling of [-1, 0.5, NaN, Infinity, '0', undefined, 9007199254740992]) {
    assert.deepEqual(flakeRegistryHeadroom([], ceiling), {label: 'flake registrations', status: 'unavailable', reason: 'invalid flake ceiling'})
  }
})
