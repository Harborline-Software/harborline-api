#!/usr/bin/env node
// repin-baseline.mjs <baseline.json> <narrative.json> <test-log>
//
// Detects the log shape and re-pins either the host baseline from a full `dotnet test` log or the
// capability baseline from a full Vitest log (NOT a bare summary). Host behavior:
//   - parses the summary line  "Failed: F, Passed: P, Skipped: S, Total: T"
//   - parses every failed-test line  "  Failed <identity> [<duration>]"  — the identity is the whole
//     text between "Failed " and the trailing " [duration]", so display names with spaces survive
//   - REFUSES (exit 1, no write) unless every failed identity is EXACTLY EQUAL to a knownFlaky[].test
//     identity of the baseline (no prefix, suffix or substring match), so the persisted tuple is exactly
//     what the log says once the gate's bounded flake retry is accounted for:
//         total = T, notExecuted = S, failed = 0, passed = P + (known-flaky failures) = T - S
//   - refuses on any arithmetic disagreement (P + F + S != T) and when the summary claims failures the
//     log does not name.
// Capability behavior: the failed count and exact `filename :: test` permitted-failure names must
// remain unchanged; only total/passed/pending and the prepended narrative delta are spliced.
// Never a JSON round-trip of the baseline: the splice edits lines so hand-written prose survives.
import {readFileSync, writeFileSync} from 'node:fs'
import {execFileSync} from 'node:child_process'
import {fileURLToPath} from 'node:url'

export const FAILED_LINE = /^\s*Failed\s+(.+?)\s+\[\d+(?:\.\d+)?\s*(?:ms|s|m|h)\]\s*$/

const [baseline, narrative, logPath] = process.argv.slice(2)
if (!baseline || !narrative || !logPath) {
  console.error('usage: repin-baseline.mjs <baseline.json> <narrative.json> <test-log>')
  process.exit(2)
}
const log = readFileSync(logPath, 'utf8')
const dotnetMatch = log.match(/Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)/)
const vitestMatch = log.match(/Tests\s+(?:(\d+)\s+failed\s*\|\s*)?(\d+)\s+passed(?:\s*\|\s*(\d+)\s+skipped)?\s*\((\d+)\)/)
if (!dotnetMatch && !vitestMatch) {
  console.error('refused: no dotnet or vitest test summary line in ' + logPath)
  process.exit(1)
}
if (dotnetMatch && vitestMatch) {
  console.error('refused: log contains both dotnet and vitest summary lines')
  process.exit(1)
}

const baselineText = readFileSync(baseline, 'utf8')
const cur = JSON.parse(baselineText)

