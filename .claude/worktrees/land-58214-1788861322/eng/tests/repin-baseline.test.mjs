import {test} from 'node:test'
import assert from 'node:assert/strict'
import {spawnSync} from 'node:child_process'
import {mkdtempSync, writeFileSync, readFileSync, copyFileSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {fileURLToPath} from 'node:url'

// Proof for eng/repin-baseline.mjs (ticket 236 row 4, review rounds 3 and 4): for every log shape the
// persisted tuple equals what the log says, or the script refuses and the baseline is byte-for-byte
// unchanged. The failure-identity allow-list is EXACT: every real knownFlaky[].test identity passes
// through the real dotnet failed-line grammar, and every prefix / suffix / infix near-miss of every
// row is refused.
const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '..', '..')
const script = path.join(root, 'eng', 'repin-baseline.mjs')
const baselineSrc = path.join(root, 'eng', 'baselines', 'host-test-baseline.json')
const baselineText = readFileSync(baselineSrc, 'utf8')
const baselineJson = JSON.parse(baselineText)
const knownRows = (baselineJson.knownFlaky ?? []).map(r => r.test).filter(Boolean)
const cur = baselineJson.totals
const T = cur.total + 1
const S = cur.notExecuted

function run(logText, source = baselineSrc) {
  const dir = mkdtempSync(path.join(tmpdir(), 'repin-'))
  const baseline = path.join(dir, 'baseline.json')
  copyFileSync(source, baseline)
  const beforeText = readFileSync(source, 'utf8')
  const narrative = path.join(dir, 'n.json')
  writeFileSync(narrative, JSON.stringify({recordedAt: '2026-09-03T00:00:00Z', cause: 'test', detectedBy: 'test'}))
  const log = path.join(dir, 'test.log')
  writeFileSync(log, logText)
  const r = spawnSync(process.execPath, [script, baseline, narrative, log], {encoding: 'utf8'})
  const afterText = readFileSync(baseline, 'utf8')
  return {status: r.status, out: r.stdout + r.stderr, unchanged: afterText === beforeText, after: JSON.parse(afterText).totals, afterText}
}
const summary = (f, p, s, t) => `Failed!  - Failed:     ${f}, Passed:  ${p}, Skipped:    ${s}, Total:  ${t}, Duration: 2 m 10 s - Harborline.Api.LocalNodeHost.Tests.dll (net11.0)\n`
const failedLine = name => `  Failed ${name} [348 ms]\n`
const withFailures = names => names.map(failedLine).join('') + summary(names.length, T - S - names.length, S, T)

test('exact tuple: all green', () => {
  const r = run(summary(0, T - S, S, T))
  assert.equal(r.status, 0, r.out)
  assert.deepEqual(r.after, {total: T, passed: T - S, failed: 0, notExecuted: S})
})

test('every registered knownFlaky identity is representable and counts green (exactly)', () => {
  assert.ok(knownRows.length > 0, 'the baseline has no knownFlaky rows to enumerate')
  for (const row of knownRows) {
    const r = run(withFailures([row]))
    assert.equal(r.status, 0, `row "${row}" refused: ${r.out}`)
    assert.deepEqual(r.after, {total: T, passed: T - S, failed: 0, notExecuted: S})
  }
  const all = run(withFailures(knownRows))
  assert.equal(all.status, 0, all.out)
  assert.deepEqual(all.after, {total: T, passed: T - S, failed: 0, notExecuted: S})
})

test('every prefix, suffix and infix near-miss of every knownFlaky identity is refused without a write', () => {
  for (const row of knownRows) {
    const nearMisses = [
      `Evil.${row}`,                          // registered identity as a suffix
      `${row}.Regression`,                    // registered identity as a prefix
      `Harborline.Tests.Evil.${row}.Regression`, // registered identity as an infix
      row.slice(0, -1),                       // truncated
      row.slice(1),                           // leading character dropped
      row.replace(/_/g, ' '),                 // near-identical spelling
    ].filter(x => x !== row)
    for (const miss of nearMisses) {
      const r = run(withFailures([miss]))
      assert.equal(r.status, 1, `near-miss "${miss}" was accepted: ${r.out}`)
      assert.ok(r.unchanged, `near-miss "${miss}" changed the baseline bytes`)
      assert.match(r.out, /not an exact knownFlaky identity/)
    }
  }
})

test('a distant unknown failure is refused without a write', () => {
  const r = run(withFailures(['Harborline.Tests.Real.Regression']))
  assert.equal(r.status, 1, r.out)
  assert.ok(r.unchanged)
})

test('refuses a summary that does not add up', () => {
  const r = run(summary(0, T - S, S + 1, T))
  assert.equal(r.status, 1, r.out)
  assert.ok(r.unchanged)
})

test('refuses a bare summary without the failed names it claims', () => {
  const r = run(summary(1, T - S - 1, S, T))
  assert.equal(r.status, 1, r.out)
  assert.ok(r.unchanged)
})

