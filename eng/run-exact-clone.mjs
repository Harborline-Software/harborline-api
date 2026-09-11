#!/usr/bin/env node
// Destination-only exact-clone gate for harborline-api.
//
// What it proves that an in-place build cannot: the committed tree alone is sufficient. A working
// tree accumulates untracked state — restored packages, obj/ and bin/ output, node_modules,
// generated files — and any of it can silently satisfy a dependency the commit does not actually
// carry. This clones HEAD into a scratch directory and builds there, so anything missing from the
// commit fails loudly instead of being supplied by the developer's machine.
//
// It is the API-side counterpart of run-platform-exact-clone.mjs, and it checks BOTH lanes,
// because the .NET closure and the capability TypeScript membrane now live in the same repository and a
// clone that builds one but not the other is not a working clone.
//
// Usage: node tooling/run-api-exact-clone.mjs [--record]
import {execFileSync, spawnSync} from 'node:child_process'
import {mkdtempSync, mkdirSync, readdirSync, rmSync, writeFileSync, readFileSync, existsSync} from 'node:fs'
import {evidenceTarget} from './exact-clone-evidence.mjs'
import {validateFlakeRegistry, RETRY_LIMIT} from './flake-registry.mjs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {resolveCommand} from './lib/resolve-command.mjs'
import {baselineArgument, compareHostBaseline, resultNamesIn, readHostTrx} from './host-baseline.mjs'
import {copyCoberturaReport, coverageEnabled, qualityCoveragePaths} from './coverage.mjs'

// Vendored from harborline-migration tooling/run-api-exact-clone.mjs (2026-08-20). This was the
// ONLY clean-clone proof harborline-api had, and it lived in a repo with no remote that is being
// deleted. apiRoot and the baselines now resolve inside this repository; the blob-pin below
// therefore pins against this repo's HEAD instead of migration's, which is simpler and correct.
const apiRoot = path.resolve(import.meta.dirname, '..')
const record = process.argv.includes('--record')
const evidencePath = path.join(apiRoot, 'docs/evidence/exact-clone.json')
const collectCoverage = coverageEnabled()
const coveragePaths = qualityCoveragePaths(apiRoot)

const head = execFileSync('git', ['-C', apiRoot, 'rev-parse', 'HEAD'], {encoding: 'utf8'}).trim()
const dirty = execFileSync('git', ['-C', apiRoot, 'status', '--porcelain'], {encoding: 'utf8'}).trim()
if (dirty) throw new Error(`harborline-api has uncommitted changes; the clone would not represent them:\n${dirty}`)

// Baselines the clone must reproduce. Pinned by NAME in the ticket artifacts, so a different
// failure set is a gate failure even when the counts happen to match.
//
// TAMPER-EVIDENCE. The baselines are this gate's authority, which makes "edit the baseline until
// the gate passes" the same loophole as gating on an exit code, only better disguised. So each
// baseline is cited by its COMMITTED git blob hash, and the working copy must match that blob: a
// path citation floats with the working tree and proves nothing. An uncommitted baseline edit
// stops the gate rather than silently redefining what passing means. Changing a baseline is a
// deliberate, reviewable commit and a stop-and-report event under the unexpected-delta rule.
const BASELINES = {
  host: baselineArgument(process.argv.slice(2)),
  capability: 'eng/baselines/hull-test-baseline.json',
}
const baselineProvenance = {}
for (const [name, relative] of Object.entries(BASELINES)) {
  const committedBlob = execFileSync('git', ['-C', apiRoot, 'rev-parse', `HEAD:${relative}`], {encoding: 'utf8'}).trim()
  const workingBlob = execFileSync('git', ['-C', apiRoot, 'hash-object', relative], {encoding: 'utf8'}).trim()
  baselineProvenance[name] = {path: relative, committedBlob, workingBlob, matchesCommitted: committedBlob === workingBlob}
  if (committedBlob !== workingBlob) {
    throw new Error(
      `${relative} differs from its committed blob (${committedBlob.slice(0, 12)} vs ${workingBlob.slice(0, 12)}). `
      + 'The baseline is this gate\'s authority, so an uncommitted edit to it would silently redefine passing. '
      + 'Commit the change deliberately and report it under the unexpected-delta rule, then re-run.')
  }
}
const hostBaseline = JSON.parse(readFileSync(path.join(apiRoot, BASELINES.host), 'utf8'))
const capabilityBaseline = JSON.parse(readFileSync(path.join(apiRoot, BASELINES.capability), 'utf8'))