if (vitestMatch) {
  const [failed, passed, skipped, total] = [
    Number(vitestMatch[1] ?? 0), Number(vitestMatch[2]), Number(vitestMatch[3] ?? 0), Number(vitestMatch[4]),
  ]
  if (!('pending' in (cur.totals ?? {}))) {
    console.error('refused: vitest log requires a capability baseline with totals.pending')
    process.exit(1)
  }
  if (failed + passed + skipped !== total) {
    console.error(`refused: summary does not add up (${failed}+${passed}+${skipped} != ${total})`)
    process.exit(1)
  }
  if (failed !== cur.totals.failed) {
    console.error(`refused: failed count changed ${cur.totals.failed} -> ${failed}`)
    process.exit(1)
  }

  const failedNames = log.split(/\r?\n/).map(line => {
    const match = line.match(/^\s*FAIL\s{2,}(.+?)\s+>\s+(.+?)\s*$/)
    if (!match) return null
    const titles = match[2].split(/\s+>\s+/)
    return `${match[1].trim().split(/[\\/]/).at(-1)} :: ${titles.at(-1).trim()}`
  }).filter(Boolean)
  if (failedNames.length !== failed) {
    console.error(`refused: summary says ${failed} failed but the log names ${failedNames.length}`)
    process.exit(1)
  }
  const permittedNames = (cur.permittedFailures ?? []).map(row => row.test).filter(Boolean)
  const observed = new Set(failedNames)
  const permitted = new Set(permittedNames)
  const unexpected = [...observed].filter(name => !permitted.has(name))
  const missing = [...permitted].filter(name => !observed.has(name))
  if (unexpected.length || missing.length || observed.size !== failedNames.length || permitted.size !== permittedNames.length) {
    const differences = [
      ...unexpected.map(name => `unexpected: ${name}`),
      ...missing.map(name => `missing: ${name}`),
    ]
    if (observed.size !== failedNames.length) differences.push('observed failure names contain duplicates')
    if (permitted.size !== permittedNames.length) differences.push('permitted failure names contain duplicates')
    console.error('refused: permitted-failure identities changed:\n  ' + differences.join('\n  '))
    process.exit(1)
  }

  const previous = cur.totals
  if (previous.total === total && previous.passed === passed && previous.pending === skipped) {
    console.log(`capability baseline already at ${total}/${passed}/${failed}/${skipped}`)
    process.exit(0)
  }
  const narrativeJson = JSON.parse(readFileSync(narrative, 'utf8'))
  if (typeof narrativeJson.cause !== 'string' || !narrativeJson.cause.trim()) {
    console.error('refused: narrative file must contain a non-empty cause')
    process.exit(1)
  }
  const totalsStart = baselineText.indexOf('"totals"')
  const totalsEnd = baselineText.indexOf('}', totalsStart)
  if (totalsStart < 0 || totalsEnd < 0) throw new Error('totals block not found')
  let totalsText = baselineText.slice(totalsStart, totalsEnd + 1)
  const replaceCount = (key, before, after) => {
    const pattern = new RegExp(`("${key}"\\s*:\\s*)${before}(?=\\s*[,}])`)
    if (!pattern.test(totalsText)) throw new Error(`${key} did not match previous total ${before}`)
    totalsText = totalsText.replace(pattern, `$1${after}`)
  }
  replaceCount('total', previous.total, total)
  replaceCount('passed', previous.passed, passed)
  replaceCount('pending', previous.pending, skipped)
  let next = baselineText.slice(0, totalsStart) + totalsText + baselineText.slice(totalsEnd + 1)
  const deltaPattern = /("deltaFromPrevious"\s*:\s*)("(?:\\.|[^"\\])*")/
  const deltaMatch = next.match(deltaPattern)
  if (!deltaMatch) throw new Error('deltaFromPrevious string not found')
  const previousTuple = `${previous.total}/${previous.passed}/${previous.failed}/${previous.pending}`
  const nowTuple = `${total}/${passed}/${failed}/${skipped}`
  const note = `Previous: ${previousTuple}. Now: ${nowTuple}. ${narrativeJson.cause} FAILURE IDENTITY IS UNCHANGED: the same permitted failures, carried over verbatim.`
  next = next.replace(deltaPattern, `$1${JSON.stringify(note + '\n\n' + JSON.parse(deltaMatch[2]))}`)
  JSON.parse(next)
  writeFileSync(baseline, next)
  console.log(`capability baseline ${previousTuple} -> ${nowTuple}`)
  process.exit(0)
}

const m = dotnetMatch
const [failed, passed, skipped, total] = m.slice(1).map(Number)
if (failed + passed + skipped !== total) {
  console.error(`refused: summary does not add up (${failed}+${passed}+${skipped} != ${total})`)
  process.exit(1)
}
const failedNames = log.split(/\r?\n/).map(l => l.match(FAILED_LINE)?.[1]).filter(Boolean)
if (failedNames.length !== failed) {
  console.error(`refused: summary says ${failed} failed but the log names ${failedNames.length}`)
  process.exit(1)
}
if (!('notExecuted' in (cur.totals ?? {}))) {
  console.error('refused: dotnet log requires a host baseline with totals.notExecuted')
  process.exit(1)
}
const known = new Set((cur.knownFlaky ?? []).map(r => r.test).filter(Boolean))
const unknown = failedNames.filter(n => !known.has(n))
if (unknown.length) {
  console.error('refused: failures that are not an exact knownFlaky identity:\n  ' + unknown.join('\n  '))
  process.exit(1)
}
const newTotal = total, notExecuted = skipped, newPassed = total - skipped
const prevTotal = cur.totals.total, prevPassed = cur.totals.passed, prevNotExecuted = cur.totals.notExecuted
if (prevTotal === newTotal && prevPassed === newPassed && prevNotExecuted === notExecuted) {
  console.log(`baseline already at ${newTotal}/${newPassed} (notExecuted ${notExecuted})`)
  process.exit(0)
}
console.log(`repin ${prevTotal}/${prevPassed}/${prevNotExecuted} -> ${newTotal}/${newPassed}/${notExecuted}`
  + (failed ? ` (${failed} known-flaky failure(s) counted green: ${failedNames.join(' | ')})` : ''))
execFileSync(process.execPath, [
  fileURLToPath(new URL('./splice-generic.js', import.meta.url)),
  baseline, String(prevTotal), String(prevPassed), String(prevNotExecuted),
  String(newTotal), String(newPassed), String(notExecuted), narrative,
], {stdio: 'inherit'})
