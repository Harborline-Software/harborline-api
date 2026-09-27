import {test} from 'node:test'
import assert from 'node:assert/strict'
import {spawnSync} from 'node:child_process'
import {mkdtempSync, writeFileSync, mkdirSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {fileURLToPath} from 'node:url'

// T-724 ruling 119e: known-tests-drift.mjs is report-only -- it never writes a baseline, only
// compares a run's candidate against the committed roster and exits non-zero (naming names) when
// they differ, so a nightly job catches a missed --write-known-tests refresh.
const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '..', '..')
const script = path.join(root, 'eng', 'known-tests-drift.mjs')

function fixture({hostKnown, capabilityKnown, hostObserved, capabilityObserved}) {
  const dir = mkdtempSync(path.join(tmpdir(), 'known-tests-drift-'))
  mkdirSync(path.join(dir, 'eng', 'baselines'), {recursive: true})
  const hostBaseline = path.join(dir, 'eng', 'baselines', 'host.json')
  const capabilityBaseline = path.join(dir, 'eng', 'baselines', 'capability.json')
  writeFileSync(hostBaseline, JSON.stringify({knownTests: hostKnown}))
  writeFileSync(capabilityBaseline, JSON.stringify({knownTests: capabilityKnown}))
  const candidate = path.join(dir, 'candidate.json')
  writeFileSync(candidate, JSON.stringify({
    host: {baseline: 'eng/baselines/host.json', identities: hostObserved},
    capability: {baseline: 'eng/baselines/capability.json', identities: capabilityObserved},
  }))
  const r = spawnSync(process.execPath, [script, candidate], {encoding: 'utf8', cwd: dir,
    env: {...process.env}})
  rmSync(dir, {recursive: true, force: true})
  return {status: r.status, out: r.stdout + r.stderr}
}

test('no drift: matching rosters exit 0', () => {
  const r = fixture({hostKnown: ['A', 'B'], capabilityKnown: ['X'], hostObserved: ['A', 'B'], capabilityObserved: ['X']})
  assert.equal(r.status, 0, r.out)
  assert.match(r.out, /matches this run/)
})
test('drift: a candidate identity absent from the committed roster (an addition never repinned) fails, naming it', () => {
  const r = fixture({hostKnown: ['A'], capabilityKnown: ['X'], hostObserved: ['A', 'B (new)'], capabilityObserved: ['X']})
  assert.notEqual(r.status, 0)
  assert.match(r.out, /is stale \(1 added, 0 removed/)
  assert.match(r.out, /\+ B \(new\)/)
})
test('drift: a committed identity the candidate no longer observed (a removal never repinned) fails, naming it', () => {
  const r = fixture({hostKnown: ['A', 'B (gone)'], capabilityKnown: ['X'], hostObserved: ['A'], capabilityObserved: ['X']})
  assert.notEqual(r.status, 0)
  assert.match(r.out, /is stale \(0 added, 1 removed/)
  assert.match(r.out, /- B \(gone\)/)
})
test('an unpopulated committed roster is reported, not treated as drift', () => {
  const r = fixture({hostKnown: [], capabilityKnown: ['X'], hostObserved: ['A'], capabilityObserved: ['X']})
  assert.equal(r.status, 0, r.out)
  assert.match(r.out, /has no committed roster yet/)
})
