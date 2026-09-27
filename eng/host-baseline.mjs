import {readFileSync} from 'node:fs'

export const WINDOWS_BASELINE = 'eng/baselines/host-test-baseline.json'
export const MACOS_BASELINE = 'eng/baselines/host-test-baseline.macos.json'
export const UBUNTU_BASELINE = 'eng/baselines/host-test-baseline.ubuntu.json'

export const hostBaselineFor = (platform = process.platform) =>
  platform === 'darwin' ? MACOS_BASELINE : platform === 'linux' ? UBUNTU_BASELINE : WINDOWS_BASELINE

// The capability suite gates a real chunk of itself on process.platform (cpu-image-floor-smoke.ts,
// sandbox.conformance.test.ts, ...): the SAME total runs on every host (vitest still discovers every
// describe.runIf block either way), but WHICH identities actually execute is genuinely different by
// design, permanently, not a regression -- exactly the reason the host suite already has a baseline
// per OS. Only Windows and macOS run capability-baseline-match today (verify-shared's lane excludes
// exact-clone entirely), so there is no _UBUNTU variant yet.
export const CAPABILITY_WINDOWS_BASELINE = 'eng/baselines/hull-test-baseline.json'
export const CAPABILITY_MACOS_BASELINE = 'eng/baselines/hull-test-baseline.macos.json'
export const capabilityBaselineFor = (platform = process.platform) => platform === 'darwin' ? CAPABILITY_MACOS_BASELINE : CAPABILITY_WINDOWS_BASELINE

// A capability test identity can carry the exact-clone's own path (e.g. the operational-environment
// Python-worker table), and that path is OS-specific in two ways redactEvidence()'s placeholder
// swap does not reach: the surrounding relative segments use the platform's own separator (`\` on
// Windows, `/` elsewhere), and macOS's mkdtempSync path resolves through the /var → /private/var
// symlink partway through the run, so the same clone shows up as .../private<exact-clone>/... in a
// title even though the placeholder swap already fired. Apply this AFTER redactEvidence(), both
// when an identity is compared and when it is written to a committed knownTests roster, so the same
// logical test has the same identity on every host.
export const normalizeIdentity = name => name.replaceAll('\\', '/').replace('/private<exact-clone>', '<exact-clone>')

export function baselineArgument(args) {
  const index = args.indexOf('--host-baseline')
  const baseline = index < 0 ? hostBaselineFor() : args[index + 1]
  if (![WINDOWS_BASELINE, MACOS_BASELINE, UBUNTU_BASELINE].includes(baseline)) throw new Error(`unknown host baseline: ${baseline}`)
  if (index >= 0) args.splice(index, 2)
  return baseline
}

export const resultNamesIn = (text, outcome) => [...text.matchAll(new RegExp(`^ {2}${outcome} (.+?)\\s+\\[[^\\]]*\\]\\s*$`, 'gm'))].map(m => m[1].trim())

