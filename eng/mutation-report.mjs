#!/usr/bin/env node
// T-236 row 5: Stryker.NET over every .NET test project.
//
//   node eng/mutation-report.mjs --check   every test project has a config (or an EXCLUDED reason) whose
//                                          project is one of its ProjectReferences, and a break no lower
//                                          than its recorded baseline
//   node eng/mutation-report.mjs           PR mode (owner ruling Q43, option 2): since origin/main, for
//                                          each project with changed .cs files. Survived and NoCoverage
//                                          mutants on changed lines are review feedback, never a failure;
//                                          it fails only when mutable code changed and no mutant was tested
//   node eng/mutation-report.mjs --full    scheduled mode: every configured project on the whole tree;
//                                          fails when a project's score is below its break
//
// Stryker's exit code is not evidence: under .NET 11 RC1 it has exited 0 having mutated nothing
// (T-720), and in a linked worktree since-mode marks every mutant Ignored and exits 0. So a run only
// passes when its json report exists, names every changed file, and has tested at least one mutant.
import {execFileSync, spawnSync} from 'node:child_process'
import {appendFileSync, existsSync, readFileSync, rmSync} from 'node:fs'
import path from 'node:path'

const root = path.resolve(import.meta.dirname, '..')
const base = 'origin/main'

// Test projects Stryker does not run, each with its one-line reason.
export const EXCLUDED = {
  'packages/contracts/tests/Harborline.Contracts.Tests.csproj':
    'its only C# source is codegen output (Generated/HarborlineProtocol.g.cs), which Stryker never mutates; codegen --check guards it',
}

// Thresholds. PROC-0002 ("Mutation evidence as an artefact") sets the target: low 60, high 80. Owner
// ruling 2026-09-26: each project's break starts at the floor of its recorded baseline score and is
// only raised, as test-improvement tickets land. Stryker refuses break > low, so a project whose
// baseline is above 60 carries low = break. Only the scheduled full run holds a project to its break.
export const BASELINE_FILE = 'eng/baselines/mutation-baseline.json'
const TARGET = {high: 80, low: 60}
export function thresholdsFor(breakAt) {
  return {high: TARGET.high, low: Math.max(TARGET.low, breakAt), break: breakAt}
}

// Q44 (option 3): the host is too big for one night, so it is mutated one slice a night.
// SLICES_FILE lists the slices in rotation order, silent-failure (T-719) areas first. Membership is
// first-match: a slice's Stryker `mutate` list is its own globs plus every earlier slice's globs
// negated, so the checker proves the partition against exactly the lists Stryker receives.
// Globs are matched against paths relative to the project directory; `**/` may match nothing,
// `[A-C]` is a character class, and matching is case-sensitive, as Stryker's globbing is.
export const SLICES_FILE = 'eng/baselines/mutation-slices.json'

export function globRegex(glob) {
  let source = ''
  for (let i = 0; i < glob.length; i++) {
    if (glob.startsWith('**/', i)) { source += '(?:.*/)?'; i += 2 } else if (glob.startsWith('**', i)) { source += '.*'; i++ }
    else if (glob[i] === '*') source += '[^/]*'
    else if (glob[i] === '?') source += '[^/]'
    else if (glob[i] === '[' && glob.indexOf(']', i) > i) { const end = glob.indexOf(']', i); source += glob.slice(i, end + 1); i = end }
    else source += glob[i].replace(/[.+^${}()|[\]\\]/g, '\\$&')
  }
  return new RegExp(`^${source}$`)
}

export function sliceMutate(slices, index) {
  return [...slices[index].include, ...slices.slice(0, index).flatMap(slice => slice.include).map(glob => `!${glob}`)]
}

export function inMutate(file, patterns) {
  const test = glob => globRegex(glob).test(file)
  return patterns.some(glob => !glob.startsWith('!') && test(glob)) && !patterns.some(glob => glob.startsWith('!') && test(glob.slice(1)))
}

