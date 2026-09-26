// node --test eng/tests/mutation-report.test.mjs
//
// T-236 row 5. The mutation step's exit code is not evidence, so its checker is: each refusal below
// is a shape that once passed in silence (a project with no config, a config naming the wrong
// project, a report with nothing tested). The last test runs the check against the real tree.
import assert from 'node:assert/strict'
import {spawnSync} from 'node:child_process'
import path from 'node:path'
import test from 'node:test'
import {changedLines, checkConfigs, checkSlices, globRegex, mutableChanges, pickSlice, projectSources, sliceMutate, summarise} from '../mutation-report.mjs'

const root = path.resolve(import.meta.dirname, '../..')
const config = (project, extra = {}) => JSON.stringify({'stryker-config': {
  project, since: {enabled: false, target: 'origin/main'}, thresholds: {high: 80, low: 60, break: 45}, reporters: ['json'], ...extra}})
const testProject = '<Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" />' +
  '<ProjectReference Include="..\\Lib.csproj" /></ItemGroup></Project>'

const baseline = score => JSON.stringify({projects: {'p/tests/T.csproj': {score}}})

function check(files, excluded = {}) {
  const all = {...files}
  if ('p/tests/stryker-config.json' in all && !('eng/baselines/mutation-baseline.json' in all)) all['eng/baselines/mutation-baseline.json'] = baseline(45.9)
  return checkConfigs(Object.keys(all).filter(file => file.endsWith('.csproj')), file => all[file], file => file in all, excluded)
}

test('a test project configured against its own ProjectReference passes', () => {
  const {errors, runs} = check({'p/Lib.csproj': '<Project />', 'p/tests/T.csproj': testProject, 'p/tests/stryker-config.json': config('Lib.csproj')})
  assert.deepEqual(errors, [])
  assert.deepEqual(runs, [{test: 'p/tests/T.csproj', dir: 'p/tests', project: 'p/Lib.csproj'}])
})

test('a test project with no config and no exclusion is refused', () => {
  assert.match(check({'p/Lib.csproj': '', 'p/tests/T.csproj': testProject}).errors.join(), /no p\/tests\/stryker-config.json/)
})

test('an exclusion needs a reason and a test project to name', () => {
  assert.deepEqual(check({'p/tests/T.csproj': testProject}, {'p/tests/T.csproj': 'generated code only'}).errors, [])
  assert.match(check({'p/tests/T.csproj': testProject}, {'p/tests/T.csproj': ' '}).errors.join(), /no reason/)
  assert.match(check({}, {'gone/T.csproj': 'x'}).errors.join(), /names no test project/)
})

test('a non-test project needs no config', () => {
  assert.deepEqual(check({'p/Lib.csproj': '<Project />'}).errors, [])
})

test('a config naming a project the test project does not reference is refused', () => {
  const files = {'p/Lib.csproj': '', 'p/tests/T.csproj': testProject, 'p/tests/stryker-config.json': config('Other.csproj')}
  assert.match(check(files).errors.join(), /"Other.csproj" is not a ProjectReference/)
})

test('a config pointing at a missing project is refused', () => {
  assert.match(check({'p/tests/T.csproj': testProject, 'p/tests/stryker-config.json': config('Lib.csproj')}).errors.join(), /p\/Lib.csproj does not exist/)
})

const configured = (thresholds, score = 45.9) => ({'p/Lib.csproj': '', 'p/tests/T.csproj': testProject,
  'p/tests/stryker-config.json': config('Lib.csproj', {thresholds}), 'eng/baselines/mutation-baseline.json': baseline(score)})

test('a break below the recorded baseline is refused; a raised one passes', () => {
  assert.match(check(configured({high: 80, low: 60, break: 44})).errors.join(), /break 44 is below the baseline 45/)
  assert.deepEqual(check(configured({high: 80, low: 60, break: 50})).errors, [])
})

test('low and high hold the PROC-0002 target, and low rises to a break above 60', () => {
  assert.match(check(configured({high: 80, low: 50, break: 45})).errors.join(), /thresholds must be/)
  assert.match(check(configured({high: 80, low: 60, break: 77}, 77.5)).errors.join(), /"low":77/)
  assert.deepEqual(check(configured({high: 80, low: 77, break: 77}, 77.5)).errors, [])
})

