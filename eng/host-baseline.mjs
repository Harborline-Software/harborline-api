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
  const burnDown = permitted.filter(name => passed.has(name))
  const missing = permitted.filter(name => !failed.includes(name) && !passed.has(name))
  const complete = Boolean(counts) && counts.total > 0 && failed.length === counts.failed
    && new Set(failed).size === failed.length && new Set(permitted).size === permitted.length
  return {passed: complete && newFailures.length === 0 && burnDown.length === 0 && missing.length === 0,
    burnDown, missing,
    note: 'Named comparison: remove every burn-down row from permittedFailures; missing or unlisted failures are red.'}
}
