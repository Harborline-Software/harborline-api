import {readFileSync} from 'node:fs'

export const WINDOWS_BASELINE = 'eng/baselines/host-test-baseline.json'
export const MACOS_BASELINE = 'eng/baselines/host-test-baseline.macos.json'
export const UBUNTU_BASELINE = 'eng/baselines/host-test-baseline.ubuntu.json'

export const hostBaselineFor = (platform = process.platform) =>
  platform === 'darwin' ? MACOS_BASELINE : platform === 'linux' ? UBUNTU_BASELINE : WINDOWS_BASELINE

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
    const results = [...resultBlock[1].matchAll(/<UnitTestResult\b[^>]*>/g)].map(([tag]) => {
      const {testName, outcome} = attributes(tag)
      if (!testName || !['Failed', 'Passed', 'NotExecuted'].includes(outcome)) throw new Error('missing testName or unsupported outcome')
      return {testName, outcome}
    })
    return {counts, results, problems: []}
  } catch (error) {
    return {counts: null, results: [], problems: [`host baseline incomplete: cannot read TRX ${file}: ${error.message}`]}
  }
}

// Read vitest's --reporter=json output as the same {counts, results, problems} shape readHostTrx
// gives the host step, so compareHostBaseline is one function for every baseline (T-724 ruling
// 119d). Identity matches the existing repin-baseline.mjs convention: "<file basename> :: <test
// title>" -- the leaf title, not the full ancestor-describe chain, and not the clone's absolute path.
export function readVitestJsonAsTrx(file) {
  try {
    const report = JSON.parse(readFileSync(file, 'utf8'))
    const results = report.testResults.flatMap(fileResult => {
      const base = fileResult.name.replaceAll('\\', '/').split('/').at(-1)
      return fileResult.assertionResults.map(assertion => {
        const outcome = assertion.status === 'passed' ? 'Passed' : assertion.status === 'failed' ? 'Failed' : 'NotExecuted'
        return {testName: `${base} :: ${assertion.title}`, outcome}
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
  const ran = new Set([...passed, ...failed])
  const burnDown = permitted.filter(name => passed.has(name))
  // Fail-closed (2026-09-27 review): an unpopulated roster (knownTests: [], true of macOS/Ubuntu
  // until their first --write-known-tests run) must not silently drop disappearance coverage below
  // what the old permittedFailures-scoped "missing" check gave every named row. Fall back to
  // checking the permitted rows themselves, so a permitted test silently going NotExecuted is still
  // caught exactly as it always was, independent of the roster below.
  const effectiveKnownTests = knownTests.length > 0 ? knownTests : permitted
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
