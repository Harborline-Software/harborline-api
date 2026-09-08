import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {spawnSync} from 'node:child_process'
import {assertFeed, readPin} from '../build-local-feed.mjs'

const root = path.resolve(import.meta.dirname, '../..')
test('builder accepts the full manifest and refuses a second producer before packing', () => {
  const directory = mkdtempSync(path.join(tmpdir(), 'platform-producers-'))
  const file = path.join(directory, 'manifest.json')
  const manifest = Object.entries(readPin().producers).map(([id, assembly]) => ({id, assembly}))
  const run = () => spawnSync(process.execPath, [path.join(root, 'eng/build-local-feed.mjs'), '--check-manifest', file], {encoding: 'utf8'})
  try {
    writeFileSync(file, JSON.stringify(manifest))
    assert.equal(run().status, 0)
    for (const second of [manifest[0], {...manifest[0], assembly: 'SecondProducer.dll'},
      {...manifest[0], id: manifest[0].id.toLowerCase()}]) {
      writeFileSync(file, JSON.stringify([...manifest, second]))
      const red = run()
      assert.equal(red.status, 1)
      assert.match(red.stderr, /duplicate producer for/i)
    }
    writeFileSync(file, JSON.stringify(manifest))
    assert.equal(run().status, 0)
  } finally {
    rmSync(directory, {recursive: true, force: true})
  }
})
test('the checked-in platform pin parses and names a 40-hex commit and 24 producers', () => {
  const pin = readPin()
  assert.match(pin.commit, /^[a-f0-9]{40}$/)
  assert.equal(pin.repository, 'Harborline-Software/harborline-platform')
  assert.equal(Object.keys(pin.producers).length, 24)
})
test('nuget.config declares the built local feed beside nuget.org', () => {
  assertFeed()
  const config = readFileSync(path.join(root, 'nuget.config'), 'utf8')
  assert.match(config, /key="nuget.org" value="https:\/\/api.nuget.org\/v3\/index.json"/)
})
