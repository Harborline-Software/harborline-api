#!/usr/bin/env node
// StrykerJS over the JavaScript/TypeScript test suites (T-720; PROC-0002 high/low 80/60, json report).
//
//   node eng/strykerjs.mjs check
//       Every test package (a directory with a vitest/jest/karma/playwright config, or a package.json with a
//       test* script) has a run entry or an exclusion below, and every mutated package has a measured baseline.
//   node eng/strykerjs.mjs run [--base <ref>] [<package dir>...]
//       Pull-request mode. StrykerJS has no `--since`; the equivalent here is the lines changed since the merge
//       base with <ref> (default origin/main), passed as `--mutate file:start-end` ranges over each package's
//       mutable source. The surviving and NoCoverage mutants on those lines are reported as review feedback
//       (stdout, and $GITHUB_STEP_SUMMARY when set). The package's break is NOT compared (owner ruling Q43).
//   node eng/strykerjs.mjs run --all [--shard <k>/<n>] [<package dir>...]
//       Full mode, for the scheduled run: every mutable file, and each package's score must reach its break.
//
// The exit code is not trusted. Stryker exits 0 after a run that mutated nothing (the T-711 "passed having run
// nothing" shape), so a run passes only when mutation.json exists, records at least one tested mutant (Killed,
// Survived or Timeout), and no covered mutant completed zero tests. The one sanctioned empty run is Stryker's own
// "with 0 mutant(s)" instrumentation line: the changed lines hold no runtime code. A run that leaves source
// mutated or files behind fails in both modes.
//
// Break is per package and lives in strykerjs.baselines.json (the Stryker config's break is null): owner ruling
// 2026-09-26, each package's break starts at the floor of its measured baseline and is raised as tests improve.
// `check` refuses a missing entry, a break below its baseline, and a `pending` entry (not yet measured); a full
// run reports a pending package's score without enforcing it.
//
// The sandbox sits in <repo>/.stryker-tmp/.../sandbox-*, at the package's own depth (sandboxDir), so a path that
// climbs from the source to the repo root by import.meta.url (the capability-host authority registry reads
// ../../../../packages/harborline-sdk) still reaches the real repo. Stryker's default .stryker-tmp inside the
// package puts the sandbox two levels deeper and that lookup fails the initial test run while plain vitest passes.
//
// The silent-failure blocking rule (a survivor on a changed risk: silent line fails the PR) waits on T-719.
// Kept separate from the Stryker.NET report checker on purpose; the two could merge later.
import {spawnSync} from 'node:child_process'
import {appendFileSync, existsSync, readFileSync, readdirSync, rmSync} from 'node:fs'
import path from 'node:path'

const root = path.resolve(import.meta.dirname, '..')
const config = path.join(import.meta.dirname, 'strykerjs.config.json')
export const BASELINES = JSON.parse(readFileSync(path.join(import.meta.dirname, 'strykerjs.baselines.json'), 'utf8'))

// `dir/*` covers every direct child directory; an exact entry wins over it. `toolchain` is where @stryker-mutator
// and vitest are installed. `testFiles`, when set, replaces the config's test-file glob for that package.
export const PACKAGES = [
  {path: 'apps/capability-host', toolchain: 'apps/capability-host'},
  {path: 'packages/contracts', toolchain: 'packages/contracts'},
]

const TESTED = new Set(['Killed', 'Survived', 'Timeout'])
const FEEDBACK = new Set(['Survived', 'NoCoverage'])
const MUTABLE = /^src\/.*\.(?:ts|tsx|js|jsx|mts|mjs)$/
const NOT_MUTABLE = /(?:^|\/)__tests__\/|\.test\.[^/]+$|\.d\.ts$|(?:^|\/)test-setup\.[^/]+$/

// The temp dir, relative to the package, that puts sandbox-* at the package's depth under <repo>/.stryker-tmp.
export function sandboxDir(dir) {
  const depth = dir.split('/').length
  if (dir === '.' || depth < 2) throw new Error(`${dir}: a sandbox at the package's depth needs a package at least two levels deep`)
  return path.posix.join(...Array(depth).fill('..'), '.stryker-tmp', ...Array(depth - 2).fill('nest'))
}

export const isMutable = relative => MUTABLE.test(relative) && !NOT_MUTABLE.test(relative)