// Read the VSTest TRX vocabulary, independently of console verbosity and summary layout.
// Decode once: a literal "&lt;" in a display name is serialized as "&amp;lt;".
export function readHostTrx(file) {
  try {
    const xml = readFileSync(file, 'utf8').replace(/<!--[\s\S]*?-->/g, '')
    const decode = value => value.replace(/&(#x[\da-f]+|#\d+|amp|lt|gt|quot|apos);/gi, (_, entity) => {
      if (entity.startsWith('#')) return String.fromCodePoint(entity[1].toLowerCase() === 'x' ? parseInt(entity.slice(2), 16) : +entity.slice(1))
      return {amp: '&', lt: '<', gt: '>', quot: '"', apos: "'"}[entity]
    })
    const attributes = tag => Object.fromEntries([...tag.matchAll(/([\w]+)\s*=\s*("[^"]*"|'[^']*')/g)]
      .map(([, name, value]) => [name, decode(value.slice(1, -1))]))
    const resultBlock = xml.match(/<Results\b[^>]*>([\s\S]*?)<\/Results>/)
    const summary = xml.match(/<ResultSummary\b[^>]*>([\s\S]*?)<\/ResultSummary>/)
    const counters = [...(summary?.[1] ?? '').matchAll(/<Counters\b[^>]*\/>/g)]
    if (!/<TestRun\b/.test(xml) || !/<\/TestRun>\s*$/.test(xml) || !resultBlock || counters.length !== 1) {
      throw new Error('missing or incomplete TestRun, Results or Counters')
    }
    const values = attributes(counters[0][0])
    const counts = Object.fromEntries(['total', 'passed', 'failed', 'notExecuted'].map(key => {
      if (!/^\d+$/.test(values[key] ?? '') || !Number.isSafeInteger(+values[key])) throw new Error(`invalid ${key} counter`)
      return [key, +values[key]]
    }))
    const raw = [...resultBlock[1].matchAll(/<UnitTestResult\b[^>]*>/g)].map(([tag]) => {
      const {testName, outcome, testId} = attributes(tag)
      if (!testName || !['Failed', 'Passed', 'NotExecuted'].includes(outcome)) throw new Error('missing testName or unsupported outcome')
      return {testName, outcome, testId}
    })
    // CodeRabbit 4113873155 (2026-09-27): a Set of bare testName collapses two theory cases that
    // share a DisplayName -- one can vanish while the other keeps running, and the roster would
    // never notice. Qualify rosterId with VSTest's testId (a deterministic hash of the test's full
    // identity -- method + data row -- stable run over run for the SAME case, distinct for two
    // different cases even when their DisplayName collides) ONLY for names that actually collide in
    // THIS run, so the roster stays a plain, readable testName for the overwhelming common case and
    // only pays a GUID suffix where a real ambiguity exists. testName alone remains the
    // failure-matching key (permittedFailures/knownFlaky/newFailures are unaffected).
    const nameCounts = new Map()
    for (const {testName} of raw) nameCounts.set(testName, (nameCounts.get(testName) ?? 0) + 1)
    const results = raw.map(({testName, outcome, testId}) => ({
      testName, outcome,
      rosterId: nameCounts.get(testName) > 1 && testId ? `${testName} [${testId}]` : testName,
    }))
    return {counts, results, problems: []}
  } catch (error) {
    return {counts: null, results: [], problems: [`host baseline incomplete: cannot read TRX ${file}: ${error.message}`]}
  }
}

// Read vitest's --reporter=json output as the same {counts, results, problems} shape readHostTrx
// gives the host step, so compareHostBaseline is one function for every baseline (T-724 ruling
// 119d). `testName` (used for permittedFailures/newFailures matching) matches the existing
// repin-baseline.mjs convention: "<file basename> :: <test title>" -- the leaf title, not the full
// ancestor-describe chain, and not the clone's absolute path. `rosterId` (used for the knownTests
// roster) additionally folds in the ancestor describe-block chain (CodeRabbit 4113873155): two
// tests can share a basename and leaf title from different describe blocks, and the roster must not
// collapse them the way testName alone would.
export function readVitestJsonAsTrx(file) {
  try {
    const report = JSON.parse(readFileSync(file, 'utf8'))
    const results = report.testResults.flatMap(fileResult => {
      const base = fileResult.name.replaceAll('\\', '/').split('/').at(-1)
      return fileResult.assertionResults.map(assertion => {
        const outcome = assertion.status === 'passed' ? 'Passed' : assertion.status === 'failed' ? 'Failed' : 'NotExecuted'
        const ancestry = (assertion.ancestorTitles ?? []).join(' › ')
        return {
          testName: `${base} :: ${assertion.title}`, outcome,
          rosterId: `${base} :: ${ancestry ? `${ancestry} › ` : ''}${assertion.title}`,
        }
      })
    })
    const counts = {
      total: report.numTotalTests, passed: report.numPassedTests,
      failed: report.numFailedTests, notExecuted: report.numPendingTests + (report.numTodoTests ?? 0),
    }
    return {counts, results, problems: []}
  } catch (error) {
    return {counts: null, results: [], problems: [`capability baseline incomplete: cannot read vitest JSON report ${file}: ${error.message}`]}
  }
}

// T-724 ruling 119 (T-965): identity comparison, used for every baseline (host, per-OS, and
// capability). The TOTAL is informational only -- two sibling PRs that each add tests must be able
// to land in the same merge-queue batch without fighting over one pinned integer. What must hold
// instead: every test identity in the baseline's committed `knownTests` list either ran (passed or
// failed) or is named by a `policyRemovals` row as a deliberate removal/rename; no unlisted test
// fails; and every `permittedFailures` row that stopped failing has its row removed (burn-down).
// A test that moved from run to skipped counts as a disappearance (119c): `ran` below is built ONLY
// from Passed/Failed outcomes, never NotExecuted/Skipped.
export function compareHostBaseline({baseline, counts, adjustedFailed, newFailures, trx}) {
  const permitted = (baseline.permittedFailures ?? []).map(row => row.test)
  const knownTests = baseline.knownTests ?? []
  const removedNames = new Set((baseline.policyRemovals ?? []).map(row => row.test))
  counts = trx?.counts
  const results = trx?.results ?? []
  const failed = results.filter(row => row.outcome === 'Failed').map(row => row.testName)
  const passedResults = results.filter(row => row.outcome === 'Passed')
  const passed = new Set(passedResults.map(row => row.testName))
  const burnDown = permitted.filter(name => passed.has(name))
  // CodeRabbit 4113873155: the knownTests roster is keyed by rosterId (testId-qualified for host,
  // ancestor-chain-qualified for capability), NOT the bare testName burnDown/newFailures use above --
  // two cases can share a testName (a repeated theory DisplayName, a same-title case in a different
  // describe block) and rosterId is what keeps them distinguishable. `rosterId ?? testName` covers
  // synthetic/older trx fixtures that never set it.
  const ranRosterIds = new Set(results
    .filter(row => row.outcome === 'Passed' || row.outcome === 'Failed')
    .map(row => row.rosterId ?? row.testName))
  // Fail-closed (2026-09-27 review): an unpopulated roster (knownTests: [], true of macOS/Ubuntu
  // until their first --write-known-tests run) must not silently drop disappearance coverage below
  // what the old permittedFailures-scoped "missing" check gave every named row. Fall back to
  // checking the permitted rows themselves (by testName, since permittedFailures predates rosterId),
  // so a permitted test silently going NotExecuted is still caught exactly as it always was,
  // independent of the roster below.
  const effectiveKnownTests = knownTests.length > 0 ? knownTests : permitted
  const ran = knownTests.length > 0 ? ranRosterIds : new Set([...passed, ...failed])
  // 119a/119b/119c: a known test that did not run this time (failed, passed, or renamed away) and
  // is not named by a policyRemovals row is a disappearance -- fail and list it by name. A rename is
  // a removal (old name, needs its own policyRemovals row) plus an addition (the new name simply
  // shows up in `ran`, needing no declaration).
  const disappeared = effectiveKnownTests.filter(name => !ran.has(name) && !removedNames.has(name))
  const problems = [...(trx?.problems ?? [])]
  if (!counts) problems.push('host baseline incomplete: TRX counters unavailable; inspect the host test output')
  else {
    if (!(counts.total > 0)) problems.push('host baseline incomplete: TRX counted no tests; check test discovery')
    // Each theory case contributes one result, even when its DisplayName repeats.
    for (const [label, actual, expected] of [['total', results.length, counts.total], ['failed', failed.length, counts.failed], ['passed', passedResults.length, counts.passed]]) {
      if (actual !== expected) problems.push(`host baseline incomplete: TRX has ${actual} ${label} results but its counter is ${expected}`)
    }
  }
  // Fail-closed (2026-09-27 review), continued: with no roster at all, `disappeared` above can only
  // fall back to the permitted rows (there may be none), which is not enough on its own to say
  // protection has not dropped below the pre-119 Windows rule (an exact total match). While
  // knownTests is unpopulated, ALSO require the total to match the pinned figure -- a real
  // regression, so protection is never weaker than either the old identity-scoped check (permitted
  // rows, restored above) or the old exact-count check (Windows). This block never fires once
  // knownTests is populated; the total goes back to purely informational, as ruling 119 intends.
  let rosterUnpopulated
  if (knownTests.length === 0) {
    rosterUnpopulated = 'host baseline knownTests roster is empty: enforcing the exact-total rule as a fallback (T-724 ruling 119) until it is populated by --write-known-tests'
    if (!(counts && counts.total === baseline.totals?.total)) {
      problems.push(`host baseline incomplete: total ${counts?.total ?? 'unknown'} does not match the pinned total ${baseline.totals?.total} while knownTests is unpopulated`)
    }
  }
  const seen = new Set()
  for (const name of permitted) {
    if (seen.has(name)) problems.push(`host baseline duplicate permitted row: remove duplicate row: ${name}`)
    seen.add(name)
  }
  for (const name of newFailures) problems.push(`host baseline unlisted failure: investigate: ${name}`)
  for (const name of burnDown) problems.push(`host baseline burn-down: remove row: ${name}`)
  for (const name of disappeared) problems.push(`host baseline test disappeared: add a policyRemovals row naming it if intentional, otherwise investigate: ${name}`)
  // rosterUnpopulated is visibility, not a verdict: it always prints (run-exact-clone.mjs logs it
  // alongside `problems` on every run, pass or fail) but only the explicit total-mismatch line above
  // -- pushed onto `problems` -- can fail the gate while the roster is empty.
  return {passed: problems.length === 0,
    burnDown, disappeared, problems, tail: problems.join('\n'), rosterUnpopulated: rosterUnpopulated ?? null,
    note: 'Identity comparison (T-724 ruling 119): the total is informational. Remove every burn-down row from permittedFailures; a disappeared known test needs a policyRemovals row naming it, or the gate stays red.'}
}

// CodeRabbit 4113873155 (2026-09-27): refuse a knownTests roster that would itself contain two
// results reduced to the same identity, rather than silently writing a shorter, ambiguous list.
// Takes the RAW per-result identity list (before ranNamesOf's own Set-dedup collapses it), so a
// genuine collision -- rosterId generation itself producing the same value for two different
// results -- is caught before it can hide behind the dedup.
export function rosterIdCollisions(rosterIds) {
  const seen = new Map()
  for (const id of rosterIds) seen.set(id, (seen.get(id) ?? 0) + 1)
  return [...seen].filter(([, count]) => count > 1).map(([id]) => id)
}

// CodeRabbit 4113873156/4113873157 area (2026-09-27): --write-known-tests must not be a laundering
// route around ruling 119a -- overwriting a populated roster with a candidate that silently drops a
// known identity would erase the very disappearance the gate exists to catch, with no policyRemovals
// review. Compares a CANDIDATE roster (what a run just observed) against the CURRENTLY COMMITTED
// roster, exactly as the gate itself would: any committed identity absent from the candidate and not
// named by a policyRemovals row is unexplained, and the write must be refused.
export function unexplainedRosterLoss(committedKnownTests, candidateIdentities, policyRemovals) {
  const observed = new Set(candidateIdentities)
  const removed = new Set((policyRemovals ?? []).map(row => row.test))
  return committedKnownTests.filter(name => !observed.has(name) && !removed.has(name))
}
