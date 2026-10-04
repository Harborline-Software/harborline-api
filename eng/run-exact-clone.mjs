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
import {execFileSync} from 'node:child_process'
import {copyFileSync, mkdtempSync, mkdirSync, readdirSync, rmSync, writeFileSync, readFileSync, existsSync} from 'node:fs'
import {evidenceTarget, persistStepEvidence} from './exact-clone-evidence.mjs'
import {observedSpawnSync, resetProgressFile} from './exact-clone-progress.mjs'
import {validateFlakeRegistry, RETRY_LIMIT} from './flake-registry.mjs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {randomUUID} from 'node:crypto'
import {resolveCommand} from './lib/resolve-command.mjs'
import {baselineArgument, compareHostBaseline, readHostTrx, readVitestJsonAsTrx, capabilityBaselineFor, normalizeIdentity, rosterIdCollisions, unexplainedRosterLoss} from './host-baseline.mjs'
import {copyCoberturaReport, coverageEnabled, qualityCoveragePaths} from './coverage.mjs'
import {beginQualityProduction, recordQualityProduction} from './quality-production.mjs'
import {qualityArtifacts} from './quality-step.mjs'
import {persistInputShadow} from './validation-inputs.mjs'
import {observeNuGetRoot} from './validation-nuget-root.mjs'

