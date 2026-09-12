// The named flake registry (ticket 284). Pure, so eng/tests/flake-registry.test.mjs drives it
// without a gate run.
//
// The rows themselves live in eng/baselines/host-test-baseline.json under `knownFlaky`, because
// that file is already the gate's failure-identity authority and is already blob-pinned against
// HEAD by run-exact-clone.mjs. A second file would be a second authority.
//
// What this adds to those rows: every row must be EXACT (a test identity that the failed-line
// grammar can produce), OWNED (a ticket that is answerable for removing it), DATED (first seen)
// and EXPIRING. An unowned or expired row is a gate failure, so "known flaky" cannot decay into a
// permanent exemption list, which is the failure mode Google's and Spotify's published policies
// both cap.
//
// The ratchet is REGISTERED_FLAKE_COUNT below: the registry may not grow past it. The literal
// lives in this source file and the rows live in the baseline JSON, so growing the registry means
// moving the literal in the same commit — a reviewable diff line rather than one more JSON row.
// Shrinking is not a ratchet violation; the count is a ceiling, not an equality.
export const REGISTERED_FLAKE_COUNT = 4 // 361 retired its MD-2 G-4 row after the tenth clean queue landing (2026-09-11); ConcurrentFirstLaunchers is owned by 348

// ONE identical retry (ticket 284 scope: "retries the failing registered test once with the
// identical configuration"). A larger limit turns a real regression that fails intermittently into
// a pass, and the extra attempts buy no information the first retry did not already give.
export const RETRY_LIMIT = 1

const DATE = /^\d{4}-\d{2}-\d{2}$/

// `today` is passed in, never read from the clock here: a validator that reads the clock cannot be
// tested against an expiry boundary.
export function validateFlakeRegistry(rows, today, ceiling = REGISTERED_FLAKE_COUNT) {
  const problems = []
  if (!Array.isArray(rows)) return ['knownFlaky is not an array']
  if (!DATE.test(today ?? '')) return [`today must be YYYY-MM-DD, got ${JSON.stringify(today)}`]
  if (rows.length > ceiling) {
    problems.push(`the flake registry has ${rows.length} rows but the ratchet allows ${ceiling}; `
      + 'move REGISTERED_FLAKE_COUNT in eng/flake-registry.mjs in the same commit, with the owning ticket')
  }
  const seen = new Set()
  for (const [index, row] of rows.entries()) {
    const at = `knownFlaky[${index}]`
    const name = typeof row?.test === 'string' ? row.test.trim() : ''
    if (!name) { problems.push(`${at}: no exact test identity`); continue }
    if (seen.has(name)) problems.push(`${at}: duplicate registration of "${name}"`)
    seen.add(name)
    if (!(typeof row.owner === 'string' && row.owner.trim())) problems.push(`${at} ("${name}"): no owner ticket`)
    if (!DATE.test(row.firstSeen ?? '')) problems.push(`${at} ("${name}"): firstSeen must be YYYY-MM-DD`)
    if (!DATE.test(row.expires ?? '')) problems.push(`${at} ("${name}"): expires must be YYYY-MM-DD`)
    else {
      if (DATE.test(row.firstSeen ?? '') && row.expires <= row.firstSeen) {
        problems.push(`${at} ("${name}"): expires ${row.expires} is not after firstSeen ${row.firstSeen}`)
      }
      // ISO dates compare correctly as strings, which is the whole reason the format is pinned.
      if (row.expires < today) {
        problems.push(`${at} ("${name}"): registration expired ${row.expires} (owner ${row.owner ?? 'none'}); `
          + 'fix the flake or re-register it with a new expiry and a reason')
      }
    }
    if ((row.retryLimit ?? RETRY_LIMIT) !== RETRY_LIMIT) {
      problems.push(`${at} ("${name}"): retryLimit must be ${RETRY_LIMIT} (one identical retry), got ${row.retryLimit}`)
    }
  }
  return problems
}