export function testPackages(files) {
  const dirs = new Set()
  for (const file of files) {
    const dir = path.posix.dirname(file)
    if (dir.startsWith('tests/package-consumers/')) continue
    const base = path.posix.basename(file)
    if (/^(?:vitest|jest|playwright)\.config\.[cm]?[jt]s$|^karma\.conf\.[cm]?[jt]s$/.test(base)) dirs.add(dir)
    else if (base === 'package.json') {
      const scripts = JSON.parse(readFileSync(path.join(root, file), 'utf8')).scripts ?? {}
      if (Object.keys(scripts).some(name => /^test(?::|$)/.test(name))) dirs.add(dir)
    }
  }
  return [...dirs].sort()
}

export function entryFor(dir, packages = PACKAGES) {
  return packages.find(entry => entry.path === dir)
    ?? packages.find(entry => entry.path.endsWith('/*') && path.posix.dirname(dir) === entry.path.slice(0, -2))
}

// Test packages with no entry, and exact entries that name no test package (a stale exclusion is a gap too).
export function gaps(dirs, packages = PACKAGES) {
  const missing = dirs.filter(dir => !entryFor(dir, packages)).map(dir => `${dir}: no StrykerJS entry or exclusion`)
  const stale = packages.filter(entry => !entry.path.endsWith('/*') && !dirs.includes(entry.path)).map(entry => `${entry.path}: entry names no test package`)
  return [...missing, ...stale]
}

// `git diff -U0` output -> Map(repo path -> [[start, end], ...]) of added or changed lines on the new side.
export function changedRanges(diff) {
  const ranges = new Map()
  let file
  for (const line of diff.split(/\r?\n/)) {
    if (line.startsWith('+++ ')) file = line === '+++ /dev/null' ? undefined : line.slice(6)
    const hunk = /^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@/.exec(line)
    if (hunk && file) {
      const start = Number(hunk[1])
      const count = hunk[2] === undefined ? 1 : Number(hunk[2])
      if (count > 0) ranges.set(file, [...(ranges.get(file) ?? []), [start, start + count - 1]])
    }
  }
  return ranges
}

// --mutate entries for one package: whole files (ranges undefined, full mode) or the changed ranges.
export function mutateEntries(dir, files, ranges) {
  const prefix = dir === '.' ? '' : `${dir}/`
  const entries = []
  for (const file of files) {
    if (!file.startsWith(prefix) || !isMutable(file.slice(prefix.length))) continue
    const relative = file.slice(prefix.length)
    if (!ranges) entries.push(relative)
    else for (const [start, end] of ranges.get(file) ?? []) entries.push(`${relative}:${start}-${end}`)
  }
  return entries
}

export function tally(report) {
  const counts = {}
  for (const file of Object.values(report?.files ?? {})) {
    for (const mutant of file.mutants ?? []) {
      // A Survived mutant whose run completed zero tests was never tested: the per-mutant form of "ran nothing".
      const status = mutant.status === 'Survived' && mutant.testsCompleted === 0 ? 'SurvivedNoTestsRan' : mutant.status
      counts[status] = (counts[status] ?? 0) + 1
    }
  }
  const tested = [...TESTED].reduce((sum, status) => sum + (counts[status] ?? 0), 0)
  const detected = (counts.Killed ?? 0) + (counts.Timeout ?? 0)
  const valid = tested + (counts.NoCoverage ?? 0) + (counts.SurvivedNoTestsRan ?? 0)
  return {counts, tested, score: valid ? Math.round((detected / valid) * 10000) / 100 : null}
}

// The review feedback: every Survived or NoCoverage mutant, as file:line, mutator and replacement.
export function feedback(report) {
  const rows = []
  for (const [file, {mutants = []}] of Object.entries(report?.files ?? {})) {
    for (const mutant of mutants) {
      if (FEEDBACK.has(mutant.status)) rows.push({file, line: mutant.location?.start?.line, status: mutant.status, mutator: mutant.mutatorName, replacement: mutant.replacement})
    }
  }
  return rows.sort((a, b) => a.file.localeCompare(b.file) || a.line - b.line)
}

// The break a run is held to: only a full run, and never a pending package's. null means not compared.
export const effectiveBreak = (dir, {full}, baselines = BASELINES) => {
  const entry = baselines.packages?.[dir]
  return full && entry && !entry.pending && typeof entry.break === 'number' ? entry.break : null
}