// The C# a project compiles, relative to its directory: tracked .cs under it minus <Compile Remove>,
// plus <Compile Include> links from outside it.
export function projectSources(csproj, tracked) {
  const attributes = kind => [...csproj.matchAll(new RegExp(`<Compile\\s+${kind}="([^"]+)"`, 'g'))].map(match => match[1].replaceAll('\\', '/'))
  const removed = attributes('Remove')
  return [...tracked.filter(file => file.endsWith('.cs') && !removed.some(glob => globRegex(glob).test(file))),
    ...attributes('Include').filter(file => file.startsWith('../'))]
}

export function checkSlices(definition, sources, baselines) {
  const errors = []
  const {slices} = definition
  const names = slices.map(slice => slice.name)
  if (new Set(names).size !== names.length) errors.push(`${SLICES_FILE}: duplicate slice names`)
  const firstPlain = slices.findIndex(slice => !slice.silentFailure)
  if (firstPlain >= 0 && slices.slice(firstPlain).some(slice => slice.silentFailure)) errors.push(`${SLICES_FILE}: silent-failure slices must come first in the rotation`)
  const mutates = slices.map((_, index) => sliceMutate(slices, index))
  const members = slices.map(() => 0)
  for (const file of sources) {
    const owners = mutates.flatMap((patterns, index) => inMutate(file, patterns) ? [index] : [])
    if (!owners.length) errors.push(`${file}: in no mutation slice`)
    if (owners.length > 1) errors.push(`${file}: in more than one mutation slice (${owners.map(index => names[index]).join(', ')})`)
    for (const index of owners) members[index]++
  }
  slices.forEach((slice, index) => { if (!members[index]) errors.push(`${SLICES_FILE}: slice ${slice.name} has no files`) })
  for (const name of names) {
    const baseline = baselines?.[name]
    if (baseline?.status === 'pending') {
      if (baseline.score !== undefined) errors.push(`${BASELINE_FILE}: slice ${name} has a score, so it is no longer pending: drop "status"`)
      continue
    }
    if (typeof baseline?.score !== 'number') errors.push(`${BASELINE_FILE}: slice ${name} needs {"status": "pending"} or a measured score`)
    else if (!(baseline.break >= Math.floor(baseline.score))) errors.push(`${BASELINE_FILE}: slice ${name} break ${baseline.break} is below the baseline ${Math.floor(baseline.score)}`)
  }
  for (const name of Object.keys(baselines ?? {})) if (!names.includes(name)) errors.push(`${BASELINE_FILE}: slice ${name} is not in ${SLICES_FILE}`)
  return {errors, members}
}

// The nightly pick: days since the epoch (UTC) modulo the slice count, so consecutive nights walk the
// rotation in order with no year-end skip.
export function pickSlice(slices, date) {
  return slices[Math.floor(date.getTime() / 86_400_000) % slices.length]
}

export function checkConfigs(csprojFiles, read, exists, excluded = EXCLUDED) {
  const errors = []
  const runs = []
  const tests = csprojFiles.filter(file => read(file).includes('Microsoft.NET.Test.Sdk'))
  const baselines = exists(BASELINE_FILE) ? JSON.parse(read(BASELINE_FILE)).projects ?? {} : {}
  for (const key of Object.keys(excluded)) if (!tests.includes(key)) errors.push(`${key}: excluded names no test project`)
  for (const key of Object.keys(baselines)) if (!tests.includes(key) || key in excluded) errors.push(`${key}: ${BASELINE_FILE} names no configured test project`)
  for (const test of tests) {
    const dir = path.posix.dirname(test)
    const configFile = `${dir}/stryker-config.json`
    if (test in excluded) {
      if (!excluded[test]?.trim()) errors.push(`${test}: exclusion has no reason`)
      if (exists(configFile)) errors.push(`${test}: both excluded and configured`)
      continue
    }
    if (!exists(configFile)) { errors.push(`${test}: no ${configFile} and no excluded reason`); continue }
    const config = JSON.parse(read(configFile))['stryker-config'] ?? {}
    const references = [...read(test).matchAll(/<ProjectReference\s+Include="([^"]+)"/g)]
      .map(match => path.posix.normalize(`${dir}/${match[1].replaceAll('\\', '/')}`))
    const project = references.find(reference => path.posix.basename(reference) === config.project)
    if (!project) errors.push(`${configFile}: project "${config.project}" is not a ProjectReference of ${test}`)
    else if (!exists(project)) errors.push(`${configFile}: project ${project} does not exist`)
    const baseline = baselines[test]
    const breakAt = config.thresholds?.break
    if (baseline?.fullRun && baseline.score === null) {
      // No whole-project score to hold it to (the reason is recorded); the full run skips it.
      if (breakAt !== 0) errors.push(`${configFile}: no measured baseline (${baseline.fullRun}), so break must be 0`)
    } else if (typeof baseline?.score !== 'number') errors.push(`${test}: no baseline score in ${BASELINE_FILE}`)
    else if (!(breakAt >= Math.floor(baseline.score))) errors.push(`${configFile}: break ${breakAt} is below the baseline ${Math.floor(baseline.score)}`)
    if (JSON.stringify(config.thresholds) !== JSON.stringify(thresholdsFor(breakAt)))
      errors.push(`${configFile}: thresholds must be ${JSON.stringify(thresholdsFor(breakAt))}`)
    if (!config.reporters?.includes('json')) errors.push(`${configFile}: the json reporter is required`)
    // The runner turns since on in PR mode (--since:origin/main); the config carries only its exclusions.
    if (config.since?.enabled) errors.push(`${configFile}: since is enabled by the runner in PR mode, not in the config`)
    if (project) runs.push({test, dir, project})
  }
  return {errors, runs}
}

