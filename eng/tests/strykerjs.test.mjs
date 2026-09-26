import assert from 'node:assert/strict'
import test from 'node:test'

import {baselineProblems, breakFor, entryFor, gaps, isMutable, mutableFiles, tally, verdict} from '../strykerjs.mjs'

const packages = [
  {path: 'ui/*', toolchain: 'ui/button'},
  {path: 'lib', toolchain: 'lib'},
  {path: 'e2e', exclude: 'browser suite'},
]
const report = statuses => ({files: {'src/a.ts': {mutants: statuses.map(status => ({status}))}}})

test('a test package with no entry or exclusion is a gap, and so is a stale exact entry', () => {
  assert.deepEqual(gaps(['ui/card', 'lib', 'e2e'], packages), [])
  assert.deepEqual(gaps(['ui/card', 'lib', 'e2e', 'tools'], packages), ['tools: no StrykerJS entry or exclusion'])
  assert.deepEqual(gaps(['ui/card', 'e2e'], packages), ['lib: entry names no test package'])
  assert.equal(entryFor('ui/card/nested', packages), undefined, 'dir/* covers direct children only')
})

test('only package source outside tests and setup files is mutable', () => {
  assert.ok(isMutable('src/Layout.tsx'))
  for (const file of ['src/__tests__/Layout.test.tsx', 'src/a.test.ts', 'src/test-setup.ts', 'src/types.d.ts', 'vitest.config.ts', 'tests/a.ts']) assert.ok(!isMutable(file), file)
  assert.deepEqual(mutableFiles('ui/card', ['ui/card/src/Card.tsx', 'ui/cardx/src/X.tsx', 'ui/card/src/__tests__/Card.test.tsx']), ['src/Card.tsx'])
})

test('a report with zero tested mutants fails even when Stryker exits 0 (the T-711 shape)', () => {
  assert.equal(verdict({status: 0, output: '', report: undefined}).ok, false)
  assert.equal(verdict({status: 0, output: '', report: report([])}).ok, false)
  assert.equal(verdict({status: 0, output: '', report: report(['NoCoverage', 'CompileError'])}).ok, false)
  const ranNoTests = {files: {'src/a.ts': {mutants: [{status: 'Survived', testsCompleted: 0}, {status: 'Killed', testsCompleted: 3}]}}}
  assert.equal(tally(ranNoTests).tested, 1, 'a Survived mutant that ran zero tests was not tested')
  assert.equal(verdict({status: 0, output: '', report: ranNoTests}).ok, false, 'any such mutant means the runner is not running mutant tests')
})

test('a run with tested mutants passes at or above its break and fails below it', () => {
  const sixty = report(['Killed', 'Killed', 'Timeout', 'Survived', 'NoCoverage'])
  assert.deepEqual(tally(sixty).score, 60)
  assert.equal(verdict({status: 0, output: '', report: sixty, breakAt: 60}).ok, true)
  assert.equal(verdict({status: 0, output: '', report: sixty, breakAt: 61}).ok, false, 'Stryker break is null; the checker enforces the per-package break')
  assert.equal(verdict({status: 1, output: '', report: sixty, breakAt: 0}).ok, false, 'a non-zero Stryker exit is a run error')
})

test('a break below its recorded baseline is refused, and unmeasured packages take the default', () => {
  const baselines = {default: {break: 60}, packages: {'ui/card': {baseline: 59, break: 59}, lib: {baseline: 73, break: 75}}}
  assert.deepEqual(baselineProblems(['ui/card', 'lib', 'e2e'], baselines, packages), [])
  assert.equal(breakFor('ui/card', baselines), 59)
  assert.equal(breakFor('ui/other', baselines), 60)
  const lowered = {...baselines, packages: {...baselines.packages, lib: {baseline: 73, break: 70}}}
  assert.deepEqual(baselineProblems(['ui/card', 'lib', 'e2e'], lowered, packages), ['lib: break 70 is below the recorded baseline 73'])
  const stray = {...baselines, packages: {e2e: {baseline: 10, break: 10}}}
  assert.deepEqual(baselineProblems(['e2e'], stray, packages), ['e2e: baseline names no mutated test package'])
})

test('the only sanctioned empty run is Stryker instrumenting zero mutants', () => {
  assert.equal(verdict({status: 1, output: 'INFO Instrumenter Instrumented 1 source file(s) with 0 mutant(s)', report: undefined}).ok, true)
})