// {problems, pending}: problems always fail; pending entries fail `check` but let a full run measure them.
export function baselineProblems(dirs, baselines = BASELINES, packages = PACKAGES) {
  const problems = []
  const pending = []
  const recorded = baselines.packages ?? {}
  for (const dir of dirs) {
    const owner = entryFor(dir, packages)
    if (owner && !owner.exclude && !recorded[dir]) problems.push(`${dir}: no baseline in strykerjs.baselines.json`)
  }
  for (const [dir, entry] of Object.entries(recorded)) {
    const owner = entryFor(dir, packages)
    if (!dirs.includes(dir) || !owner || owner.exclude) problems.push(`${dir}: baseline names no mutated test package`)
    else if (entry.pending) pending.push(`${dir}: baseline pending; run the baselines and record it`)
    else if (typeof entry.baseline !== 'number' || typeof entry.break !== 'number') problems.push(`${dir}: baseline and break must both be numbers`)
    else if (entry.break < entry.baseline) problems.push(`${dir}: break ${entry.break} is below the recorded baseline ${entry.baseline}`)
  }
  return {problems, pending}
}

// verdict for one package run: {ok, message}. breakAt null: the score is reported, not compared.
export function verdict({status, output, report, breakAt = null}) {
  if (/Instrumented \d+ source file\(s\) with 0 mutant\(s\)/.test(output)) return {ok: true, message: 'changed lines hold no mutable code (0 mutants generated); skipped'}
  const failedInitial = /One or more tests failed in the initial test run:\r?\n((?:\t.*(?:\r?\n|$))+)/.exec(output)
  if (failedInitial || /There were failed tests in the initial test run/.test(output)) {
    const tests = (failedInitial?.[1] ?? '').split(/\r?\n/).filter(line => /^\t[^\t]/.test(line)).map(line => line.trim())
    return {ok: false, message: `the initial test run failed under Stryker (${tests.join('; ') || 'see the output'}); if plain vitest passes, a test or source reads a file by a path relative to its own location and the sandbox does not reproduce the repo layout (see sandboxDir)`}
  }
  if (!report) return {ok: false, message: `no mutation.json written (stryker exit ${status})`}
  const {tested, score, counts} = tally(report)
  if (tested === 0) return {ok: false, message: `0 mutants tested (${JSON.stringify(counts)}); a run that tests nothing is not a pass`}
  if (counts.SurvivedNoTestsRan) return {ok: false, message: `${counts.SurvivedNoTestsRan} covered mutants ran zero tests: the test runner is not executing mutant runs (${JSON.stringify(counts)})`}
  if (status !== 0) return {ok: false, message: `stryker exit ${status}: a run error (${tested} tested, score ${score})`}
  if (breakAt === null) return {ok: true, message: `${tested} tested, score ${score}, break not compared ${JSON.stringify(counts)}`}
  if (score < breakAt) return {ok: false, message: `score ${score} under break ${breakAt} (${tested} tested) ${JSON.stringify(counts)}`}
  return {ok: true, message: `${tested} tested, score ${score}, break ${breakAt} ${JSON.stringify(counts)}`}
}

export function shard(targets, spec) {
  if (!spec) return targets
  const [k, n] = spec.split('/').map(Number)
  if (!(n >= 1 && k >= 1 && k <= n)) throw new Error(`bad --shard ${spec}; expected k/n with 1 <= k <= n`)
  return targets.filter((_, index) => index % n === k - 1)
}