test('a configured project with no baseline, or a baseline naming none, is refused', () => {
  const files = configured({high: 80, low: 60, break: 45})
  files['eng/baselines/mutation-baseline.json'] = JSON.stringify({projects: {'gone/T.csproj': {score: 1}}})
  const errors = check(files).errors.join()
  assert.match(errors, /p\/tests\/T.csproj: no baseline score/)
  assert.match(errors, /gone\/T.csproj: .* names no configured test project/)
})

test('a project with no measured baseline carries a recorded reason and break 0', () => {
  const files = configured({high: 80, low: 60, break: 0})
  files['eng/baselines/mutation-baseline.json'] = JSON.stringify({projects: {'p/tests/T.csproj': {score: null, fullRun: 'too big'}}})
  assert.deepEqual(check(files).errors, [])
  files['p/tests/stryker-config.json'] = config('Lib.csproj', {thresholds: {high: 80, low: 60, break: 10}})
  assert.match(check(files).errors.join(), /no measured baseline \(too big\), so break must be 0/)
})

test('since is enabled by the runner, not the config', () => {
  const files = configured({high: 80, low: 60, break: 45})
  files['p/tests/stryker-config.json'] = config('Lib.csproj', {since: {enabled: true, target: 'origin/main'}})
  assert.match(check(files).errors.join(), /since is enabled by the runner/)
})

test('only changed .cs files of the mutated project, outside its tests, are mutable', () => {
  const run = {dir: 'apps/host/tests', project: 'apps/host/Host.csproj'}
  assert.deepEqual(mutableChanges(['apps/host/A.cs', 'apps/host/tests/ATests.cs', 'apps/host/A.md', 'apps/other/B.cs'], run), ['apps/host/A.cs'])
})

const report = statuses => ({files: {
  'C:\\r\\apps\\host\\A.cs': {mutants: statuses.map(status => ({status}))},
  'C:\\r\\apps\\host\\Unchanged.cs': {mutants: [{status: 'Ignored'}, {status: 'Ignored'}]}}})

test('a report scores only the changed files', () => {
  const summary = summarise(report(['Killed', 'Killed', 'Timeout', 'Survived', 'NoCoverage']), ['apps/host/A.cs'])
  assert.equal(summary.tested, 4)
  assert.equal(summary.generated, 5)
  assert.equal(summary.score, 60)
  assert.deepEqual(summary.missing, [])
})

test('a report whose changed-file mutants were all ignored has tested nothing', () => {
  const summary = summarise(report(['Ignored', 'Ignored']), ['apps/host/A.cs'])
  assert.equal(summary.tested, 0)
  assert.equal(summary.generated, 2)
})

test('survivors and uncovered mutants on changed lines are listed; others are not', () => {
  const lines = changedLines(['diff --git a/apps/host/A.cs b/apps/host/A.cs', '--- a/apps/host/A.cs', '+++ b/apps/host/A.cs',
    '@@ -10,0 +11,2 @@', '+x', '+y', '@@ -30 +32 @@', '-a', '+b', '@@ -40,3 +42,0 @@'].join('\n'))
  assert.deepEqual(lines, {'apps/host/A.cs': [[11, 12], [32, 32]]})
  const mutant = (status, line) => ({status, mutatorName: 'Equality', replacement: 'a != b', location: {start: {line}}})
  const summary = summarise({files: {"C:\\r\\apps\\host\\A.cs": {mutants: [
    mutant('Survived', 11), mutant('NoCoverage', 32), mutant('Killed', 12), mutant('Survived', 20)]}}}, ['apps/host/A.cs'], lines)
  assert.deepEqual(summary.survivors.map(s => [s.line, s.status]), [[11, 'Survived'], [32, 'NoCoverage']])
})

test('a full-mode summary scores every file', () => {
  assert.equal(summarise(report(['Killed', 'Survived'])).score, 50)
})

test('a changed file absent from the report is named', () => {
  assert.deepEqual(summarise(report(['Killed']), ['apps/host/A.cs', 'apps/host/New.cs']).missing, ['apps/host/New.cs'])
})

test('the real tree configures or excludes every test project', () => {
  const result = spawnSync(process.execPath, [path.join(root, 'eng/mutation-report.mjs'), '--check'], {encoding: 'utf8'})
  assert.equal(result.status, 0, result.stderr)
  assert.match(result.stdout, /mutation configs: [1-9]\d* configured/)
})

