#!/usr/bin/env node
// T-236 row 5: Stryker.NET over every .NET test project, in diff mode against origin/main.
//
//   node eng/mutation-report.mjs --check     every test project has a config (or an EXCLUDED reason)
//                                            whose project is one of its ProjectReferences
//   node eng/mutation-report.mjs             run Stryker for each test project whose mutated project
//                                            has changed .cs files; fail if a run tested no mutant
//   node eng/mutation-report.mjs --enforce   also fail when Stryker fails its break threshold
//
// Stryker's exit code is not evidence: under .NET 11 RC1 it has exited 0 having mutated nothing
// (T-720), and in a linked worktree since-mode marks every mutant Ignored and exits 0. So a run only
// passes when its json report exists, names every changed file, and has tested at least one mutant.
import {execFileSync, spawnSync} from 'node:child_process'
import {appendFileSync, existsSync, readFileSync, rmSync} from 'node:fs'
import path from 'node:path'

const root = path.resolve(import.meta.dirname, '..')
const base = 'origin/main'

// Test projects Stryker does not run, each with its one-line reason. Empty: every one has a config.
export const EXCLUDED = {}

// PROC-0002 "Mutation evidence as an artefact": break 60, low 60, high 80, json reporter, since main.
const STANDARD = {high: 80, low: 60, break: 60}

export function checkConfigs(csprojFiles, read, exists) {
  const errors = []
  const runs = []
  const tests = csprojFiles.filter(file => read(file).includes('Microsoft.NET.Test.Sdk'))
  for (const key of Object.keys(EXCLUDED)) if (!tests.includes(key)) errors.push(`${key}: EXCLUDED names no test project`)
  for (const test of tests) {
    const dir = path.posix.dirname(test)
    const configFile = `${dir}/stryker-config.json`
    if (test in EXCLUDED) {
      if (!EXCLUDED[test]?.trim()) errors.push(`${test}: exclusion has no reason`)
      if (exists(configFile)) errors.push(`${test}: both excluded and configured`)
      continue
    }
    if (!exists(configFile)) { errors.push(`${test}: no ${configFile} and no EXCLUDED reason`); continue }
    const config = JSON.parse(read(configFile))['stryker-config'] ?? {}
    const references = [...read(test).matchAll(/<ProjectReference\s+Include="([^"]+)"/g)]
      .map(match => path.posix.normalize(`${dir}/${match[1].replaceAll('\\', '/')}`))
    const project = references.find(reference => path.posix.basename(reference) === config.project)
    if (!project) errors.push(`${configFile}: project "${config.project}" is not a ProjectReference of ${test}`)
    else if (!exists(project)) errors.push(`${configFile}: project ${project} does not exist`)
    if (JSON.stringify(config.thresholds) !== JSON.stringify(STANDARD)) errors.push(`${configFile}: thresholds must be ${JSON.stringify(STANDARD)}`)
    if (!config.reporters?.includes('json')) errors.push(`${configFile}: the json reporter is required`)
    if (!config.since?.enabled || config.since.target !== base) errors.push(`${configFile}: since must be enabled against ${base}`)
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

// mutation-testing-report-schema: files keyed by path, each with mutants[].status. Counts only the
// changed files; since-mode reports every other file's mutants as Ignored.
export function summarise(report, changed) {
  const entries = Object.entries(report.files ?? {}).map(([file, value]) => [file.replaceAll('\\', '/'), value])
  const matches = (entry, file) => entry === file || entry.endsWith(`/${file}`)
  const missing = changed.filter(file => !entries.some(([entry]) => matches(entry, file)))
  const counts = {}
  for (const [entry, {mutants = []}] of entries) {
    if (!changed.some(file => matches(entry, file))) continue
    for (const {status} of mutants) counts[status] = (counts[status] ?? 0) + 1
  }
  const n = status => counts[status] ?? 0
  const tested = n('Killed') + n('Survived') + n('Timeout')
  const detected = n('Killed') + n('Timeout')
  const covered = detected + n('Survived') + n('NoCoverage')
  const generated = Object.values(counts).reduce((sum, count) => sum + count, 0)
  return {counts, tested, generated, score: covered ? Math.round(1000 * detected / covered) / 10 : null, missing}
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

function main() {
  const git = (...args) => execFileSync('git', args, {cwd: root, encoding: 'utf8'}).trim()
  const files = git('ls-files', '*.csproj').split('\n').filter(Boolean)
  const {errors, runs} = checkConfigs(files, file => readFileSync(path.join(root, file), 'utf8'), file => existsSync(path.join(root, file)))
  for (const error of errors) console.error(`FAIL ${error}`)
  if (errors.length) process.exit(1)
  if (process.argv.includes('--check')) { console.log(`mutation configs: ${runs.length} configured, ${Object.keys(EXCLUDED).length} excluded`); return }

  if (path.resolve(root, git('rev-parse', '--git-dir')) !== path.resolve(root, git('rev-parse', '--git-common-dir'))) {
    console.error('FAIL linked worktree: Stryker since-mode diffs the main checkout and ignores every mutant. Run from a plain clone.')
    process.exit(1)
  }
  const changed = git('diff', '--name-only', `${base}...HEAD`).split('\n').filter(Boolean)
  const msbuild = msbuildArguments()
  const rows = ['| Test project | Changed files | Tested | Killed | Survived | No coverage | Score |', '| --- | ---: | ---: | ---: | ---: | ---: | ---: |']
  let failed = false
  for (const run of runs) {
    const mutable = mutableChanges(changed, run)
    if (!mutable.length) { rows.push(`| ${run.test} | 0 | skipped | | | | |`); continue }
    const output = path.join(root, '.stryker', path.basename(run.test, '.csproj'))
    rmSync(output, {recursive: true, force: true})
    const result = spawnSync('dotnet', ['tool', 'run', 'dotnet-stryker', '--', ...msbuild, '--output', output],
      {cwd: path.join(root, run.dir), stdio: 'inherit'})
    const reportFile = path.join(output, 'reports', 'mutation-report.json')
    if (!existsSync(reportFile)) { console.error(`FAIL ${run.test}: no json report (Stryker exited ${result.status})`); failed = true; continue }
    const {counts, tested, generated, score, missing} = summarise(JSON.parse(readFileSync(reportFile, 'utf8')), mutable)
    rows.push(`| ${run.test} | ${mutable.length} | ${tested} | ${counts.Killed ?? 0} | ${counts.Survived ?? 0} | ${counts.NoCoverage ?? 0} | ${score ?? 'n/a'} |`)
    if (missing.length) { console.error(`FAIL ${run.test}: changed files absent from the report: ${missing.join(', ')}`); failed = true }
    // A changed file with no mutable code yields no mutants; mutants that all went untested do not pass.
    if (generated && !tested) { console.error(`FAIL ${run.test}: mutants generated but none tested ${JSON.stringify(counts)}`); failed = true }
    if (result.status) {
      console.error(`${process.argv.includes('--enforce') ? 'FAIL' : 'WARN'} ${run.test}: Stryker exited ${result.status} (break ${STANDARD.break})`)
      if (process.argv.includes('--enforce')) failed = true
    }
  }
  const table = rows.join('\n')
  console.log(table)
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### Mutation (since ${base})\n\n${table}\n`)
  if (failed) process.exit(1)
}

if (process.argv[1]?.replaceAll('\\', '/').endsWith('eng/mutation-report.mjs')) main()