// Changed .cs files Stryker could mutate for `project`: under its directory, outside its tests.
export function mutableChanges(changed, {dir, project}) {
  const source = path.posix.dirname(project)
  return changed.filter(file => file.endsWith('.cs') && file.startsWith(`${source}/`) && !file.startsWith(`${dir}/`)
    && !/\/(bin|obj)\//.test(file))
}

// `git diff -U0` -> {file: [[firstLine, lastLine], ...]} for the new side of each hunk.
export function changedLines(diff) {
  const lines = {}
  let file
  for (const line of diff.split(/\r?\n/)) {
    if (line.startsWith('+++ ')) file = line.startsWith('+++ b/') ? line.slice(6) : undefined
    const hunk = file && /^@@ -\S+ \+(\d+)(?:,(\d+))? @@/.exec(line)
    if (hunk && hunk[2] !== '0') (lines[file] ??= []).push([Number(hunk[1]), Number(hunk[1]) + Number(hunk[2] ?? 1) - 1])
  }
  return lines
}

function statusCounts(mutants) {
  const counts = {}
  for (const {status} of mutants) counts[status] = (counts[status] ?? 0) + 1
  const n = status => counts[status] ?? 0
  const detected = n('Killed') + n('Timeout')
  const covered = detected + n('Survived') + n('NoCoverage')
  return {counts, tested: detected + n('Survived'), generated: mutants.length,
    score: covered ? Math.round(10000 * detected / covered) / 100 : null}
}

// mutation-testing-report-schema: files keyed by path, each with mutants[] {status, mutatorName,
// replacement, location}. `changed` null means the whole report (full mode); otherwise only those
// files count, since since-mode reports every other file's mutants as Ignored. `lines` narrows the
// survivors listed for review to the changed lines.
export function summarise(report, changed = null, lines = {}) {
  const entries = Object.entries(report.files ?? {}).map(([file, value]) => [file.replaceAll('\\', '/'), value])
  const matches = (entry, file) => entry === file || entry.endsWith(`/${file}`)
  const missing = (changed ?? []).filter(file => !entries.some(([entry]) => matches(entry, file)))
  const mutants = []
  const survivors = []
  for (const [entry, {mutants: fileMutants = []}] of entries) {
    const file = changed ? changed.find(candidate => matches(entry, candidate)) : entry
    if (!file) continue
    mutants.push(...fileMutants)
    for (const mutant of fileMutants) {
      const line = mutant.location?.start?.line
      if ((mutant.status === 'Survived' || mutant.status === 'NoCoverage') && lines[file]?.some(([first, last]) => line >= first && line <= last))
        survivors.push({file, line, status: mutant.status, mutator: mutant.mutatorName, replacement: mutant.replacement})
    }
  }
  return {...statusCounts(mutants), missing, survivors}
}

function msbuildArguments() {
  // stryker-net#3758: a TargetFramework inherited from Directory.Build.props is invisible to
  // Buildalyzer, which then treats the project as .NET Framework and runs Visual Studio Build Tools'
  // MSBuild.exe; that MSBuild has no .NET SDK resolver, so analysis fails. Name the pinned SDK's.
  if (process.platform !== 'win32') return []
  const version = execFileSync('dotnet', ['--version'], {cwd: root, encoding: 'utf8'}).trim()
  const line = execFileSync('dotnet', ['--list-sdks'], {cwd: root, encoding: 'utf8'}).split(/\r?\n/)
    .find(entry => entry.startsWith(`${version} [`))
  if (!line) throw new Error(`dotnet --list-sdks does not list the pinned SDK ${version}`)
  return ['--msbuild-path', path.join(line.slice(line.indexOf('[') + 1, -1), version, 'MSBuild.exe')]
}

function stryker(run, extra) {
  const output = path.join(root, '.stryker', path.basename(run.test, '.csproj'))
  rmSync(output, {recursive: true, force: true})
  const result = spawnSync('dotnet', ['tool', 'run', 'dotnet-stryker', '--', ...msbuildArguments(), '--output', output, ...extra],
    {cwd: path.join(root, run.dir), stdio: 'inherit'})
  const reportFile = path.join(output, 'reports', 'mutation-report.json')
  return {status: result.status, report: existsSync(reportFile) ? JSON.parse(readFileSync(reportFile, 'utf8')) : null,
    reportFile: path.relative(root, reportFile).replaceAll('\\', '/')}
}

const cell = value => String(value ?? '').replaceAll('|', '\\|').replaceAll('\n', ' ').slice(0, 80)

function main() {
  const git = (...args) => execFileSync('git', args, {cwd: root, encoding: 'utf8', maxBuffer: 1 << 28}).trim()
  const read = file => readFileSync(path.join(root, file), 'utf8')
  const files = git('ls-files', '*.csproj').split('\n').filter(Boolean)
  const {errors, runs} = checkConfigs(files, read, file => existsSync(path.join(root, file)))
  for (const error of errors) console.error(`FAIL ${error}`)
  const baselineFile = JSON.parse(read(BASELINE_FILE))
  const definition = JSON.parse(read(SLICES_FILE))
  const sliced = runs.find(run => run.test === definition.project)
  if (!sliced) errors.push(`${SLICES_FILE}: project ${definition.project} is not a configured test project`)
  else {
    const tracked = git('ls-files', '--', path.posix.dirname(sliced.project)).split('\n').filter(Boolean)
      .map(file => path.posix.relative(path.posix.dirname(sliced.project), file))
    const {errors: sliceErrors, members} = checkSlices(definition, projectSources(read(sliced.project), tracked), baselineFile.slices)
    errors.push(...sliceErrors)
    if (process.argv.includes('--check')) definition.slices.forEach((slice, index) => console.log(`slice ${index + 1} ${slice.name}: ${members[index]} files`))
  }
  for (const error of errors) console.error(`FAIL ${error}`)
  if (errors.length) process.exit(1)
  if (process.argv.includes('--check')) { console.log(`mutation configs: ${runs.length} configured, ${Object.keys(EXCLUDED).length} excluded, ${definition.slices.length} host slices`); return }

  const full = process.argv.includes('--full')
  const argument = name => { const index = process.argv.indexOf(name); return index > 0 ? process.argv[index + 1] : undefined }
  const only = argument('--only')
  const baselines = baselineFile.projects
  const rows = ['| Test project | Changed files | Tested | Killed | Survived | No coverage | Score | Break |',
    '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |']
  const feedback = []
  let failed = false
  const fail = message => { console.error(`FAIL ${message}`); failed = true }

  if (!full && path.resolve(root, git('rev-parse', '--git-dir')) !== path.resolve(root, git('rev-parse', '--git-common-dir'))) {
    fail('linked worktree: Stryker since-mode diffs the main checkout and ignores every mutant. Run from a plain clone.')
    process.exit(1)
  }
  const changed = full ? [] : git('diff', '--name-only', `${base}...HEAD`).split('\n').filter(Boolean)
  const lines = full ? {} : changedLines(git('diff', '-U0', `${base}...HEAD`, '--', '*.cs'))

  for (const run of runs) {
    if (only && run.test !== only) continue
    let breakAt = JSON.parse(read(`${run.dir}/stryker-config.json`))['stryker-config'].thresholds.break
    if (full) {
      let extra = []
      let label = run.test
      if (run === sliced) {
        // One slice a night; --slice <name> picks one by hand (workflow_dispatch).
        const slice = argument('--slice') ? definition.slices.find(s => s.name === argument('--slice')) : pickSlice(definition.slices, new Date())
        if (!slice) { fail(`no slice named ${argument('--slice')}`); continue }
        const baseline = baselineFile.slices[slice.name]
        const pending = baseline.status === 'pending'
        breakAt = pending ? 0 : baseline.break
        label = `${run.test} slice ${slice.name}${pending ? ' (pending: recorded, not enforced)' : ''}`
        extra = ['--break-at', '0', ...sliceMutate(definition.slices, definition.slices.indexOf(slice)).flatMap(glob => ['--mutate', glob])]
      } else if (baselines[run.test]?.fullRun) { rows.push(`| ${run.test} | | skipped: ${cell(baselines[run.test].fullRun)} | | | | | |`); continue }
      const started = Date.now()
      const {status, report, reportFile} = stryker(run, extra)
      console.log(`${label}: ${Math.round((Date.now() - started) / 60000)} min`)
      if (!report) { fail(`${label}: no json report (Stryker exited ${status})`); continue }
      const {counts, tested, score} = summarise(report)
      rows.push(`| ${label} | | ${tested} | ${counts.Killed ?? 0} | ${counts.Survived ?? 0} | ${counts.NoCoverage ?? 0} | ${score ?? 'n/a'} | ${breakAt} |`)
      console.log(`${label}: ${reportFile}`)
      if (!tested) fail(`${label}: no mutant tested ${JSON.stringify(counts)}`)
      else if (score < breakAt) fail(`${label}: score ${score} is below its break ${breakAt} (${BASELINE_FILE})`)
      continue
    }
    const mutable = mutableChanges(changed, run)
    if (!mutable.length) { rows.push(`| ${run.test} | 0 | skipped | | | | | |`); continue }
    // --break-at 0: a PR is never held to its project's floor (Q43); the scheduled full run is.
    const {status, report, reportFile} = stryker(run, [`--since:${base}`, '--break-at', '0'])
    if (!report) { fail(`${run.test}: no json report (Stryker exited ${status})`); continue }
    const {counts, tested, generated, score, missing, survivors} = summarise(report, mutable, lines)
    rows.push(`| ${run.test} | ${mutable.length} | ${tested} | ${counts.Killed ?? 0} | ${counts.Survived ?? 0} | ${counts.NoCoverage ?? 0} | ${score ?? 'n/a'} | |`)
    console.log(`${run.test}: ${reportFile}`)
    if (missing.length) fail(`${run.test}: changed files absent from the report: ${missing.join(', ')}`)
    // A changed file with no mutable code yields no mutants; mutants that all went untested do not pass.
    if (generated && !tested) fail(`${run.test}: mutable code changed but no mutant was tested ${JSON.stringify(counts)}`)
    for (const s of survivors) feedback.push(`| ${s.file}:${s.line} | ${s.status} | ${s.mutator} | \`${cell(s.replacement)}\` |`)
  }
  let summary = `${full ? `### Mutation (full run; break from ${BASELINE_FILE})` : `### Mutation (since ${base})`}\n\n${rows.join('\n')}\n`
  if (!full) summary += feedback.length
    ? `\nSurviving and uncovered mutants on changed lines (review feedback, not a failure):\n\n| Line | Status | Mutator | Replacement |\n| --- | --- | --- | --- |\n${feedback.join('\n')}\n`
    : '\nNo surviving or uncovered mutant on a changed line.\n'
  console.log(summary)
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary)
  if (failed) process.exit(1)
}

if (process.argv[1]?.replaceAll('\\', '/').endsWith('eng/mutation-report.mjs')) main()
