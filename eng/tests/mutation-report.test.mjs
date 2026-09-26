// node --test eng/tests/mutation-report.test.mjs
//
// T-236 row 5. The mutation step's exit code is not evidence, so its checker is: each refusal below
// is a shape that once passed in silence (a project with no config, a config naming the wrong
// project, a report with nothing tested). The last test runs the check against the real tree.
import assert from 'node:assert/strict'
import {spawnSync} from 'node:child_process'
import path from 'node:path'
import test from 'node:test'
import {checkConfigs, mutableChanges, summarise} from '../mutation-report.mjs'

const root = path.resolve(import.meta.dirname, '../..')
const config = (project, extra = {}) => JSON.stringify({'stryker-config': {
  project, since: {enabled: true, target: 'origin/main'}, thresholds: {high: 80, low: 60, break: 60}, reporters: ['json'], ...extra}})
const testProject = '<Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" />' +
  '<ProjectReference Include="..\\Lib.csproj" /></ItemGroup></Project>'

function check(files, excluded = {}) {
  return checkConfigs(Object.keys(files).filter(file => file.endsWith('.csproj')), file => files[file], file => file in files, excluded)
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

test('thresholds off the PROC-0002 standard are refused', () => {
  const files = {'p/Lib.csproj': '', 'p/tests/T.csproj': testProject, 'p/tests/stryker-config.json': config('Lib.csproj', {thresholds: {break: 80}})}
  assert.match(check(files).errors.join(), /thresholds must be/)
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

test('a changed file absent from the report is named', () => {
  assert.deepEqual(summarise(report(['Killed']), ['apps/host/A.cs', 'apps/host/New.cs']).missing, ['apps/host/New.cs'])
})

test('the real tree configures or excludes every test project', () => {
  const result = spawnSync(process.execPath, [path.join(root, 'eng/mutation-report.mjs'), '--check'], {encoding: 'utf8'})
  assert.equal(result.status, 0, result.stderr)
  assert.match(result.stdout, /mutation configs: [1-9]\d* configured/)
})
