// node --test eng/tests/mutation-report.test.mjs
//
// T-236 row 5. The mutation step's exit code is not evidence, so its checker is: each refusal below
// is a shape that once passed in silence (a project with no config, a config naming the wrong
// project, a report with nothing tested). The last test runs the check against the real tree.
import assert from 'node:assert/strict'
import {spawnSync} from 'node:child_process'
import path from 'node:path'
import test from 'node:test'
import {changedLines, checkConfigs, mutableChanges, summarise} from '../mutation-report.mjs'

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
