#!/usr/bin/env node
// StrykerJS over the JavaScript/TypeScript test suites (T-720; PROC-0002 thresholds 80/60/60, json report).
//
//   node eng/strykerjs.mjs check
//       Every test package (a directory with a vitest/jest/karma/playwright config, or a package.json
//       with a test* script) has a run entry or an exclusion below. A new suite cannot be silently skipped.
//   node eng/strykerjs.mjs run [--base <ref>] [--all] [<package dir>...]
//       StrykerJS has no `--since`. The equivalent used here: the files changed since the merge base with
//       <ref> (default origin/main), filtered to the package's mutable source, are passed as `--mutate`.
//       --all mutates every mutable file instead. A package with no changed mutable source is skipped.
//
// The exit code is not trusted. Stryker exits 0 after a run that mutated nothing (the T-711 "passed having
// run nothing" shape), so a run passes only when mutation.json exists and records at least one tested
// mutant (Killed, Survived or Timeout). The one sanctioned empty run is Stryker's own "with 0 mutant(s)"
// instrumentation line: the changed files hold no runtime code (a types-only file), which is a skip.
//
// Kept separate from the Stryker.NET report checker on purpose; the two could merge later.
import {spawnSync} from 'node:child_process'
import {existsSync, readFileSync} from 'node:fs'
import path from 'node:path'

const root = path.resolve(import.meta.dirname, '..')
const config = path.join(import.meta.dirname, 'strykerjs.config.json')

// `dir/*` covers every direct child directory. `toolchain` is where @stryker-mutator and vitest are installed.
export const PACKAGES = [
  {path: 'apps/capability-host', toolchain: 'apps/capability-host'},
  {path: 'packages/contracts', toolchain: 'packages/contracts', exclude: 'vitest 5.0.1: the StrykerJS 10.0.0 vitest runner (built on vitest 4.1) ran zero tests for 2421 of 2498 covered mutants; re-enable when it supports vitest 5'},
]

const TESTED = new Set(['Killed', 'Survived', 'Timeout'])
const MUTABLE = /^src\/.*\.(?:ts|tsx|js|jsx|mts|mjs)$/
const NOT_MUTABLE = /(?:^|\/)__tests__\/|\.test\.[^/]+$|\.d\.ts$|(?:^|\/)test-setup\.[^/]+$/

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

export function mutableFiles(dir, files) {
  const prefix = dir === '.' ? '' : `${dir}/`
  return files.filter(file => file.startsWith(prefix)).map(file => file.slice(prefix.length)).filter(isMutable)
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

// verdict for one package run: {ok, message}
export function verdict({status, output, report}) {
  if (/Instrumented \d+ source file\(s\) with 0 mutant\(s\)/.test(output)) return {ok: true, message: 'changed files hold no mutable code (0 mutants generated); skipped'}
  if (!report) return {ok: false, message: `no mutation.json written (stryker exit ${status})`}
  const {tested, score, counts} = tally(report)
  if (tested === 0) return {ok: false, message: `0 mutants tested (${JSON.stringify(counts)}); a run that tests nothing is not a pass`}
  if (counts.SurvivedNoTestsRan) return {ok: false, message: `${counts.SurvivedNoTestsRan} covered mutants ran zero tests: the test runner is not executing mutant runs (${JSON.stringify(counts)})`}
  if (status !== 0) return {ok: false, message: `stryker exit ${status}: score ${score} under break 60, or a run error (${tested} tested)`}
  return {ok: true, message: `${tested} tested, score ${score} ${JSON.stringify(counts)}`}
}

const git = (...args) => spawnSync('git', args, {cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024})
const lines = text => text.split(/\r?\n/).filter(Boolean)

function runPackage(dir, entry, files) {
  const mutate = mutableFiles(dir, files)
  if (!mutate.length) return {ok: true, message: 'no changed mutable source; skipped'}
  const cwd = path.join(root, dir)
  const bin = path.join(root, entry.toolchain, 'node_modules/@stryker-mutator/core/bin/stryker.js')
  if (!existsSync(bin)) return {ok: false, message: `StrykerJS is not installed in ${entry.toolchain}; run pnpm install --frozen-lockfile there`}
  const before = mutate.map(file => readFileSync(path.join(cwd, file), 'utf8'))
  const status = () => git('status', '--porcelain', '--untracked-files=all', '--', dir).stdout
  const statusBefore = status()
  const run = spawnSync(process.execPath, [bin, 'run', path.relative(cwd, config), '--mutate', mutate.join(',')], {cwd, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024})
  const output = `${run.stdout ?? ''}${run.stderr ?? ''}`
  process.stdout.write(output)
  const reportPath = path.join(cwd, 'reports/mutation/mutation.json')
  const report = existsSync(reportPath) ? JSON.parse(readFileSync(reportPath, 'utf8')) : undefined
  // inPlace mode edits the working tree; Stryker restores it, and this refuses a run that did not.
  const dirty = mutate.filter((file, index) => readFileSync(path.join(cwd, file), 'utf8') !== before[index])
  if (dirty.length) return {ok: false, message: `source left mutated after the in-place run: ${dirty.join(', ')}`}
  // A mutant with a side effect (one wrote a file named "-s" into the api's capability-host) lands in the real tree in place.
  const strays = lines(status()).filter(line => !lines(statusBefore).includes(line))
  if (strays.length) return {ok: false, message: `the run left files in the working tree: ${strays.join('; ')}`}
  return verdict({status: run.status, output, report})
}

if (import.meta.main) {
  const [command, ...rest] = process.argv.slice(2)
  const tracked = lines(git('ls-files').stdout)
  const dirs = testPackages(tracked)
  const problems = gaps(dirs)
  for (const problem of problems) console.log(`FAIL ${problem}`)
  if (command === 'check') {
    for (const dir of dirs) { const entry = entryFor(dir); if (entry) console.log(`ok ${dir}: ${entry.exclude ? `excluded (${entry.exclude})` : `mutated with ${entry.toolchain}`}`) }
    process.exit(problems.length ? 1 : 0)
  }
  if (command !== 'run') { console.error('usage: node strykerjs.mjs check | run [--base <ref>] [--all] [<package dir>...]'); process.exit(2) }
  const baseIndex = rest.indexOf('--base')
  const base = baseIndex >= 0 ? rest[baseIndex + 1] : 'origin/main'
  const all = rest.includes('--all')
  const chosen = rest.filter((arg, index) => !arg.startsWith('--') && rest[index - 1] !== '--base')
  let files = tracked
  if (!all) {
    const mergeBase = git('merge-base', base, 'HEAD')
    if (mergeBase.status !== 0) { console.error(`cannot find the merge base with ${base}: ${mergeBase.stderr}`); process.exit(1) }
    files = lines(git('diff', '--name-only', '--diff-filter=ACMR', mergeBase.stdout.trim(), '--').stdout)
  }
  const targets = (chosen.length ? chosen : dirs).filter(dir => entryFor(dir) && !entryFor(dir).exclude)
  let failed = problems.length > 0
  for (const dir of targets) {
    const result = runPackage(dir, entryFor(dir), files)
    console.log(`${result.ok ? 'ok' : 'FAIL'} strykerjs ${dir}: ${result.message}`)
    failed ||= !result.ok
  }
  process.exit(failed ? 1 : 0)
}
