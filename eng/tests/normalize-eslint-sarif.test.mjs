import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {spawnSync} from 'node:child_process'

const root = path.resolve(import.meta.dirname, '../..')
const normalizer = path.join(root, 'eng/normalize-eslint-sarif.mjs')

const eslintSarif = uri => ({
  version: '2.1.0',
  runs: [{
    tool: {driver: {name: '@typescript-eslint/eslint-plugin', version: '8.69.0'}},
    results: [{
      ruleId: '@typescript-eslint/no-floating-promises',
      level: 'error',
      message: {text: 'Promises must be awaited.'},
      locations: [{physicalLocation: {artifactLocation: {uri}, region: {startLine: 2, startColumn: 3}}}],
    }],
  }],
})

test('ESLint SARIF is normalized to the eslint engine and three quality fingerprints', t => {
  const directory = mkdtempSync(path.join(tmpdir(), 'eslint-normalizer-test-'))
  t.after(() => rmSync(directory, {recursive: true, force: true}))
  const file = path.join(directory, 'contracts.sarif')
  writeFileSync(file, JSON.stringify(eslintSarif('file:///C:/clones/api/packages/contracts/src/canary.ts')))
  const result = spawnSync(process.execPath,
    [normalizer, '--repo-root', 'C:/clones/api', 'contracts', file], {encoding: 'utf8'})
  assert.equal(result.status, 0, result.stderr)
  const run = JSON.parse(readFileSync(file, 'utf8')).runs[0]
  const finding = run.results[0]
  assert.equal(run.tool.driver.name, 'eslint')
  assert.equal(finding.locations[0].physicalLocation.artifactLocation.uri, 'packages/contracts/src/canary.ts')
  assert.deepEqual(Object.keys(finding.partialFingerprints).sort(), [
    'harborline/primary-location/v1', 'harborline/primary-location/v2', 'harborline/project/v1',
  ])
  for (const value of Object.values(finding.partialFingerprints)) assert.match(value, /^sha256:[a-f0-9]{64}$/)
})
