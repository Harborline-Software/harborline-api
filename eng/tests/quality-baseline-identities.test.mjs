import test from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import path from 'node:path'
import {distinctBaseline} from '../quality-step.mjs'

const root = path.resolve(import.meta.dirname, '../..')

test('quality baseline fingerprints identify every recorded finding', () => {
  const baseline = JSON.parse(readFileSync(path.join(root, 'eng/baselines/quality-baseline.json'), 'utf8'))
  const fingerprints = baseline.findings.map(finding => finding.fingerprint)
  assert.equal(new Set(fingerprints).size, fingerprints.length,
    `expected ${fingerprints.length} distinct fingerprints, found ${new Set(fingerprints).size}`)
})

test('baseline writer retains one copy of an honestly duplicated identity', () => {
  const identity = JSON.stringify({'harborline/primary-location/v1': 'location', 'harborline/project/v1': 'project'})
  const baseline = distinctBaseline({findings: [
    {fingerprint: 'broad-a', enginePartial: identity},
    {fingerprint: 'broad-b', enginePartial: identity},
  ]})
  assert.equal(baseline.findings.length, 1)
})