// Vendored from harborline-migration tooling/run-api-exact-clone.mjs (2026-08-20). This was the
// ONLY clean-clone proof harborline-api had, and it lived in a repo with no remote that is being
// deleted. apiRoot and the baselines now resolve inside this repository; the blob-pin below
// therefore pins against this repo's HEAD instead of migration's, which is simpler and correct.
const apiRoot = path.resolve(import.meta.dirname, '..')
const record = process.argv.includes('--record')
// Shared by the host and capability lanes below: which of a lane's observed failures are NOT on
// its own permittedFailures list (the ones a baseline comparison must never silently swallow).
const unpermitted = (failedNames, permittedNames) => failedNames.filter(name => !permittedNames.has(name))
// T-724 ruling 119e: knownTests is a committed, sorted list of test identities, generated ONLY by
// this tool (or the nightly/land repin tooling) and never hand-edited. `--write-known-tests`
// refreshes it from what this run actually observed running; an ordinary gate run never writes it.
const writeKnownTests = process.argv.includes('--write-known-tests')
// Escape hatch for the unexplainedRosterLoss guard below: a deliberate, reviewed change to HOW
// identities are built (not a test-suite change) makes every old entry look "gone" against the new
// candidate, and that is not what ruling 119a's policyRemovals review is for. Requires a human to
// pass this explicitly, alongside --write-known-tests, and is not something ordinary tooling passes.
const forceKnownTests = process.argv.includes('--force-known-tests')
const evidencePath = path.join(apiRoot, 'docs/evidence/exact-clone.json')
const collectCoverage = coverageEnabled()
const coveragePaths = qualityCoveragePaths(apiRoot)
const qualityEnabled = process.env.HARBORLINE_GATE_QUALITY === '1'
if (qualityEnabled) {
  // qualityArtifacts also finds SARIF outside the engine directories. Never attest an old
  // top-level or nested report merely because this run rebuilt the three normal directories.
  const oldQuality = qualityArtifacts(apiRoot)
  beginQualityProduction(apiRoot, [...oldQuality.sarif, ...oldQuality.rawSarif])
}

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
  capability: capabilityBaselineFor(),
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
let knownTestsWriteRefused = false
const steps = []
const progressFile = path.join(apiRoot, '.claude', 'gate-evidence', `exact-clone-${head}-progress.jsonl`)
// A new attempt must not inherit a previous attempt's terminal state.
resetProgressFile(progressFile)
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
const run = (id, command, args, cwd, {expectNonZero = false, diagnosticDirectory} = {}) => {
  const started = Date.now()
  const resolved = resolveCommand(command, args)
  const result = observedSpawnSync(id, resolved.executable, resolved.args, {cwd, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024}, {file: progressFile, diagnosticDirectory})
  const rawOutput = `${result.stdout ?? ''}${result.stderr ?? ''}`
  // Error messages, executable paths and spawn arguments can contain credentials.
  // Retain only bounded OS codes and numeric errno, plus the termination signal.
  const safeCode = value => typeof value === 'string' && /^[A-Z][A-Z0-9_]{0,63}$/.test(value) ? value : 'UNKNOWN'
  const spawnError = result.error ? {code: safeCode(result.error.code),
    ...(Number.isSafeInteger(result.error.errno) ? {errno: result.error.errno} : {})} : undefined
  const signal = result.signal ? safeCode(result.signal) : undefined
  const diagnostics = [spawnError && `spawn error: ${JSON.stringify(spawnError)}`, signal && `termination signal: ${signal}`].filter(Boolean)
  const output = stripAnsi(rawOutput) + (diagnostics.length ? `\n${diagnostics.join('\n')}\n` : '')
  const step = {
    id,
    passed: expectNonZero ? true : result.status === 0,
    verdictFrom: expectNonZero ? 'baseline comparison, not exit code' : 'exit code',
    exitCode: result.status,
    ...(spawnError ? {spawnError} : {}),
    ...(signal ? {signal} : {}),
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
let persisted
let packageRootResolution = {status: 'unavailable'}
try {
  if (collectCoverage) {
    rmSync(path.join(apiRoot, 'artifacts', 'quality', 'coverage'), {recursive: true, force: true})
    for (const report of Object.values(coveragePaths)) rmSync(report, {force: true})
  }
  const cloneStep = run('git-clone', 'git', ['clone', '--quiet', '--no-hardlinks', apiRoot, clone], apiRoot)
  if (!cloneStep.passed) throw new Error('Exact-clone git clone failed; see stage evidence')
  // Resolve once before restore/build. Keep approval as parent-owned state even
  // when a failed query leaves an inherited override available to the build.
  packageRootResolution = observeNuGetRoot({cwd: clone})
  if (packageRootResolution.status !== 'resolved') {
    console.error('compiler package root unavailable; input observation remains incomplete')
  }

  // Sanity: the clone must carry no build or dependency artifacts. If it does, the .gitignore is
  // wrong and this gate would be testing the same ambient state it exists to exclude.
  const tracked = execFileSync('git', ['-C', clone, 'ls-files'], {encoding: 'utf8', maxBuffer: 1e9}).split('\n')
  const artifacts = tracked.filter(file => /(^|\/)(node_modules|obj|bin)\//.test(file))
  steps.push({id: 'clone-carries-no-artifacts', passed: artifacts.length === 0, artifactCount: artifacts.length, sample: artifacts.slice(0, 5)})

  run('validation-reuse-contracts', process.execPath, ['--test',
    'eng/tests/validation-reuse.test.mjs', 'eng/tests/validation-inputs.test.mjs',
    'eng/tests/validation-github-shadow.test.mjs', 'eng/tests/validation-producer-policy.test.mjs',
    'eng/tests/validation-compiler-inputs.test.mjs', 'eng/tests/validation-consumer.test.mjs'], clone)
  run('platform-feed', process.execPath, ['eng/exact-clone-platform-feed.mjs', apiRoot, scratch], clone)
  run('dotnet-restore', 'dotnet', ['restore', 'Harborline.Api.slnx', '-nodeReuse:false', '-maxcpucount:6'], clone)
  // Ticket 340: on landing, the clean-clone build is also the Roslyn analysis
  // invocation. Directory.Build.targets expands the project name per compiler
  // invocation, so the single solution build cannot overwrite one global log.
  const qualityDirectory = path.join(apiRoot, 'artifacts', 'quality')
  const roslynDirectory = path.join(qualityDirectory, 'roslyn')
  const archDirectory = path.join(qualityDirectory, 'arch')
  const eslintDirectory = path.join(qualityDirectory, 'eslint')
  const buildArgs = ['build', 'Harborline.Api.slnx', '-c', 'Release', '--nologo', '--no-restore', '-nodeReuse:false', '-maxcpucount:6']
  buildArgs.push('-p:HarborlineValidationInputCapture=1')
  process.env.HARBORLINE_VALIDATION_CAPTURE_SESSION = randomUUID()
  buildArgs.push(`-p:HarborlineValidationCaptureSession=${process.env.HARBORLINE_VALIDATION_CAPTURE_SESSION}`)
  if (qualityEnabled) {
    rmSync(roslynDirectory, {recursive: true, force: true})
    rmSync(archDirectory, {recursive: true, force: true})
    rmSync(eslintDirectory, {recursive: true, force: true})
    mkdirSync(roslynDirectory, {recursive: true})
    mkdirSync(archDirectory, {recursive: true})
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
    // Owner ruling Q38: tests tagged Lane=perf (the Layout timing-parity collection) measure wall-clock
    // timing and run only in verify-perf, alone on mac16 (perf-quiet); every host lane excludes them.
    ['test', 'apps/local-node-host/tests/tests.csproj', '-c', 'Release', '--nologo', '--no-build', '-nodeReuse:false', '-maxcpucount:6',
      '--filter', 'Lane!=perf',
      '--logger', 'trx;LogFileName=host-tests.trx', '--results-directory', hostResultsDirectory,
      // Plain blame observes test events only: no hang timeout, dump, abort or coverage change.
      '--blame', '--diag', `${path.join(scratch, 'host-diagnostics', 'vstest.log')};TraceLevel=Info`,
      ...(collectCoverage ? ['--settings', 'eng/coverage.runsettings', '--collect:XPlat Code Coverage'] : [])], clone,
    {expectNonZero: true, diagnosticDirectory: path.join(scratch, 'host-diagnostics')})
  run('analyzer-canary', 'bash', ['eng/verify-analyzer-canary.sh'], clone)
  run('arch-canary', 'bash', ['eng/verify-arch-canary.sh'], clone)
  // 323: the globalization positive control builds one project, so it needs the restored clone, not the bare checkout.
  run('globalization-canary', 'bash', ['eng/verify-globalization-canary.sh'], clone)
  if (qualityEnabled) {
    const archSarif = path.join(archDirectory, 'api-tier-dependency.sarif')
    run('arch-sarif', process.execPath,
      ['eng/arch-sarif.mjs', '--repo-root', clone, path.join(hostResultsDirectory, 'host-tests.trx'), archSarif], clone)
    const sarif = JSON.parse(readFileSync(archSarif, 'utf8'))
    const results = sarif.runs?.flatMap(run => run.results ?? []) ?? []
    const sarifRun = sarif.runs?.[0]
    const hasResultShape = results.every(result => result.ruleId === 'HLQ.ARCH.1000'
      && /^[^/:]+(?:\/[^/:]+)*$/.test(result.locations?.[0]?.physicalLocation?.artifactLocation?.uri ?? '')
      && Number.isInteger(result.locations?.[0]?.physicalLocation?.region?.startLine)
      && result.partialFingerprints && typeof result.partialFingerprints === 'object')
    steps.push({id: 'arch-sarif', passed: sarif.version === '2.1.0' && sarifRun?.tool?.driver?.name === 'arch'
      && sarifRun?.invocations?.every(invocation => invocation.executionSuccessful === true) && hasResultShape,
    resultCount: results.length, sarifCount: 1, driver: sarifRun?.tool?.driver?.name})
  }
  if (collectCoverage) {
    copyCoberturaReport({resultsDirectory: hostResultsDirectory, target: coveragePaths.host,
      label: 'unit-tests', sourceRoot: '.'})
  }
  run('boundary-check', 'bash', ['eng/verify-boundaries.sh'], clone)

  run('capability-typecheck', 'npx', ['tsc', '-p', 'tsconfig.json', '--noEmit'], path.join(clone, 'apps/capability-host'))
  // --reporter=json (in addition to the default console reporter) names every test, passed or
  // failed, not only the failures a bare summary would count -- the capability-baseline-match
  // identity comparison below needs the full roster, the same way TRX gives it to the host step.
  const capabilityJsonPath = path.join(scratch, 'capability-tests.json')
  const capabilityTests = run('capability-tests', 'npx',
    ['vitest', 'run', '--reporter=default', '--reporter=json', `--outputFile=${capabilityJsonPath}`],
    path.join(clone, 'apps/capability-host'), {expectNonZero: true})

  const countsOf = text => {
    const dotnet = text.match(/Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)/)
    if (dotnet) return {failed: +dotnet[1], passed: +dotnet[2], skipped: +dotnet[3], total: +dotnet[4]}
    const vitest = text.match(/Tests\s+(?:(\d+)\s+failed\s*\|\s*)?(\d+)\s+passed(?:\s*\|\s*(\d+)\s+skipped)?\s*\((\d+)\)/)
    if (vitest) return {failed: +(vitest[1] ?? 0), passed: +vitest[2], skipped: +(vitest[3] ?? 0), total: +vitest[4]}
    return null
  }
  // The host step always requests a TRX (`--logger trx;LogFileName=host-tests.trx` above), so its
  // per-test identities are always available; the comparison below is identity-based for every
  // baseline now (T-724 ruling 119), never only for a baseline marked "named".
  const hostTrx = readHostTrx(path.join(hostResultsDirectory, 'host-tests.trx'))
  const hostCounts = hostTrx.counts
  // Some capability test titles interpolate an absolute path under the clone (e.g. the operational-
  // environment Python-worker table names the script it invoked), and the clone lives at a fresh
  // mkdtempSync path every run -- an identity that embeds it would never survive a re-run, let alone
  // a knownTests refresh from a different machine. Redact it exactly as the step tail already is.
  const capabilityTrxRaw = readVitestJsonAsTrx(capabilityJsonPath)
  const capabilityTrx = {...capabilityTrxRaw, results: capabilityTrxRaw.results.map(row => ({
    ...row, testName: normalizeIdentity(redactEvidence(row.testName)), rosterId: normalizeIdentity(redactEvidence(row.rosterId)),
  }))}

  // T-724 ruling 119e: this candidate is written on EVERY run, pass or fail, --write-known-tests or
  // not -- it is what a nightly/CI run actually observed, kept as evidence (under
  // .claude/gate-evidence/, already uploaded by every verify-* job's "if: always()" artifact step)
  // for a scheduled drift check (eng/known-tests-drift.mjs) to compare against the committed roster
  // without ever committing anything itself. `--write-known-tests` below reuses this SAME candidate
  // to splice the committed baseline -- one code path feeds both the manual refresh and the report.
  // rosterId (CodeRabbit 4113873155), not the bare testName: two cases can share a testName (a
  // repeated theory DisplayName, or a same-title capability case from a different describe block).
  const ranNamesOf = trx => [...new Set(trx.results.filter(row => row.outcome !== 'NotExecuted').map(row => row.rosterId ?? row.testName))].sort()
  const knownTestsCandidate = {
    schemaVersion: 1, recordedAt: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'), apiCommit: head,
    host: {baseline: BASELINES.host, identities: ranNamesOf(hostTrx)},
    capability: {baseline: BASELINES.capability, identities: ranNamesOf(capabilityTrx)},
  }
  const evidenceDirectory = path.join(apiRoot, '.claude', 'gate-evidence')
  mkdirSync(evidenceDirectory, {recursive: true})
  writeFileSync(path.join(evidenceDirectory, 'known-tests-candidate.json'), `${JSON.stringify(knownTestsCandidate, null, 2)}\n`)

  if (writeKnownTests) {
    let refused = false
    for (const {baseline: relative, identities} of [knownTestsCandidate.host, knownTestsCandidate.capability]) {
      // CodeRabbit 4113873155: refuse a roster that would itself contain a collision (two rows
      // reduced to the same identity) rather than silently writing a shorter, ambiguous list --
      // ranNamesOf already deduplicates via Set, so this only fires if rosterId generation itself
      // produced a genuine duplicate (a bug, not a normal run), and it must be loud. Checked against
      // the RAW per-result identities, before that Set-dedup can hide a collision.
      const trx = relative === BASELINES.host ? hostTrx : capabilityTrx
      const dupes = rosterIdCollisions(trx.results.map(row => row.rosterId ?? row.testName))
      if (dupes.length) {
        console.error(`known tests REFUSED for ${relative}: ${dupes.length} rosterId collision(s), the same identity produced by more than one result:`)
        for (const id of dupes) console.error(`  ${id}`)
        refused = true
        continue
      }
      const target = path.join(apiRoot, relative)
      const current = JSON.parse(readFileSync(target, 'utf8'))
      // CodeRabbit 4113873156/4113873157 area (2026-09-27): --write-known-tests must not be a
      // laundering route for ruling 119a -- overwriting a populated roster with a candidate that
      // silently drops a known identity would erase the very disappearance the gate exists to
      // catch, with no policyRemovals review. Validate the candidate against the CURRENT committed
      // roster first, exactly as the gate itself would; only a first (empty-roster) population
      // skips this, since there is nothing yet to compare against.
      const currentKnown = current.knownTests ?? []
      if (currentKnown.length > 0 && !forceKnownTests) {
        const unexplained = unexplainedRosterLoss(currentKnown, identities, current.policyRemovals)
        if (unexplained.length) {
          console.error(`known tests REFUSED for ${relative}: ${unexplained.length} identity(ies) in the committed roster did not run this time and no policyRemovals row names them:`)
          for (const name of unexplained) console.error(`  ${name}`)
          console.error('  Add a policyRemovals row (T-724 ruling 119a/b) if this is intentional, or pass --force-known-tests for a deliberate identity-FORMAT change, then re-run --write-known-tests. The committed roster is unchanged.')
          refused = true
          continue
        }
      }
      writeFileSync(target, `${JSON.stringify({...current, knownTests: identities}, null, 2)}\n`)
      console.log(`known tests written: ${relative} (${identities.length} identities)`)
    }
    if (refused) {
      console.error('--write-known-tests: at least one roster was refused; see above. Exiting non-zero.')
      knownTestsWriteRefused = true
    }
  }

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

  const observedFailures = hostTrx.results.filter(row => row.outcome === 'Failed').map(row => row.testName)
  const unexpected = unpermitted(observedFailures, permittedNames)
  const retryable = unexpected.every(name => flakyLimits.has(name)) ? unexpected : []
  const retries = []
  let retryStage = 0
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
      const result = observedSpawnSync(`host-retry-${++retryStage}`, resolved.executable, resolved.args, {cwd: clone, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024}, {file: progressFile})
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
  if (!hostComparison.passed) {
    // Keep the clone on an identity failure so the operator can inspect both TRX and raw output.
    retainScratch = true
    mkdirSync(hostResultsDirectory, {recursive: true})
    const outputFile = path.join(hostResultsDirectory, 'host-tests-output.txt')
    writeFileSync(outputFile, hostTests.rawOutput)
    // A red verdict is exactly when the TRX matters most, and "TRX missing" is itself one of the
    // red reasons -- so copying unconditionally would throw on the very path it exists to explain.
    const trxSource = path.join(hostResultsDirectory, 'host-tests.trx')
    if (existsSync(trxSource)) {
      const trxEvidence = path.join(apiRoot, '.claude', 'gate-evidence', 'host-tests.trx')
      mkdirSync(path.dirname(trxEvidence), {recursive: true})
      copyFileSync(trxSource, trxEvidence)
    }
    hostComparison.problems = hostComparison.problems.map(line => `${line}; host output: ${outputFile}`)
    hostComparison.tail = hostComparison.problems.join('\n')
  }
  for (const line of hostComparison.problems ?? []) console.log(line)
  if (hostComparison.rosterUnpopulated) console.log(hostComparison.rosterUnpopulated)
  steps.push({
    id: 'host-baseline-match',
    ...hostComparison,
    fullOutput: hostComparison.passed === false ? hostTests.fullOutput : undefined,
    baseline: BASELINES.host,
    expected: hostBaseline.totals, observed: hostCounts,
    newFailures,
    observedAfterFlakeRetry: adjustedFailed === null ? null : {...hostCounts, failed: adjustedFailed},
    rescuedByRetry: rescued,
    note: hostComparison.note ?? 'Counts AND failure identity (newFailures must be empty), after the bounded knownFlaky retry. Failure IDENTITY is pinned by name in host-test-baseline.json and must be reviewed on any change.',
  })
  // T-724 ruling 119d: the same identity comparison as the host step, not an exact-count match --
  // total is informational, a known test disappearing needs a policyRemovals row, and an unpermitted
  // failure is red regardless of the total.
  const capabilityPermittedNames = new Set((capabilityBaseline.permittedFailures ?? []).map(row => row.test))
  const capabilityFailedNames = capabilityTrx.results.filter(row => row.outcome === 'Failed').map(row => row.testName)
  const capabilityNewFailures = unpermitted(capabilityFailedNames, capabilityPermittedNames)
  const capabilityComparison = compareHostBaseline({
    baseline: capabilityBaseline, counts: capabilityTrx.counts, adjustedFailed: capabilityTrx.counts?.failed,
    newFailures: capabilityNewFailures, trx: capabilityTrx,
  })
  for (const line of capabilityComparison.problems ?? []) console.log(line)
  if (capabilityComparison.rosterUnpopulated) console.log(capabilityComparison.rosterUnpopulated)
  steps.push({
    id: 'capability-baseline-match',
    ...capabilityComparison,
    fullOutput: capabilityComparison.passed === false ? capabilityTests.fullOutput : undefined,
    baseline: BASELINES.capability,
    expected: capabilityBaseline.totals, observed: capabilityTrx.counts,
    newFailures: capabilityNewFailures,
    note: capabilityComparison.note ?? `Identity comparison (T-724 ruling 119): the total is informational. Failure IDENTITY is pinned by name in ${BASELINES.capability} (the platform-selected capability baseline).`,
  })

  const passed = steps.every(step => step.passed !== false)
  report = {
    schemaVersion: 1, repository: 'harborline-api', gate: 'destination-exact-clone',
    baselineProvenance,
    status: passed ? 'PASS' : 'FAIL', apiCommit: head,
    recordedAt: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'),
    steps,
  }
} catch (error) {
  steps.push({id: 'report-assembly', passed: false,
    tail: redactEvidence(error instanceof Error ? error.stack ?? error.message : String(error))})
  report = {
    schemaVersion: 1, repository: 'harborline-api', gate: 'destination-exact-clone',
    baselineProvenance, status: 'FAIL', apiCommit: head,
    recordedAt: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'), steps,
  }
} finally {
  // Observe the dependency/native closure while scratch still exists. Shadow data never
  // authorizes skipping work and a collection failure cannot change the gate verdict.
  persistInputShadow({apiRoot, clone, hostBaseline: BASELINES.host,
    coverage: collectCoverage, quality: qualityEnabled, packageRootResolution})
  // Preserve captured command output before removing scratch, including an aborted report.
  persisted = persistStepEvidence({report, apiRoot, redactEvidence})
  if (!retainScratch) rmSync(scratch, {recursive: true, force: true})
}

// Record the report whatever its status. A gate that discards its own evidence on failure forces
// a full re-run to learn why it failed — the same evidence-destruction pattern as piping a test
// run through `tail`. The report carries its own status; consumers check that, not the file's
// existence. This matches run-platform-exact-clone.mjs, which records its failures too.
// Keep complete failed-step output in the existing ignored evidence directory, using the
// same path and ANSI redaction as the report tail. Raw output stays out of recorded JSON;
// the report references a relative artifact path, and passing reports stay compact.
if (qualityEnabled && report.status === 'PASS' && !knownTestsWriteRefused) {
  const producedQuality = qualityArtifacts(apiRoot)
  recordQualityProduction(apiRoot, {head, run: process.env.HARBORLINE_VERIFY_QUALITY_RUN,
    files: [...producedQuality.sarif, ...producedQuality.rawSarif]})
}
// mkdir first: migration already had docs/refoundation/evidence/phase-4/, this repository has no
// docs/evidence/ at all. Without this the gate runs every step for roughly fifteen minutes and
// then throws ENOENT on its final line, discarding the verdict it just spent that long computing.
// --record writes the committed evidence. A FAIL without --record is written OUTSIDE the tracked tree
// (.claude/gate-evidence/ is ignored) so a red gate never dirties the checkout it ran in and the rerun
// stays clean.
const target = evidenceTarget({record, status: report.status, apiRoot, evidencePath})
if (target) {
  mkdirSync(path.dirname(target), {recursive: true})
  writeFileSync(target, `${JSON.stringify(persisted, null, 2)}\n`)
}
process.stdout.write(`${JSON.stringify({status: report.status, apiCommit: head.slice(0, 7), steps: steps.map(s => `${s.id}:${s.passed === false ? 'FAIL' : 'ok'}`)}, null, 2)}\n`)
if (report.status === 'FAIL') {
  for (const step of persisted.steps.filter(step => step.passed === false)) {
    process.stdout.write(`${step.id}:\n`)
    if (step.outputFile) process.stdout.write(`  full output: ${step.outputFile}\n`)
    for (const line of (step.tail ?? '').split('\n')) process.stdout.write(`  ${line}\n`)
  }
}
process.exit(report.status === 'PASS' && !knownTestsWriteRefused ? 0 : 1)