const scratch = mkdtempSync(path.join(tmpdir(), 'harborline-api-exact-clone-'))
const clone = path.join(scratch, 'clone')
let retainScratch = false
const steps = []
// The ESC byte is part of the pattern; stripping only the bracket sequence would leave a stray
// ESC that \s+ cannot match, so a colourised summary would fail to parse for a reason invisible
// in the printed output. Built via fromCharCode so this file carries no literal control byte.
const ANSI = new RegExp(`${String.fromCharCode(27)}\\[[0-9;]*m`, 'g')
const stripAnsi = text => text.replace(ANSI, '')
const redactEvidence = text => {
  let redacted = stripAnsi(text)
  for (const [value, replacement] of [[clone, '<exact-clone>'], [scratch, '<exact-clone-root>']]) {
    redacted = redacted.replaceAll(value, replacement).replaceAll(value.replaceAll('\\', '/'), replacement)
  }
  return redacted
}

// `expectNonZero` marks a step whose exit code is NOT the verdict. Both test suites exit non-zero
// by design because each carries permitted, name-pinned failures; gating on the exit code would
// make this gate unpassable while the baselines are honest. For those steps the baseline
// comparison below is the authority, and the exit code is recorded for the record only.
const run = (id, command, args, cwd, {expectNonZero = false} = {}) => {
  const started = Date.now()
  const resolved = resolveCommand(command, args)
  const result = spawnSync(resolved.executable, resolved.args, {cwd, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024})
  const rawOutput = `${result.stdout ?? ''}${result.stderr ?? ''}`
  const output = stripAnsi(rawOutput)
  const step = {
    id,
    passed: expectNonZero ? true : result.status === 0,
    verdictFrom: expectNonZero ? 'baseline comparison, not exit code' : 'exit code',
    exitCode: result.status,
    durationMs: Date.now() - started,
    tail: redactEvidence(output).trimEnd().split('\n').slice(-14).join('\n'),
  }
  // Parse the FULL output, never the tail. Parsing a truncated view made the result depend on how
  // many lines a runner happened to print after its summary: host-baseline-match passed one run
  // and failed the next on identical trees. The tail exists for human reading only.
  step.fullOutput = output
  if (id === 'dotnet-host-tests') step.rawOutput = rawOutput
  steps.push(step)
  return step
}