test('skipped count is persisted as notExecuted, not hard-coded', () => {
  const r = run(summary(0, T - (S + 1), S + 1, T))
  assert.equal(r.status, 0, r.out)
  assert.deepEqual(r.after, {total: T, passed: T - (S + 1), failed: 0, notExecuted: S + 1})
})

test('no-op when the baseline already matches', () => {
  const r = run(summary(0, cur.total - S, S, cur.total))
  assert.equal(r.status, 0, r.out)
  assert.ok(r.unchanged)
  assert.match(r.out, /already at/)
})

const capabilitySrc = path.join(root, 'eng', 'baselines', 'hull-test-baseline.json')
const capabilityText = readFileSync(capabilitySrc, 'utf8')
const capabilityJson = JSON.parse(capabilityText)
const capabilityFailures = capabilityJson.permittedFailures.map(row => row.test)
const capabilityCurrent = capabilityJson.totals
const capabilityTotal = capabilityCurrent.total + 2
const capabilityPending = capabilityCurrent.pending + 1
const capabilityPassed = capabilityTotal - capabilityPending - capabilityCurrent.failed
const vitestFailureLine = identity => {
  const [file, name] = identity.split(' :: ')
  return ` FAIL  src/membrane/${file} > membrane contract > ${name}\n`
}
const vitestLog = (names, {failed = names.length, passed = capabilityPassed, skipped = capabilityPending, total = capabilityTotal} = {}) =>
  names.map(vitestFailureLine).join('') + ` Tests  ${failed} failed | ${passed} passed | ${skipped} skipped (${total})\n`

test('capability accepts unchanged exact failure identities and splices counts', () => {
  const r = run(vitestLog(capabilityFailures), capabilitySrc)
  assert.equal(r.status, 0, r.out)
  assert.deepEqual(r.after, {total: capabilityTotal, passed: capabilityPassed, failed: capabilityCurrent.failed, pending: capabilityPending})
  assert.match(JSON.parse(r.afterText).deltaFromPrevious, new RegExp(`^Previous: ${capabilityCurrent.total}/${capabilityCurrent.passed}/${capabilityCurrent.failed}/${capabilityCurrent.pending}\\. Now: ${capabilityTotal}/${capabilityPassed}/${capabilityCurrent.failed}/${capabilityPending}\\. test `))
})

test('capability refuses a failed-count change without a write', () => {
  const failed = capabilityCurrent.failed + 1
  const passed = capabilityTotal - capabilityPending - failed
  const r = run(vitestLog(capabilityFailures, {failed, passed}), capabilitySrc)
  assert.equal(r.status, 1, r.out)
  assert.ok(r.unchanged)
  assert.match(r.out, new RegExp(`failed count changed ${capabilityCurrent.failed} -> ${failed}`))
})

test('capability refuses a renamed permitted failure and names both sides of the difference', () => {
  // The shipping baseline has burned down to zero; use a nonempty fixture to exercise rename refusal.
  const dir = mkdtempSync(path.join(tmpdir(), 'repin-named-'))
  const source = path.join(dir, 'baseline.json')
  const original = 'known.test.ts :: permitted failure'
  writeFileSync(source, JSON.stringify({...capabilityJson,
    totals: {...capabilityCurrent, failed: 1, passed: capabilityCurrent.passed - 1},
    permittedFailures: [{test: original}]}))
  let r
  try { r = run(vitestLog([original + ' renamed'], {passed: capabilityPassed - 1}), source) }
  finally { rmSync(dir, {recursive: true, force: true}) }
  assert.equal(r.status, 1, r.out)
  assert.ok(r.unchanged)
  const escapeRegex = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  assert.match(r.out, new RegExp(`unexpected: ${escapeRegex(original + ' renamed')}`))
  assert.match(r.out, new RegExp(`missing: ${escapeRegex(original)}`))
})

test('capability output is JSON-valid and byte-for-byte unchanged outside the spliced fields', () => {
  const r = run(vitestLog(capabilityFailures), capabilitySrc)
  assert.equal(r.status, 0, r.out)
  assert.doesNotThrow(() => JSON.parse(r.afterText))
  const delta = capabilityJson.deltaFromPrevious
  const expected = capabilityText
    .replace(`"total": ${capabilityCurrent.total}`, `"total": ${capabilityTotal}`)
    .replace(`"passed": ${capabilityCurrent.passed}`, `"passed": ${capabilityPassed}`)
    .replace(`"pending": ${capabilityCurrent.pending}`, `"pending": ${capabilityPending}`)
    .replace(JSON.stringify(delta), JSON.stringify(`Previous: ${capabilityCurrent.total}/${capabilityCurrent.passed}/${capabilityCurrent.failed}/${capabilityCurrent.pending}. Now: ${capabilityTotal}/${capabilityPassed}/${capabilityCurrent.failed}/${capabilityPending}. test FAILURE IDENTITY IS UNCHANGED: the same permitted failures, carried over verbatim.\n\n${delta}`))
  assert.equal(r.afterText, expected)
})
