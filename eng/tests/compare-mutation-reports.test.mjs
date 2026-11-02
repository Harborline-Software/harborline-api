// node --test eng/tests/compare-mutation-reports.test.mjs
import {test} from 'node:test'
import assert from 'node:assert/strict'
import {spawnSync} from 'node:child_process'
import {mkdirSync, mkdtempSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {compare} from '../compare-mutation-reports.mjs'

const loc = (a, b) => ({start: {line: a, column: 1}, end: {line: b, column: 2}})
const report = (statuses, tests = ['T.A']) => ({
  files: {'src\\A.cs': {mutants: statuses.map((status, i) => ({mutatorName: 'Equality', replacement: '!=', location: loc(i + 1, i + 1), status}))}},
  testFiles: {'t.cs': {tests: tests.map((name, i) => ({id: String(i), name}))}},
})
function dir(r) {
  const d = mkdtempSync(path.join(tmpdir(), 'cmp-'))
  mkdirSync(path.join(d, '.stryker', 'tests', 'reports'), {recursive: true})
  writeFileSync(path.join(d, '.stryker', 'tests', 'reports', 'mutation-report.json'), JSON.stringify(r))
  return d
}

test('identical runs match', () => {
  assert.equal(compare(dir(report(['Killed', 'Survived'])), dir(report(['Killed', 'Survived']))).pass, true)
})
test('Killed vs Timeout is listed as timing, not a failure', () => {
  const r = compare(dir(report(['Killed', 'Survived'])), dir(report(['Timeout', 'Survived'])))
  assert.equal(r.pass, true)
  assert.equal(r.projects[0].timing.length, 1)
})
test('any other status difference fails', () => {
  assert.equal(compare(dir(report(['Killed', 'Survived'])), dir(report(['Killed', 'Killed']))).pass, false)
})
test('a mutant present on one side only fails', () => {
  assert.equal(compare(dir(report(['Killed', 'Survived'])), dir(report(['Killed']))).pass, false)
})
test('a test discovered on one side only fails', () => {
  assert.equal(compare(dir(report(['Killed'], ['T.A', 'T.B'])), dir(report(['Killed'], ['T.A']))).pass, false)
})
test('a project missing on one side fails', () => {
  const a = dir(report(['Killed']))
  mkdirSync(path.join(a, '.stryker', 'other', 'reports'), {recursive: true})
  writeFileSync(path.join(a, '.stryker', 'other', 'reports', 'mutation-report.json'), JSON.stringify(report(['Killed'])))
  assert.equal(compare(a, dir(report(['Killed']))).pass, false)
})
test('the CLI exits non-zero with its usage when given no directories', () => {
  const r = spawnSync(process.execPath, [path.resolve(import.meta.dirname, '../compare-mutation-reports.mjs')], {encoding: 'utf8'})
  assert.equal(r.status, 2)
  assert.match(r.stderr, /usage: compare-mutation-reports\.mjs/)
})
test('the CLI exits 1 on a difference and 0 on a match', () => {
  const cli = path.resolve(import.meta.dirname, '../compare-mutation-reports.mjs')
  const ref = dir(report(['Killed']))
  assert.equal(spawnSync(process.execPath, [cli, ref, dir(report(['Killed']))]).status, 0)
  assert.equal(spawnSync(process.execPath, [cli, ref, dir(report(['Survived']))]).status, 1)
})