let report
try {
  if (collectCoverage) {
    rmSync(path.join(apiRoot, 'artifacts', 'quality', 'coverage'), {recursive: true, force: true})
    for (const report of Object.values(coveragePaths)) rmSync(report, {force: true})
  }
  execFileSync('git', ['clone', '--quiet', '--no-hardlinks', apiRoot, clone], {stdio: 'ignore'})

  // Sanity: the clone must carry no build or dependency artifacts. If it does, the .gitignore is
  // wrong and this gate would be testing the same ambient state it exists to exclude.
  const tracked = execFileSync('git', ['-C', clone, 'ls-files'], {encoding: 'utf8', maxBuffer: 1e9}).split('\n')
  const artifacts = tracked.filter(file => /(^|\/)(node_modules|obj|bin)\//.test(file))
  steps.push({id: 'clone-carries-no-artifacts', passed: artifacts.length === 0, artifactCount: artifacts.length, sample: artifacts.slice(0, 5)})

  run('platform-feed', process.execPath, ['eng/exact-clone-platform-feed.mjs', apiRoot, scratch], clone)
  run('dotnet-restore', 'dotnet', ['restore', 'Harborline.Api.slnx', '-nodeReuse:false', '-maxcpucount:6'], clone)
  // Ticket 340: on landing, the clean-clone build is also the Roslyn analysis
  // invocation. Directory.Build.targets expands the project name per compiler
  // invocation, so the single solution build cannot overwrite one global log.
  const qualityDirectory = path.join(apiRoot, 'artifacts', 'quality')
  const roslynDirectory = path.join(qualityDirectory, 'roslyn')
  const eslintDirectory = path.join(qualityDirectory, 'eslint')
  const qualityEnabled = process.env.HARBORLINE_GATE_QUALITY === '1'
  const buildArgs = ['build', 'Harborline.Api.slnx', '-c', 'Release', '--nologo', '--no-restore', '-nodeReuse:false', '-maxcpucount:6']
  if (qualityEnabled) {
    rmSync(roslynDirectory, {recursive: true, force: true})
    rmSync(eslintDirectory, {recursive: true, force: true})
    mkdirSync(roslynDirectory, {recursive: true})
    mkdirSync(eslintDirectory, {recursive: true})
    buildArgs.push(`-p:HarborlineRoslynSarifDirectory=${roslynDirectory}`)
  }
  run('dotnet-build', 'dotnet', buildArgs, clone)
  if (qualityEnabled) {
    const roslynSarifFiles = readdirSync(roslynDirectory)
      .filter(file => /\.sarif$/i.test(file)).sort().map(file => path.join(roslynDirectory, file))
    run('roslyn-sarif-normalize', process.execPath,
      ['eng/normalize-roslyn-sarif.mjs', '--repo-root', clone, ...roslynSarifFiles], clone)
    let roslynSarifCheck = {passed: false, reason: 'not written'}
    try {
      const sarifs = roslynSarifFiles.map(file => JSON.parse(readFileSync(file, 'utf8')))
      const results = sarifs.flatMap(sarif => sarif.runs?.flatMap(run => run.results ?? []) ?? [])
      const hasResultShape = results.every(result => typeof result.ruleId === 'string'
        && /^[^/:]+(?:\/[^/:]+)*$/.test(result.locations?.[0]?.physicalLocation?.artifactLocation?.uri ?? '')
        && Number.isInteger(result.locations?.[0]?.physicalLocation?.region?.startLine)
        && result.partialFingerprints && typeof result.partialFingerprints === 'object')
      roslynSarifCheck = {
        passed: roslynSarifFiles.length > 0
          && sarifs.every(sarif => sarif.version === '2.1.0'
            && typeof sarif.runs?.[0]?.tool?.driver?.name === 'string')
          && hasResultShape,
        resultCount: results.length,
        sarifCount: roslynSarifFiles.length,
        driver: sarifs[0]?.runs?.[0]?.tool?.driver?.name,
      }
    } catch (error) {
      roslynSarifCheck = {passed: false, reason: String(error)}
    }
    steps.push({id: 'roslyn-sarif', ...roslynSarifCheck})
  }

  // The capability lane's dependencies are installed BEFORE the host tests, not after. The two lanes are
  // not independent in one direction: CapabilityInvokeCorrelationTraceTests is a .NET test that spawns
  // apps/capability-host/node_modules/.bin/vitest and drives the TypeScript loopback transport against the
  // live host, so it needs the capability install to have happened. Ordering the install after the host
  // tests made that test fail in the clone for a reason that was purely about step order — it
  // passes in any developer tree, where node_modules already exists — and the failure was carried
  // as permitted debt in host-test-baseline.json rather than being a real finding.
  //
  // contracts must be built first: its package.json points main/types at dist/, which no clone
  // carries — the exact class of gap this gate exists to surface. Moving these three steps earlier
  // does not weaken that; they still install and build from the clone alone.
  run('capability-contracts-install', 'pnpm', ['install', '--frozen-lockfile'], path.join(clone, 'packages/contracts'))
  if (qualityEnabled) {
    const eslintSarif = path.join(eslintDirectory, 'contracts.sarif')
    // Preview findings deliberately leave ESLint with its normal nonzero result;
    // SARIF is the quality engine's evidence and decides whether they block.
    run('contracts-eslint', 'pnpm', ['exec', 'eslint', 'src', '--format', '@microsoft/eslint-formatter-sarif', '--output-file', eslintSarif],
      path.join(clone, 'packages/contracts'), {expectNonZero: true})
    run('eslint-sarif-normalize', process.execPath,
      ['eng/normalize-eslint-sarif.mjs', '--repo-root', clone, 'contracts', eslintSarif], clone)
    let eslintSarifCheck = {passed: false, reason: 'not written'}
    try {
      const sarif = JSON.parse(readFileSync(eslintSarif, 'utf8'))
      const results = sarif.runs?.flatMap(run => run.results ?? []) ?? []
      const hasResultShape = results.every(result => typeof result.ruleId === 'string'
        && /^[^/:]+(?:\/[^/:]+)*$/.test(result.locations?.[0]?.physicalLocation?.artifactLocation?.uri ?? '')
        && Number.isInteger(result.locations?.[0]?.physicalLocation?.region?.startLine)
        && result.partialFingerprints && typeof result.partialFingerprints === 'object')
      eslintSarifCheck = {
        passed: sarif.version === '2.1.0'
          && sarif.runs?.every(run => run.tool?.driver?.name === 'eslint')
          && hasResultShape,
        resultCount: results.length,
        sarifCount: 1,
        driver: sarif.runs?.[0]?.tool?.driver?.name,
      }
    } catch (error) {
      eslintSarifCheck = {passed: false, reason: String(error)}
    }
    steps.push({id: 'eslint-sarif', ...eslintSarifCheck})
  }
  run('capability-contracts-build', 'pnpm', ['run', 'build'], path.join(clone, 'packages/contracts'))
  // 246: the contracts package carries its own tests, including the package-name fence; a clone that
  // only builds it would let a retired-name regression through this route.
  const contractsDirectory = path.join(clone, 'packages/contracts')
  const contractsCoverageDirectory = path.join(contractsDirectory, 'coverage')
  run('capability-contracts-tests', 'pnpm', collectCoverage
    ? ['run', 'test:coverage']
    : ['test'], contractsDirectory)
  if (collectCoverage) {
    copyCoberturaReport({
      resultsDirectory: contractsCoverageDirectory,
      target: coveragePaths.contracts,
      label: 'contracts',
      sourceRoot: 'packages/contracts',
    })
  }
  run('capability-install', 'npm', ['install', '--no-audit', '--no-fund'], path.join(clone, 'apps/capability-host'))

  const hostResultsDirectory = collectCoverage
    ? path.join(apiRoot, 'artifacts', 'quality', 'coverage', 'host')
    : path.join(clone, 'TestResults', 'host')
  const hostTests = run('dotnet-host-tests', 'dotnet',
    ['test', 'apps/local-node-host/tests/tests.csproj', '-c', 'Release', '--nologo', '--no-build', '-nodeReuse:false', '-maxcpucount:6',
      '--logger', 'trx;LogFileName=host-tests.trx', '--results-directory', hostResultsDirectory,
      ...(collectCoverage ? ['--settings', 'eng/coverage.runsettings', '--collect:XPlat Code Coverage'] : [])], clone, {expectNonZero: true})
  run('analyzer-canary', 'bash', ['eng/verify-analyzer-canary.sh'], clone)
  if (collectCoverage) {
    copyCoberturaReport({resultsDirectory: hostResultsDirectory, target: coveragePaths.host,
      label: 'unit-tests', sourceRoot: '.'})
  }
  run('boundary-check', 'bash', ['eng/verify-boundaries.sh'], clone)

  run('capability-typecheck', 'npx', ['tsc', '-p', 'tsconfig.json', '--noEmit'], path.join(clone, 'apps/capability-host'))
  const capabilityTests = run('capability-tests', 'npx', ['vitest', 'run'], path.join(clone, 'apps/capability-host'), {expectNonZero: true})

  const countsOf = text => {
    const dotnet = text.match(/Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)/)
    if (dotnet) return {failed: +dotnet[1], passed: +dotnet[2], skipped: +dotnet[3], total: +dotnet[4]}
    const vitest = text.match(/Tests\s+(?:(\d+)\s+failed\s*\|\s*)?(\d+)\s+passed(?:\s*\|\s*(\d+)\s+skipped)?\s*\((\d+)\)/)
    if (vitest) return {failed: +(vitest[1] ?? 0), passed: +vitest[2], skipped: +(vitest[3] ?? 0), total: +vitest[4]}
    return null
  }
  const hostTrx = hostBaseline.comparison === 'named' ? readHostTrx(path.join(hostResultsDirectory, 'host-tests.trx')) : null
  const hostCounts = hostBaseline.comparison === 'named' ? hostTrx.counts : countsOf(hostTests.fullOutput)
  const capabilityCounts = countsOf(capabilityTests.fullOutput)

  // Bounded retry for the named flaky tests, exactly as host-test-baseline.json's knownFlaky
  // entries prescribe, and ticket 284 caps at ONE identical retry: a second red is the gate's verdict.
  //
  // Why this is not leniency. A test pinned as a permitted failure is masked forever, and a test
  // left out entirely reddens the gate on its own schedule; the retry is the only option that
  // discriminates the two cases, because a genuine race clears on one identical retry while a
  // real regression stays red through it. The entries were written with that reasoning and with
  // a retryLimit, and the gate simply had not implemented it.
  //
  // Four properties keep it honest:
  //   1. Retries happen ONLY when every unexpected failure is a knownFlaky NAME. One unexpected
  //      failure that is not on that list means no retries at all and the gate fails, which is the
  //      regression case.
  //   2. A retry whose filter matches no test is NOT counted as green. A rename that silently
  //      stops targeting the test must fail the gate, not quietly satisfy it.
  //   3. Attempts-to-green is recorded per test. The knownFlaky entry says a change in that number
  //      is itself signal, so discarding it would throw away the measurement the retry produces.
  //   4. Every row is validated first (owner, dates, expiry, ratchet); an invalid registry rescues
  //      nothing, so a stale registration cannot keep buying retries.
  // The trailing bracket is the duration, and it is NOT always numeric — vstest prints "[< 1 ms]"
  // for a fast test, so anchoring on a digit silently drops those rows. Match the bracket itself.
  const failedNamesIn = text => resultNamesIn(text, 'Failed')
  const permittedNames = new Set((hostBaseline.permittedFailures ?? []).map(row => row.test))
  // Ticket 284: the registry is validated BEFORE it is used to rescue anything. An unowned or
  // expired row cannot buy a retry, because the row is what makes the retry legitimate.
  const registryProblems = validateFlakeRegistry(hostBaseline.knownFlaky ?? [], new Date().toISOString().slice(0, 10))
  steps.push({
    id: 'flake-registry-valid',
    passed: registryProblems.length === 0,
    problems: registryProblems,
    registered: (hostBaseline.knownFlaky ?? []).map(row => ({test: row.test, owner: row.owner, firstSeen: row.firstSeen, expires: row.expires})),
    note: 'Every knownFlaky row is exact, owned, dated and unexpired, and the registry is within its ratchet (eng/flake-registry.mjs).',
  })
  const flakyLimits = registryProblems.length === 0
    ? new Map((hostBaseline.knownFlaky ?? []).map(row => [row.test, RETRY_LIMIT]))
    : new Map()

  const observedFailures = hostBaseline.comparison === 'named'
    ? hostTrx.results.filter(row => row.outcome === 'Failed').map(row => row.testName)
    : failedNamesIn(hostTests.fullOutput)
  const unexpected = observedFailures.filter(name => !permittedNames.has(name))
  const retryable = unexpected.every(name => flakyLimits.has(name)) ? unexpected : []
  const retries = []
  for (const name of retryable) {
    const limit = flakyLimits.get(name)
    // vstest filter values need these escaped; spawnSync passes the value as one argv element, so
    // shell quoting is not in play, only the filter grammar.
    const escaped = name.replace(/([\\()&|=!~])/g, '\\$1')
    const filter = /^[A-Za-z0-9_.]+$/.test(name) ? `FullyQualifiedName=${escaped}` : `DisplayName~${escaped}`
    // Both outcomes are recorded: the suite run that failed, and the single retry. A retry that is
    // only reported as a final verdict hides the thing the retry exists to measure.
    const record = {test: name, filter, attempts: 0, targeted: false, green: false,
      outcomes: [{attempt: 0, source: 'host suite', green: false}]}
    for (let attempt = 1; attempt <= limit; attempt++) {
      const resolved = resolveCommand('dotnet',
        ['test', 'apps/local-node-host/tests/tests.csproj', '-c', 'Release', '--nologo', '--no-build', '-nodeReuse:false', '-maxcpucount:6', '--filter', filter])
      const result = spawnSync(resolved.executable, resolved.args, {cwd: clone, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024})
      const output = stripAnsi(`${result.stdout ?? ''}${result.stderr ?? ''}`)
      record.attempts = attempt
      if (/No test matches the given testcase filter/.test(output)) {
        record.note = 'filter matched no test; not counted as green'
        record.outcomes.push({attempt, source: 'retry', green: false, note: 'filter matched no test'})
        break
      }
      record.targeted = true
      const counts = countsOf(output)
      const green = Boolean(counts && counts.total > 0 && counts.failed === 0)
      record.outcomes.push({attempt, source: 'retry', green, counts})
      if (green) {
        record.green = true
        break
      }
    }
    retries.push(record)
  }

  const rescued = retries.filter(row => row.green).length
  const adjustedFailed = hostCounts ? hostCounts.failed - rescued : null
  // R-0006 lesson 1 (ticket 242): KNOWN vs NEW from the EFFECTIVE post-retry sets, and the verdict
  // consumes the same sets. known = permitted names observed failing + knownFlaky names that went green
  // on retry; NEW = every other observed failure (a knownFlaky that never went green is NEW too).
  const rescuedNames = new Set(retries.filter(row => row.green).map(row => row.test))
  const permittedObserved = observedFailures.filter(name => permittedNames.has(name))
  const newFailures = observedFailures.filter(name => !permittedNames.has(name) && !rescuedNames.has(name))
  console.log(`host-suite failures — known: permitted ${permittedObserved.length}`
    + (permittedObserved.length ? ` [${permittedObserved.join(', ')}]` : '')
    + `, known-flaky rescued ${rescuedNames.size}` + (rescuedNames.size ? ` [${[...rescuedNames].join(', ')}]` : '')
    + ` | NEW: ${newFailures.length ? newFailures.join(', ') : 'none'}`)

  steps.push({
    id: 'host-flake-retry',
    passed: true,
    verdictFrom: 'informational; the verdict is host-baseline-match below',
    unexpectedFailures: unexpected,
    retriedUnderKnownFlaky: retryable,
    retries,
    rescued,
    note: retryable.length === 0 && unexpected.length > 0
      ? 'An unexpected failure is NOT on the knownFlaky list, so nothing was retried and the gate fails on identity.'
      : 'Attempts-to-green is the measurement this step exists to produce; a change in it is signal.',
  })

  const hostComparison = compareHostBaseline({baseline: hostBaseline, counts: hostCounts,
    adjustedFailed, newFailures, trx: hostTrx})
  if (hostBaseline.comparison === 'named' && !hostComparison.passed) {
    // Keep the clone on a named failure so the operator can inspect both TRX and raw output.
    retainScratch = true
    mkdirSync(hostResultsDirectory, {recursive: true})
    const outputFile = path.join(hostResultsDirectory, 'host-tests-output.txt')
    writeFileSync(outputFile, hostTests.rawOutput)
    hostComparison.problems = hostComparison.problems.map(line => `${line}; host output: ${outputFile}`)
    hostComparison.tail = hostComparison.problems.join('\n')
  }
  for (const line of hostComparison.problems ?? []) console.log(line)
  steps.push({
    id: 'host-baseline-match',
    ...hostComparison,
    baseline: BASELINES.host,
    expected: hostBaseline.totals, observed: hostCounts,
    newFailures,
    observedAfterFlakeRetry: adjustedFailed === null ? null : {...hostCounts, failed: adjustedFailed},
    rescuedByRetry: rescued,
    note: hostComparison.note ?? 'Counts AND failure identity (newFailures must be empty), after the bounded knownFlaky retry. Failure IDENTITY is pinned by name in host-test-baseline.json and must be reviewed on any change.',
  })
  steps.push({
    id: 'capability-baseline-match',
    passed: Boolean(capabilityCounts) && capabilityCounts.total === capabilityBaseline.totals.total && capabilityCounts.failed === capabilityBaseline.totals.failed,
    expected: capabilityBaseline.totals, observed: capabilityCounts,
    note: 'Counts only, same as the host step. Failure IDENTITY is pinned by name in hull-test-baseline.json.',
  })

  const passed = steps.every(step => step.passed !== false)
  report = {
    schemaVersion: 1, repository: 'harborline-api', gate: 'destination-exact-clone',
    baselineProvenance,
    status: passed ? 'PASS' : 'FAIL', apiCommit: head,
    recordedAt: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'),
    steps,
  }
} finally {
  if (!retainScratch) rmSync(scratch, {recursive: true, force: true})
}

