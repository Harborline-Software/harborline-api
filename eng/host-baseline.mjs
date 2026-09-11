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

export function compareHostBaseline({baseline, counts, adjustedFailed, newFailures, trx}) {
  if (baseline.comparison !== 'named') {
    // Keep the Windows verdict expression unchanged.
    return {passed: Boolean(counts) && counts.total === baseline.totals.total && adjustedFailed === baseline.totals.failed && newFailures.length === 0}
  }
  const permitted = baseline.permittedFailures.map(row => row.test)
  counts = trx?.counts
  const results = trx?.results ?? []
  const failed = results.filter(row => row.outcome === 'Failed').map(row => row.testName)
  const passedResults = results.filter(row => row.outcome === 'Passed')
  const passed = new Set(passedResults.map(row => row.testName))
  const burnDown = permitted.filter(name => passed.has(name))
  const missing = permitted.filter(name => !failed.includes(name) && !passed.has(name))
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
  for (const name of missing) problems.push(`host baseline missing result: ${name}`)
  return {passed: problems.length === 0,
    burnDown, missing, problems, tail: problems.join('\n'),
    note: 'Named comparison: remove every burn-down row from permittedFailures; missing or unlisted failures are red.'}
}
