import test from 'node:test'
import assert from 'node:assert/strict'
import {spawnSync} from 'node:child_process'
import {mkdtempSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {readPlatformSeedVersion} from '../platform-seed-version.mjs'

const root = path.resolve(import.meta.dirname, '../..')
const message = 'could not read the platform pack version from the preload service'

test('reads revision 2.0.0 from the pinned platform manifest (T-670 intake acceptance)', () => {
  assert.equal(readPlatformSeedVersion(path.join(root, '.feed')), '2.0.0')
})

for (const [name, result] of [
  ['missing version', {status: 0, stdout: ''}],
  ['empty version', {status: 0, stdout: 'platform-seed-version=\n'}],
  ['invalid version', {status: 0, stdout: 'platform-seed-version=unknown\n'}],
  ['ambiguous version', {status: 0, stdout: 'platform-seed-version=2.0.0\nplatform-seed-version=1.6.1\n'}],
  ['unreadable manifest', {status: 1, stdout: 'platform-seed-version=2.0.0\n'}],
  ['unavailable reader', {status: null, stdout: null}],
]) {
  test(`${name} refuses with the existing diagnostic`, () => {
    assert.throws(() => readPlatformSeedVersion('/unused', () => result), {message})
  })
}

test('a feed with no manifest refuses through the CLI with the existing diagnostic', () => {
  // Keep this named fixture for inspection; never replace a shared feed or cache.
  const feed = mkdtempSync(path.join(tmpdir(), 'api479-empty-feed-'))
  const result = spawnSync('node', [path.join(root, 'eng/platform-seed-version.mjs'), feed], {encoding: 'utf8'})
  assert.equal(result.status, 1)
  assert.equal(result.stdout, '')
  assert.equal(result.stderr, 'removal-exercise: could not read the platform pack version from the preload service\n')
})


test('an unreadable feed archive refuses through the CLI with the existing diagnostic', () => {
  const feed = mkdtempSync(path.join(tmpdir(), 'api479-corrupt-feed-'))
  writeFileSync(path.join(feed, 'broken.nupkg'), 'not an archive')
  const result = spawnSync('node', [path.join(root, 'eng/platform-seed-version.mjs'), feed], {encoding: 'utf8'})
  assert.equal(result.status, 1)
  assert.equal(result.stdout, '')
  assert.equal(result.stderr, 'removal-exercise: could not read the platform pack version from the preload service\n')
})
