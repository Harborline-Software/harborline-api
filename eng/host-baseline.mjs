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
  // 119a/119b/119c: a known test that did not run this time (failed, passed, or renamed away) and
  // is not named by a policyRemovals row is a disappearance -- fail and list it by name. A rename is
  // a removal (old name, needs its own policyRemovals row) plus an addition (the new name simply
  // shows up in `ran`, needing no declaration).
  const disappeared = knownTests.filter(name => !ran.has(name) && !removedNames.has(name))
  const problems = [...(trx?.problems ?? [])]
  if (!counts) problems.push('host baseline incomplete: TRX counters unavailable; inspect the host test output')
  else {
    if (!(counts.total > 0)) problems.push('host baseline incomplete: TRX counted no tests; check test discovery')
    // Each theory case contributes one result, even when its DisplayName repeats.
    for (const [label, actual, expected] of [['total', results.length, counts.total], ['failed', failed.length, counts.failed], ['passed', passedResults.length, counts.passed]]) {
      if (actual !== expected) problems.push(`host baseline incomplete: TRX has ${actual} ${label} results but its counter is ${expected}`)
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
  return {passed: problems.length === 0,
    burnDown, disappeared, problems, tail: problems.join('\n'),
    note: 'Identity comparison (T-724 ruling 119): the total is informational. Remove every burn-down row from permittedFailures; a disappeared known test needs a policyRemovals row naming it, or the gate stays red.'}
}
