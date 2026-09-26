import assert from 'node:assert/strict'
import test from 'node:test'

import {baselineProblems, changedRanges, effectiveBreak, entryFor, feedback, gaps, isMutable, mutateEntries, shard, tally, verdict} from '../strykerjs.mjs'

const packages = [
  {path: 'ui/*', toolchain: 'ui/button'},
  {path: 'lib', toolchain: 'lib'},
  {path: 'e2e', exclude: 'browser suite'},
]
const report = statuses => ({files: {'src/a.ts': {mutants: statuses.map((status, index) => ({status, location: {start: {line: index + 1}}, mutatorName: 'M', replacement: 'r'}))}}})

test('a test package with no entry or exclusion is a gap, and so is a stale exact entry', () => {
  assert.deepEqual(gaps(['ui/card', 'lib', 'e2e'], packages), [])
  assert.deepEqual(gaps(['ui/card', 'lib', 'e2e', 'tools'], packages), ['tools: no StrykerJS entry or exclusion'])
  assert.deepEqual(gaps(['ui/card', 'e2e'], packages), ['lib: entry names no test package'])
  assert.equal(entryFor('ui/card/nested', packages), undefined, 'dir/* covers direct children only')
})

test('only package source outside tests and setup files is mutable', () => {
  assert.ok(isMutable('src/Layout.tsx'))
  for (const file of ['src/__tests__/Layout.test.tsx', 'src/a.test.ts', 'src/test-setup.ts', 'src/types.d.ts', 'vitest.config.ts', 'tests/a.ts']) assert.ok(!isMutable(file), file)
})

test('PR mode mutates only the changed lines of mutable files; full mode mutates whole files', () => {
  const diff = [
    'diff --git a/ui/card/src/Card.tsx b/ui/card/src/Card.tsx', '--- a/ui/card/src/Card.tsx', '+++ b/ui/card/src/Card.tsx',
    '@@ -3,0 +4,2 @@', '+a', '+b', '@@ -10 +12 @@', '-x', '+y', '@@ -20,3 +23,0 @@',
    '+++ b/ui/card/src/__tests__/Card.test.tsx', '@@ -1 +1 @@', '+++ b/ui/cardx/src/X.tsx', '@@ -1 +1 @@',
  ].join('\n')
  const ranges = changedRanges(diff)
  assert.deepEqual(ranges.get('ui/card/src/Card.tsx'), [[4, 5], [12, 12]], 'a pure deletion adds no range')
  const files = [...ranges.keys()]
  assert.deepEqual(mutateEntries('ui/card', files, ranges), ['src/Card.tsx:4-5', 'src/Card.tsx:12-12'])
  assert.deepEqual(mutateEntries('ui/card', files, undefined), ['src/Card.tsx'])
})

test('a report with zero tested mutants fails even when Stryker exits 0 (the T-711 shape)', () => {
  assert.equal(verdict({status: 0, output: '', report: undefined}).ok, false)
  assert.equal(verdict({status: 0, output: '', report: report([])}).ok, false)
  assert.equal(verdict({status: 0, output: '', report: report(['NoCoverage', 'CompileError'])}).ok, false)
  const ranNoTests = {files: {'src/a.ts': {mutants: [{status: 'Survived', testsCompleted: 0}, {status: 'Killed', testsCompleted: 3}]}}}
  assert.equal(tally(ranNoTests).tested, 1, 'a Survived mutant that ran zero tests was not tested')
  assert.equal(verdict({status: 0, output: '', report: ranNoTests}).ok, false, 'any such mutant means the runner is not running mutant tests')
})

test('the break is compared only in a full run, never in PR mode and never for a pending package', () => {
  const baselines = {packages: {'ui/card': {baseline: 59, break: 59}, lib: {pending: true}}}
  assert.equal(effectiveBreak('ui/card', {full: true}, baselines), 59)
  assert.equal(effectiveBreak('ui/card', {full: false}, baselines), null)
  assert.equal(effectiveBreak('lib', {full: true}, baselines), null)
  const low = report(['Killed', 'Survived', 'Survived', 'NoCoverage'])
  assert.equal(verdict({status: 0, output: '', report: low, breakAt: 59}).ok, false, 'a full run under its break fails')
  assert.equal(verdict({status: 0, output: '', report: low, breakAt: null}).ok, true, 'a PR run reports, it does not compare')
  assert.equal(verdict({status: 1, output: '', report: low, breakAt: null}).ok, false, 'a non-zero Stryker exit is a run error in either mode')
  assert.deepEqual(tally(report(['Killed', 'Killed', 'Timeout', 'Survived', 'NoCoverage'])).score, 60)
})

test('PR feedback lists the Survived and NoCoverage mutants by line', () => {
  assert.deepEqual(feedback(report(['Killed', 'Survived', 'NoCoverage'])).map(row => `${row.status}@${row.line}`), ['Survived@2', 'NoCoverage@3'])
})

test('every mutated package needs a baseline, a break below it is refused, and pending fails check', () => {
  const baselines = {packages: {'ui/card': {baseline: 59, break: 59}, lib: {baseline: 73, break: 75}}}
  assert.deepEqual(baselineProblems(['ui/card', 'lib', 'e2e'], baselines, packages), {problems: [], pending: []})
  assert.deepEqual(baselineProblems(['ui/card', 'ui/new', 'lib'], baselines, packages).problems, ['ui/new: no baseline in strykerjs.baselines.json'])
  const lowered = {packages: {...baselines.packages, lib: {baseline: 73, break: 70}}}
  assert.deepEqual(baselineProblems(['ui/card', 'lib'], lowered, packages).problems, ['lib: break 70 is below the recorded baseline 73'])
  const pending = {packages: {...baselines.packages, lib: {pending: true}}}
  assert.deepEqual(baselineProblems(['ui/card', 'lib'], pending, packages), {problems: [], pending: ['lib: baseline pending; run the baselines and record it']})
  assert.deepEqual(baselineProblems(['e2e'], {packages: {e2e: {baseline: 10, break: 10}}}, packages).problems, ['e2e: baseline names no mutated test package'])
})

test('shards partition the targets', () => {
  const targets = ['a', 'b', 'c', 'd', 'e']
  assert.deepEqual([shard(targets, '1/2'), shard(targets, '2/2')], [['a', 'c', 'e'], ['b', 'd']])
  assert.deepEqual(shard(targets, undefined), targets)
  assert.throws(() => shard(targets, '3/2'))
})

test('the only sanctioned empty run is Stryker instrumenting zero mutants', () => {
  assert.equal(verdict({status: 1, output: 'INFO Instrumenter Instrumented 1 source file(s) with 0 mutant(s)', report: undefined}).ok, true)
})
