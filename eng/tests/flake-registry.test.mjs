import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import path from 'node:path'
import {fileURLToPath} from 'node:url'
import {validateFlakeRegistry, planFlakeRetries, REGISTERED_FLAKE_COUNT, RETRY_LIMIT} from '../flake-registry.mjs'
import {compareHostBaseline} from '../host-baseline.mjs'

// Proof for the named flake registry (ticket 284). Each rule below is stated as "this shape is
// refused", and the last two tests run the rule against the REAL registry, so a row added to
// eng/baselines/host-test-baseline.json without an owner, without dates, past its expiry, or past
// the ratchet is caught here and not only in a gate run.
const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '..', '..')
const baseline = JSON.parse(readFileSync(path.join(root, 'eng', 'baselines', 'host-test-baseline.json'), 'utf8'))
const real = baseline.knownFlaky ?? []

const TODAY = '2026-09-07'
const valid = {test: 'Some.Namespace.Type.Method', owner: '284', firstSeen: '2026-09-01', expires: '2026-10-01', retryLimit: 0}
const retired = 'Harborline.Api.LocalNodeHost.Tests.Installation.InstallIdentityTests.ConcurrentFirstLaunchers_ObserveOneInstallIdentity'
const row = extra => ({...valid, ...extra})

test('a well-formed registry passes', () => {
  // An explicit historical ceiling exercises field validation independently of
  // today's zero-allowance policy. It does not grant production retry authority.
  assert.deepEqual(validateFlakeRegistry([row()], TODAY, 1), [])
})

test('a row without an owner is refused', () => {
  const {owner, ...unowned} = valid
  assert.match(validateFlakeRegistry([unowned], TODAY).join('\n'), /no owner ticket/)
  assert.match(validateFlakeRegistry([row({owner: '  '})], TODAY).join('\n'), /no owner ticket/)
})

test('a row past its expiry is refused, and the day of expiry is still valid', () => {
  assert.match(validateFlakeRegistry([row({expires: '2026-09-06'})], TODAY).join('\n'), /registration expired 2026-09-06/)
  assert.deepEqual(validateFlakeRegistry([row({expires: TODAY})], TODAY, 1), [])
})

test('undated rows and an expiry that does not follow firstSeen are refused', () => {
  assert.match(validateFlakeRegistry([row({firstSeen: 'yesterday'})], TODAY).join('\n'), /firstSeen must be YYYY-MM-DD/)
  assert.match(validateFlakeRegistry([row({expires: undefined})], TODAY).join('\n'), /expires must be YYYY-MM-DD/)
  assert.match(validateFlakeRegistry([row({expires: '2026-08-01', firstSeen: '2026-09-01'})], TODAY).join('\n'),
    /is not after firstSeen/)
})

test('a nameless row, and a duplicate registration, are refused', () => {
  assert.match(validateFlakeRegistry([row({test: '   '})], TODAY).join('\n'), /no exact test identity/)
  assert.match(validateFlakeRegistry([row(), row()], TODAY).join('\n'), /duplicate registration/)
})

test('the registry cannot grow past the ratchet without the count literal moving', () => {
  const four = [1, 2, 3, 4].map(n => row({test: `T${n}`}))
  const problems = validateFlakeRegistry(four, TODAY, 3)
  assert.match(problems.join('\n'), /4 rows but the ratchet allows 3/)
  assert.match(problems.join('\n'), /REGISTERED_FLAKE_COUNT/)
  // Shrinking is never a violation: the count is a ceiling.
  assert.deepEqual(validateFlakeRegistry([row()], TODAY, 3), [])
})

test('owner-requested zero policy rejects even one otherwise valid registration', () => {
  assert.equal(REGISTERED_FLAKE_COUNT, 0)
  assert.deepEqual(validateFlakeRegistry([], TODAY), [])
  assert.match(validateFlakeRegistry([row()], TODAY).join('\n'), /1 rows but the ratchet allows 0/)
})

test('owner-requested zero retry limit refuses a historical one-retry allowance', () => {
  assert.equal(RETRY_LIMIT, 0)
  assert.match(validateFlakeRegistry([row({retryLimit: 3})], TODAY).join('\n'), /retryLimit must be 0/)
  assert.match(validateFlakeRegistry([row({retryLimit: 1})], TODAY).join('\n'), /retryLimit must be 0/)
  const {retryLimit, ...defaulted} = valid
  assert.deepEqual(validateFlakeRegistry([defaulted], TODAY, 1), [])
})

test('the real registry is valid today and within its ratchet', () => {
  const today = new Date().toISOString().slice(0, 10)
  assert.deepEqual(validateFlakeRegistry(real, today), [], `today is ${today}`)
  assert.ok(real.length <= REGISTERED_FLAKE_COUNT)
})

test('all real host registries are empty while the retired test stays in every roster', () => {
  for (const filename of ['host-test-baseline.json', 'host-test-baseline.macos.json', 'host-test-baseline.ubuntu.json']) {
    const host = JSON.parse(readFileSync(path.join(root, 'eng/baselines', filename), 'utf8'))
    assert.deepEqual(host.knownFlaky, [], filename)
    assert.ok(host.knownTests.includes(retired), `${filename}: retired allowance must not remove the test`)
  }
})

test('the production gate planner cannot retry or rescue the retired first-result failure', () => {
  for (const registry of [[], [{test: retired, owner: '348', firstSeen: '2026-09-08', expires: '2026-10-08'}]]) {
    const plan = planFlakeRetries(registry, '2026-10-07', [retired])
    assert.deepEqual(plan.retryable, [])
    assert.equal(plan.flakyLimits.size, 0)
    const counts = {total: 1, passed: 0, failed: 1, notExecuted: 0}
    const result = compareHostBaseline({baseline: {permittedFailures: [], knownTests: [retired], policyRemovals: []},
      counts, adjustedFailed: counts.failed, newFailures: [retired],
      trx: {counts, problems: [], results: [{testName: retired, outcome: 'Failed'}]}})
    assert.equal(result.passed, false)
  }
})