// Record the report whatever its status. A gate that discards its own evidence on failure forces
// a full re-run to learn why it failed — the same evidence-destruction pattern as piping a test
// run through `tail`. The report carries its own status; consumers check that, not the file's
// existence. This matches run-platform-exact-clone.mjs, which records its failures too.
// `persisted` drops each step's fullOutput, which is a working value for the baseline comparisons
// above and not evidence. Writing `report` here instead was a defect: it persisted every runner's
// full console output, and on a Windows host that output carries the scratch clone path, which
// sits under the per-user temp directory and so trips
// committed-control-plane-has-no-personal-path. It never fired on macOS, whose mkdtemp returns a
// path under /var/folders instead. Each step still keeps its 14-line `tail`, so a failure remains
// readable without a re-run.
//
// Note for the next editor: do not spell that home-directory prefix out here. This comment is
// itself scanned, and naming the pattern literally fails the very check it describes.
const persisted = {...report, steps: report.steps.map(({fullOutput, rawOutput, ...rest}) => rest)}
// mkdir first: migration already had docs/refoundation/evidence/phase-4/, this repository has no
// docs/evidence/ at all. Without this the gate runs every step for roughly fifteen minutes and
// then throws ENOENT on its final line, discarding the verdict it just spent that long computing.
// --record writes the committed evidence. A FAIL without --record is written OUTSIDE the tracked tree
// (.claude/land-evidence/ is ignored) so a red gate never dirties the checkout it ran in and the rerun
// stays clean; eng/land-evidence.sh reads it from there before the land worktree is removed.
const target = evidenceTarget({record, status: report.status, apiRoot, evidencePath})
if (target) {
  mkdirSync(path.dirname(target), {recursive: true})
  writeFileSync(target, `${JSON.stringify(persisted, null, 2)}\n`)
}
process.stdout.write(`${JSON.stringify({status: report.status, apiCommit: head.slice(0, 7), steps: steps.map(s => `${s.id}:${s.passed === false ? 'FAIL' : 'ok'}`)}, null, 2)}\n`)
if (report.status === 'FAIL') {
  for (const step of persisted.steps.filter(step => step.passed === false)) {
    process.stdout.write(`${step.id}:\n`)
    for (const line of (step.tail ?? '').split('\n')) process.stdout.write(`  ${line}\n`)
  }
}
process.exit(report.status === 'PASS' ? 0 : 1)