// Q44: the host's rotating slices.
const slices = [
  {name: 'auth', silentFailure: true, include: ['**/*Authoriz*.cs']},
  {name: 'data', silentFailure: false, include: ['**/Data/**']},
  {name: 'rest', silentFailure: false, include: ['**']},
]
const pending = {auth: {status: 'pending'}, data: {status: 'pending'}, rest: {status: 'pending'}}

test('globs: ** spans folders (or none), * stays in one', () => {
  assert.ok(globRegex('**/*Authoriz*.cs').test('AuthorizationGate.cs'))
  assert.ok(globRegex('**/*Authoriz*.cs').test('Health/RequestAuthorization.cs'))
  assert.ok(!globRegex('Data/*.cs').test('Data/Identity/Store.cs'))
  assert.ok(globRegex('**/Data/**').test('Data/Identity/Store.cs'))
  assert.ok(globRegex('Health/[A-B]*').test('Health/BankAccountRoutes.cs'))
  assert.ok(!globRegex('Health/[A-B]*').test('Health/CalendarRoutes.cs'))
  assert.ok(!globRegex('**/*authoriz*.cs').test('RequestAuthorization.cs'), 'case-sensitive, as Stryker is')
})

test('a slice excludes every earlier slice, so each file has one owner', () => {
  assert.deepEqual(sliceMutate(slices, 2), ['**', '!**/*Authoriz*.cs', '!**/Data/**'])
  const {errors, members} = checkSlices({slices}, ['Data/AuthorizationStore.cs', 'Data/Roster.cs', 'Program.cs'], pending)
  assert.deepEqual(errors, [])
  assert.deepEqual(members, [1, 1, 1])
})

test('a file in no slice, and a slice with no file, are refused', () => {
  const {errors} = checkSlices({slices: slices.slice(0, 2)}, ['Program.cs', 'Data/A.cs'], pending)
  assert.match(errors.join(), /Program.cs: in no mutation slice/)
  assert.match(checkSlices({slices}, ['Data/A.cs', 'Program.cs'], pending).errors.join(), /slice auth has no files/)
})

test('silent-failure slices lead the rotation', () => {
  const reordered = [slices[1], slices[0], slices[2]]
  assert.match(checkSlices({slices: reordered}, ['Data/A.cs', 'X/Authorize.cs', 'P.cs'], pending).errors.join(), /must come first/)
})

test('a slice baseline is pending or measured, never both, and its break holds the floor', () => {
  const files = ['Authorize.cs', 'Data/A.cs', 'P.cs']
  assert.match(checkSlices({slices}, files, {...pending, auth: {status: 'pending', score: 41.2}}).errors.join(), /no longer pending/)
  assert.match(checkSlices({slices}, files, {...pending, auth: {score: 41.2, break: 40}}).errors.join(), /break 40 is below the baseline 41/)
  assert.deepEqual(checkSlices({slices}, files, {...pending, auth: {score: 41.2, break: 41}}).errors, [])
  assert.match(checkSlices({slices}, files, {data: pending.data, rest: pending.rest}).errors.join(), /slice auth needs/)
  assert.match(checkSlices({slices}, files, {...pending, gone: {status: 'pending'}}).errors.join(), /slice gone is not in/)
})

test('the nightly pick walks the rotation one slice a day and wraps', () => {
  const day = n => new Date(Date.UTC(2026, 8, 26 + n, 7))
  const picks = [0, 1, 2, 3].map(n => pickSlice(slices, day(n)).name)
  assert.equal(new Set(picks.slice(0, 3)).size, 3)
  assert.equal(picks[3], picks[0])
  assert.equal(pickSlice(slices, new Date(Date.UTC(2026, 8, 26, 0, 1))).name, pickSlice(slices, new Date(Date.UTC(2026, 8, 26, 23, 59))).name)
})

test('a project compiles its tracked C# minus Compile Remove, plus linked files', () => {
  const csproj = '<Compile Remove="Entrypoint.cs" /><Compile Remove="tests/**/*.cs" /><Compile Include="..\\..\\shared\\Env.cs" Link="Env.cs" />'
  assert.deepEqual(projectSources(csproj, ['Entrypoint.cs', 'Program.cs', 'tests/A.cs', 'README.md']), ['Program.cs', '../../shared/Env.cs'])
})
