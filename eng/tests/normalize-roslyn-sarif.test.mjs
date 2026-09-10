import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import * as fsExtra from 'node:fs'
import {repositoryRelativePath} from '../normalize-roslyn-sarif.mjs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {spawnSync} from 'node:child_process'

const root = path.resolve(import.meta.dirname, '../..')
const normalizer = path.join(root, 'eng/normalize-roslyn-sarif.mjs')

const sarifWith = uri => ({
  version: '2.1.0',
  runs: [{
    tool: {driver: {name: 'fixture'}},
    results: [{
      ruleId: 'CA1031',
      level: 'warning',
      message: {text: 'fixture'},
      locations: [{physicalLocation: {
        artifactLocation: {uri},
        region: {startLine: 7, startColumn: 3},
      }}],
    }],
  }],
})

test('normalizer makes Windows and POSIX locations repository-relative and clone-stable', t => {
  const directory = mkdtempSync(path.join(tmpdir(), 'roslyn-normalizer-test-'))
  t.after(() => rmSync(directory, {recursive: true, force: true}))
  const fixtures = [
    {name: 'windows', repoRoot: 'C:/clones/api', uri: 'file:///C:/clones/api/packages/foo/Source.cs'},
    {name: 'posix', repoRoot: '/opt/build/api', uri: '/opt/build/api/packages/foo/Source.cs'},
  ]

  const normalized = fixtures.map(fixture => {
    const file = path.join(directory, `${fixture.name}.sarif`)
    writeFileSync(file, JSON.stringify(sarifWith(fixture.uri)))
    const result = spawnSync(process.execPath,
      [normalizer, '--repo-root', fixture.repoRoot, file], {encoding: 'utf8'})
    assert.equal(result.status, 0, result.stderr)
    return JSON.parse(readFileSync(file, 'utf8')).runs[0].results[0]
  })

  for (const result of normalized) {
    assert.equal(result.locations[0].physicalLocation.artifactLocation.uri,
      'packages/foo/Source.cs')
    assert.doesNotMatch(result.locations[0].physicalLocation.artifactLocation.uri,
      /^(?:file:|[A-Za-z]:|\/)/)
  }
  assert.equal(normalized[0].partialFingerprints['harborline/primary-location/v1'],
    normalized[1].partialFingerprints['harborline/primary-location/v1'])
})

test('a location under the root’s resolved real path is inside the repository (macOS /var -> /private/var)', () => {
  const {realpathSync, symlinkSync, mkdirSync} = fsExtra
  const base = mkdtempSync(path.join(tmpdir(), 'sarif-real-'))
  try {
    const real = path.join(base, 'real'); mkdirSync(real)
    const link = path.join(base, 'link'); symlinkSync(real, link, 'junction')
    const resolved = realpathSync.native(link)
    const uri = 'file:///' + path.posix.join(resolved.split(path.sep).join('/'), 'src/A.cs').replace(/^\//, '')
    assert.equal(repositoryRelativePath(uri, link), 'src/A.cs')
    assert.equal(repositoryRelativePath(uri, resolved), 'src/A.cs')
  } finally {
    rmSync(base, {recursive: true, force: true})
  }
})