const git = (...args) => spawnSync('git', args, {cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024})
const lines = text => text.split(/\r?\n/).filter(Boolean)
const summary = text => { if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${text}\n`) }

function runPackage(dir, entry, files, ranges, full) {
  const mutate = mutateEntries(dir, files, ranges)
  if (!mutate.length) return {ok: true, message: `no ${full ? '' : 'changed '}mutable source; skipped`}
  const cwd = path.join(root, dir)
  const bin = path.join(root, entry.toolchain, 'node_modules/@stryker-mutator/core/bin/stryker.js')
  if (!existsSync(bin)) return {ok: false, message: `StrykerJS is not installed in ${entry.toolchain}; run pnpm install --frozen-lockfile there`}
  const sources = [...new Set(mutate.map(item => item.replace(/:\d+-\d+$/, '')))]
  const before = sources.map(file => readFileSync(path.join(cwd, file), 'utf8'))
  // The whole repo: the sandbox now sits at the repo's depth, so a side-effecting mutant that climbs out of it lands
  // in the repo, not in the package.
  const status = () => git('status', '--porcelain', '--untracked-files=all').stdout
  const statusBefore = status()
  // The vitest runner writes stryker-setup-<n>.js into the package and does not always remove it. It is git-ignored,
  // so the stray check below cannot see it, but tooling that walks the directory (the design-review surface digest) can.
  const setupFiles = () => readdirSync(cwd).filter(name => /^stryker-setup-\d+\.js$/.test(name))
  const setupBefore = new Set(setupFiles())
  const args = [bin, 'run', path.relative(cwd, config), '--tempDirName', sandboxDir(dir), '--mutate', mutate.join(','), ...(entry.testFiles ? ['--testFiles', entry.testFiles.join(',')] : [])]
  const run = spawnSync(process.execPath, args, {cwd, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024})
  const output = `${run.stdout ?? ''}${run.stderr ?? ''}`
  for (const name of setupFiles()) if (!setupBefore.has(name)) rmSync(path.join(cwd, name), {force: true})
  process.stdout.write(output)
  const reportPath = path.join(cwd, 'reports/mutation/mutation.json')
  const report = existsSync(reportPath) ? JSON.parse(readFileSync(reportPath, 'utf8')) : undefined
  // inPlace mode edits the working tree; Stryker restores it, and this refuses a run that did not.
  const dirty = sources.filter((file, index) => readFileSync(path.join(cwd, file), 'utf8') !== before[index])
  if (dirty.length) return {ok: false, message: `source left mutated after the in-place run: ${dirty.join(', ')}`}
  // A mutant with a side effect (one wrote a file named "-s" into the api's capability-host) lands in the real tree in place.
  const strays = lines(status()).filter(line => !lines(statusBefore).includes(line))
  if (strays.length) return {ok: false, message: `the run left files in the working tree: ${strays.join('; ')}`}
  const result = verdict({status: run.status, output, report, breakAt: effectiveBreak(dir, {full})})
  return {...result, rows: report && !full ? feedback(report) : []}
}

if (import.meta.main) {
  const [command, ...rest] = process.argv.slice(2)
  const tracked = lines(git('ls-files').stdout)
  const dirs = testPackages(tracked)
  const baseline = baselineProblems(dirs)
  const problems = [...gaps(dirs), ...baseline.problems, ...(command === 'check' ? baseline.pending : [])]
  for (const problem of problems) console.log(`FAIL ${problem}`)
  if (command !== 'check') for (const note of baseline.pending) console.log(`note ${note}`)
  if (command === 'check') {
    for (const dir of dirs) { const entry = entryFor(dir); if (entry) console.log(`ok ${dir}: ${entry.exclude ? `excluded (${entry.exclude})` : `mutated with ${entry.toolchain}, break ${effectiveBreak(dir, {full: true}) ?? 'pending'}`}`) }
    process.exit(problems.length ? 1 : 0)
  }
  if (command !== 'run') { console.error('usage: node eng/strykerjs.mjs check | run [--base <ref>] | run --all [--shard k/n] [<package dir>...]'); process.exit(2) }
  const option = name => { const index = rest.indexOf(name); return index >= 0 ? rest[index + 1] : undefined }
  const full = rest.includes('--all')
  const chosen = rest.filter((arg, index) => !arg.startsWith('--') && !['--base', '--shard'].includes(rest[index - 1]))
  let files = tracked
  let ranges
  if (!full) {
    const base = option('--base') ?? 'origin/main'
    const mergeBase = git('merge-base', base, 'HEAD')
    if (mergeBase.status !== 0) { console.error(`cannot find the merge base with ${base}: ${mergeBase.stderr}`); process.exit(1) }
    ranges = changedRanges(git('diff', '-U0', '--no-color', '--diff-filter=ACMR', mergeBase.stdout.trim(), '--').stdout)
    files = [...ranges.keys()]
  }
  const targets = shard((chosen.length ? chosen : dirs).filter(dir => entryFor(dir) && !entryFor(dir).exclude), option('--shard'))
  summary(`## StrykerJS ${full ? 'full run (each package held to its break)' : 'on changed lines (review feedback; break not compared)'}\n`)
  let failed = problems.length > 0
  for (const dir of targets) {
    const result = runPackage(dir, entryFor(dir), files, ranges, full)
    console.log(`${result.ok ? 'ok' : 'FAIL'} strykerjs ${dir}: ${result.message}`)
    summary(`- ${result.ok ? 'ok' : '**FAIL**'} \`${dir}\`: ${result.message}`)
    if (result.rows?.length) {
      const table = result.rows.slice(0, 200).map(row => `| \`${row.file}:${row.line}\` | ${row.status} | ${row.mutator} | \`${String(row.replacement ?? '').replaceAll('|', '\\|').replaceAll('\n', ' ').slice(0, 80)}\` |`)
      for (const row of result.rows) console.log(`  ${row.status} ${dir}/${row.file}:${row.line} ${row.mutator}`)
      summary(`\n| mutant | status | mutator | replacement |\n| --- | --- | --- | --- |\n${table.join('\n')}${result.rows.length > 200 ? `\n\n${result.rows.length - 200} more in the json artifact.` : ''}\n`)
    }
    failed ||= !result.ok
  }
  process.exit(failed ? 1 : 0)
}
