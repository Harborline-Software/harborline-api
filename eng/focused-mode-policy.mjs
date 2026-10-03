import {createHash} from 'node:crypto'
import {execFileSync, spawnSync} from 'node:child_process'
import {existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, writeFileSync} from 'node:fs'
import path from 'node:path'
import {hostname} from 'node:os'
import {readHostTrx} from './host-baseline.mjs'
import {copyCoberturaReport, coverageSummaryFromFile, isCollectorCoberturaReport} from './coverage.mjs'

// An additional early regression check, never a replacement for a landing lane.
// Digests bind content; they are not authentication or permission to reuse a build.
const digest = value => `sha256:${createHash('sha256').update(JSON.stringify(value)).digest('hex')}`
const modes = ['coverage-off', 'coverage-on']
const safePath = value => typeof value === 'string' && value.length > 0
  && !/^[\\/]|^[A-Za-z]:|[\\\x00-\x1f]/.test(value)
  && !value.split('/').some(part => !part || part === '.' || part === '..')
const sha = value => typeof value === 'string' && /^[a-f0-9]{40}$/.test(value)
const fail = (code, detail) => { throw new Error(`focused-mode ${code}: ${detail}`) }

function category(file) {
  if (!safePath(file)) return 'unknown'
  if (/^(README|CONTRIBUTING|AGENTS|CI-PIPELINE)\.md$/.test(file)
    || /^(docs|adrs|designs)\/.*\.md$/.test(file)) return 'documentation'
  if (/coverage|\.runsettings$/i.test(file)) return 'coverage'
  if (/(^|\/)(global\.json|NuGet\.config|packages\.lock\.json)$/.test(file)
    || /^eng\/(platform|quality)-pin\.json$/.test(file)) return 'toolchain'
  if (/\.(csproj|props|targets|sln|slnx)$/.test(file) || /^artifacts\//.test(file)
    || /^eng\//.test(file) || /^\.github\//.test(file)) return 'build-artifact'
  if (/\.cs$/.test(file) && /validat/i.test(file)) return 'validation'
  if (/\.cs$/.test(file) && /reflection|construction|ArchTests/i.test(file)) return 'reflection'
  if (/\.(cs|dll|exe|pdb|nupkg|so|dylib|bin)$/.test(file)) return 'binary'
  return 'unknown'
}

// Phase 1: no completed receipt is needed to decide what must execute.
export function classifyFocusedModes(changes, {diffAvailable = true} = {}) {
  const records = []
  let unsupported = diffAvailable !== true || !Array.isArray(changes)
  for (const change of Array.isArray(changes) ? changes : []) {
    const status = change?.status
    const validStatus = typeof status === 'string' && /^(A|M|D|R(?:100|[0-9]{1,2})?|C(?:100|[0-9]{1,2})?)$/.test(status)
    const renamed = validStatus && /^[RC]/.test(status)
    const files = renamed ? [change.oldPath, change.path] : [change?.path]
    if (!validStatus || files.some(file => !safePath(file))) unsupported = true
    for (const file of files) {
      const kind = validStatus ? category(file) : 'unknown'
      if (kind === 'unknown') unsupported = true
      records.push({path: safePath(file) ? file : null, status: validStatus ? status : null, kind})
    }
  }
  records.sort((a, b) => JSON.stringify(a) < JSON.stringify(b) ? -1 : JSON.stringify(a) > JSON.stringify(b) ? 1 : 0)
  const requiredModes = unsupported || records.some(row => row.kind !== 'documentation') ? [...modes] : []
  const body = {schemaVersion: 1, diffAvailable: diffAvailable === true,
    changes: records, requiredModes, selection: unsupported ? 'unsupported' : requiredModes.length ? 'focused' : 'not-required'}
  return {...body, planDigest: digest(body)}
}

// This suite executes compiled-assembly reflection/IL fences and their planted
// bypass control. It is a bounded proof about that suite, not all behavior tests.
export const focusedSuite = Object.freeze({
  id: 'compiled-write-fences-v1', project: 'apps/local-node-host/tests/tests.csproj',
  filter: 'FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.ArchTests.WritePipelineExecutorFenceTests',
  requiredTests: Object.freeze([
    'ck-10 fence: no production code runs its own loop over the ADR 0038 stage order',
    'ck-10 fence: only the executor calls a KernelWrite stage',
    'ck-10 fence: every admitted-write commit is a KernelWrite commit stage or a reviewed not-yet-moved path',
    'ck-10 fence: the record writer commits only from its KernelWrite commit stages, EF saves included',
    'ck-10 fence: a planted bypass outside the writer is caught by every check',
  ]),
})
const scopeDigest = digest(focusedSuite)

export function requireRunnableSelection(plan) {
  const body = {...plan}; delete body.planDigest
  if (plan?.schemaVersion !== 1 || digest(body) !== plan.planDigest) fail('invalid-plan', 'selection digest/schema mismatch')
  if (plan.selection === 'unsupported') fail('unsupported-selection', 'unknown change class or unavailable/invalid diff; dual-mode proof is required but no supported focused selector was established')
  if (plan.diffAvailable !== true || !Array.isArray(plan.changes) || plan.changes.some(row =>
    !row || !safePath(row.path) || row.kind === 'unknown' || row.kind !== category(row.path)
    || !/^(A|M|D|R(?:100|[0-9]{1,2})?|C(?:100|[0-9]{1,2})?)$/.test(row.status ?? ''))) {
    fail('invalid-plan', 'change records do not match the classifier')
  }
  const expected = plan.changes.some(row => row.kind !== 'documentation') ? modes : []
  if (plan.selection !== (expected.length ? 'focused' : 'not-required') || JSON.stringify(plan.requiredModes) !== JSON.stringify(expected)) {
    fail('invalid-plan', 'mode requirements do not match selection')
  }
  return plan.requiredModes.length ? focusedSuite : null
}

// Phase 2: compare independently executed OFF/ON results only after execution.
export function validateFocusedModeEvidence(plan, expected, receipts) {
  const suite = requireRunnableSelection(plan)
  if (!Array.isArray(receipts)) fail('missing-evidence', 'receipt list absent')
  if (!suite) {
    if (receipts.length) fail('unexpected-evidence', 'no additional focused modes selected')
    return {status: 'not-required', planDigest: plan.planDigest}
  }
  if (!sha(expected?.commit) || !sha(expected?.tree)
    || ['host', 'sdk', 'run'].some(key => typeof expected?.[key] !== 'string' || !expected[key].trim() || expected[key].length > 256)
    || !/^sha256:[a-f0-9]{64}$/.test(expected?.dependencies ?? '')) {
    fail('invalid-context', 'exact source, host, SDK, dependency digest and run identity are required')
  }
  if (receipts.length !== 2 || receipts.some(row => !row || typeof row !== 'object')
    || new Set(receipts.map(row => row.mode)).size !== 2) fail('missing-mode', 'exactly one OFF and one ON receipt required')
  for (const mode of modes) {
    const row = receipts.find(item => item.mode === mode)
    if (!row) fail('missing-mode', mode)
    if (row.schemaVersion !== 1 || row.planDigest !== plan.planDigest || row.scopeDigest !== scopeDigest) fail('wrong-scope', mode)
    for (const key of ['commit', 'tree', 'host', 'sdk', 'dependencies', 'run']) {
      if (row[key] !== expected[key]) fail('identity-mismatch', `${mode} ${key}`)
    }
    const {counts, results} = row
    if (row.exitCode !== 0 || row.executed !== true || !counts || !Array.isArray(results)
      || !Number.isSafeInteger(counts.total) || counts.total < focusedSuite.requiredTests.length
      || counts.passed !== counts.total || counts.failed !== 0 || counts.notExecuted !== 0
      || results.length !== counts.total || results.some(test => !test || test.outcome !== 'Passed'
        || typeof test.rosterId !== 'string' || !test.rosterId || typeof test.testName !== 'string' || !test.testName)
      || new Set(results.map(test => test.rosterId)).size !== results.length) fail('nonpassing-evidence', mode)
    for (const name of focusedSuite.requiredTests) {
      if (!results.some(test => test.testName === name)) fail('missing-test', `${mode} ${name}`)
    }
    if (mode === 'coverage-on' && !/^sha256:[a-f0-9]{64}$/.test(row.coverageDigest ?? '')) fail('missing-coverage', mode)
    if (mode === 'coverage-on') {
      const coverage = row.coverage
      if (!Number.isSafeInteger(coverage?.validLines) || coverage.validLines <= 0
        || !Number.isSafeInteger(coverage?.coveredLines) || coverage.coveredLines < 0 || coverage.coveredLines > coverage.validLines
        || !Array.isArray(coverage?.paths) || !coverage.paths.length
        || coverage.paths.some(file => typeof file !== 'string' || !file.trim())
        || new Set(coverage.paths).size !== coverage.paths.length) fail('nonpopulated-coverage', mode)
    }
    if (mode === 'coverage-off' && (row.coverageDigest !== null || row.coverage !== null)) fail('unexpected-coverage', mode)
  }
  const inventories = receipts.map(row => row.results.map(test => test.rosterId).sort())
  if (JSON.stringify(inventories[0]) !== JSON.stringify(inventories[1])) fail('inventory-mismatch', 'OFF and ON executed different tests')
  return {status: 'passed', planDigest: plan.planDigest, scopeDigest}
}

export function parseChangedFiles(raw) {
  const fields = raw.split('\0'); if (fields.at(-1) === '') fields.pop()
  const changes = []
  for (let i = 0; i < fields.length;) {
    const status = fields[i++]
    const oldPath = /^[RC]/.test(status) ? fields[i++] : undefined
    const file = fields[i++]
    if (!file || (oldPath !== undefined && !oldPath)) fail('invalid-diff', 'incomplete name-status record')
    changes.push({status, path: file, ...(oldPath !== undefined ? {oldPath} : {})})
  }
  return changes
}

function dependencyDigest(root) {
  const feed = path.join(root, '.feed')
  if (!existsSync(path.join(feed, 'packed-version.props'))) fail('missing-feed', 'build the pinned local feed before focused modes')
  const files = readdirSync(feed).filter(name => name.endsWith('.nupkg') || name === 'packed-version.props').sort()
  if (!files.some(name => name.endsWith('.nupkg'))) fail('missing-feed', 'pinned package bytes absent')
  return digest(files.map(name => [name, createHash('sha256').update(readFileSync(path.join(feed, name))).digest('hex')]))
}

const coberturaFiles = directory => readdirSync(directory, {withFileTypes: true}).flatMap(entry => {
  const file = path.join(directory, entry.name)
    return entry.isDirectory() ? coberturaFiles(file)
      : entry.isFile() && (isCollectorCoberturaReport(file) || /\.cobertura\.xml$/i.test(entry.name)) ? [file] : []
})

// Executable hook: called by the existing verify-preflight CLI for host/all.
// Isolated output paths and explicit collector settings prevent stale mode reuse.
// Bank's authenticated construction/reuse receipts remain a separate contract.
export function executeFocusedModes({apiRoot = process.cwd(), env = process.env,
  git = (...args) => execFileSync('git', ['-C', apiRoot, ...args], {encoding: 'utf8'}).trim(),
  command = (executable, args, options) => spawnSync(executable, args, options),
  readTrx = readHostTrx, dependencies = () => dependencyDigest(apiRoot),
  sdk = () => execFileSync('dotnet', ['--version'], {cwd: apiRoot, encoding: 'utf8'}).trim(),
} = {}) {
  if (git('status', '--porcelain')) fail('dirty-source', 'commit the candidate before early focused parity')
  const commit = git('rev-parse', 'HEAD'), tree = git('rev-parse', 'HEAD^{tree}')
  let plan
  try {
    const base = git('merge-base', 'HEAD', 'origin/main')
    const raw = git('diff', '--name-status', '-z', '--find-renames', base, 'HEAD')
    plan = classifyFocusedModes(parseChangedFiles(raw))
  } catch { plan = classifyFocusedModes(null, {diffAvailable: false}) }
  const suite = requireRunnableSelection(plan)
  if (!suite) return validateFocusedModeEvidence(plan, null, [])
  const evidenceRoot = path.join(apiRoot, '.claude', 'gate-evidence', 'focused-modes')
  mkdirSync(evidenceRoot, {recursive: true})
  const directory = mkdtempSync(path.join(evidenceRoot, 'run-'))
  const expected = {commit, tree, host: `${hostname()}/${process.platform}/${process.arch}`, sdk: sdk(), dependencies: dependencies(), run: path.basename(directory)}
  const receipts = []
  for (const mode of plan.requiredModes) {
    const resultsDirectory = path.join(directory, mode)
    mkdirSync(resultsDirectory, {recursive: true})
    const offSettings = path.join(resultsDirectory, 'coverage-off.runsettings')
    if (mode === 'coverage-off') writeFileSync(offSettings,
      '<RunSettings><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName="XPlat Code Coverage" enabled="false" /></DataCollectors></DataCollectionRunSettings></RunSettings>', 'utf8')
    const args = ['test', suite.project, '-c', 'Release', '--nologo', '-nodeReuse:false', '-maxcpucount:1',
      '--artifacts-path', path.join(apiRoot, 'artifacts', 'focused-modes', expected.run, mode),
      '--filter', suite.filter, '--logger', 'trx;LogFileName=focused.trx', '--results-directory', resultsDirectory,
      '--settings', mode === 'coverage-on' ? path.join(apiRoot, 'eng', 'coverage.runsettings') : offSettings,
      ...(mode === 'coverage-on' ? ['--collect:XPlat Code Coverage'] : [])]
    let result
    try { result = command('dotnet', args, {cwd: apiRoot, env: {...env, HARBORLINE_GATE_COVERAGE: mode === 'coverage-on' ? '1' : '0'}, stdio: 'inherit', shell: false}) }
    catch { result = {status: null} }
    const trx = readTrx(path.join(resultsDirectory, 'focused.trx'))
    let coverageDigest = null
    let coverage = null
    if (mode === 'coverage-on') {
      try {
        const target = path.join(resultsDirectory, 'focused.cobertura.xml')
        copyCoberturaReport({resultsDirectory, target, label: 'focused-mode-parity', sourceRoot: apiRoot})
        coverage = coverageSummaryFromFile(target)
        coverageDigest = `sha256:${createHash('sha256').update(readFileSync(target)).digest('hex')}`
      } catch { /* Missing collector output is rejected after both modes execute. */ }
    } else if (coberturaFiles(resultsDirectory).length) coverageDigest = 'unexpected'
    receipts.push({schemaVersion: 1, mode, ...expected, planDigest: plan.planDigest, scopeDigest,
      executed: result?.status !== null && result?.status !== undefined, exitCode: result?.status ?? null,
      counts: trx.counts, results: trx.results, coverageDigest, coverage})
  }
  writeFileSync(path.join(directory, 'receipts.json'), `${JSON.stringify({plan, expected, receipts}, null, 2)}\n`, 'utf8')
  if (git('status', '--porcelain') || git('rev-parse', 'HEAD') !== commit || git('rev-parse', 'HEAD^{tree}') !== tree
    || sdk() !== expected.sdk || dependencies() !== expected.dependencies) fail('inputs-changed', `source/toolchain/feed changed during modes; evidence ${directory}`)
  const verdict = validateFocusedModeEvidence(plan, expected, receipts)
  console.log(`focused-mode parity: ${verdict.status}; ${directory}`)
  return verdict
}

if (process.argv[1]?.replaceAll('\\', '/').endsWith('/eng/focused-mode-policy.mjs')) {
  try { executeFocusedModes() }
  catch (error) { console.error(error.message); process.exitCode = 1 }
}
