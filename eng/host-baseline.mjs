export const WINDOWS_BASELINE = 'eng/baselines/host-test-baseline.json'
export const MACOS_BASELINE = 'eng/baselines/host-test-baseline.macos.json'
export const MACOS_LANDING_REFUSAL = 'macOS host baseline receipts are slice-only; landings require the Windows host baseline (ticket 324)'

export const hostBaselineFor = (platform = process.platform) => platform === 'darwin' ? MACOS_BASELINE : WINDOWS_BASELINE

export function baselineArgument(args) {
  const index = args.indexOf('--host-baseline')
  const baseline = index < 0 ? hostBaselineFor() : args[index + 1]
  if (![WINDOWS_BASELINE, MACOS_BASELINE].includes(baseline)) throw new Error(`unknown host baseline: ${baseline}`)
  if (index >= 0) args.splice(index, 2)
  return baseline
}

export function receiptBaselineProblem(receipt, slice = false) {
  if (receipt.hostBaseline === MACOS_BASELINE && !slice) return MACOS_LANDING_REFUSAL
  if (![WINDOWS_BASELINE, MACOS_BASELINE].includes(receipt.hostBaseline)) return 'receipt does not name a recognized host baseline; rerun verification'
  return null
}

export const resultNamesIn = (text, outcome) => [...text.matchAll(new RegExp(`^ {2}${outcome} (.+?)\\s+\\[[^\\]]*\\]\\s*$`, 'gm'))].map(m => m[1].trim())

export function compareHostBaseline({baseline, counts, adjustedFailed, newFailures, output}) {
  if (baseline.comparison !== 'named') {
    // Keep the Windows verdict expression unchanged.
    return {passed: Boolean(counts) && counts.total === baseline.totals.total && adjustedFailed === baseline.totals.failed && newFailures.length === 0}
  }
  const permitted = baseline.permittedFailures.map(row => row.test)
  const failed = resultNamesIn(output, 'Failed')
  const passed = new Set(resultNamesIn(output, 'Passed'))
  const skipped = resultNamesIn(output, 'Skipped')
  const burnDown = permitted.filter(name => passed.has(name))
  const missing = permitted.filter(name => !failed.includes(name) && !passed.has(name))
  const problems = []
  if (!counts) problems.push('host baseline incomplete: runner summary not parsed; inspect the host test output')
  else {
    if (!(counts.total > 0)) problems.push('host baseline incomplete: runner counted no tests; check test discovery')
    // Each theory case contributes one result, even when its DisplayName repeats.
    if (failed.length !== counts.failed) problems.push(`host baseline incomplete: parsed ${failed.length} failed result lines but the runner counted ${counts.failed}`)
  }
  if (failed.length + passed.size + skipped.length === 0) {
    problems.push('host baseline incomplete: no per-test result lines parsed; rerun with --logger "console;verbosity=normal"')
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
