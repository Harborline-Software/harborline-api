import test from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import path from 'node:path'
import {inspectCandidate} from '../validation-consumer.mjs'
import {inputs} from './validation-fixtures.mjs'
import {fingerprint} from '../validation-reuse.mjs'

test('consumer workflow has read-only scope and executes only its own workflow commit', () => {
  const source = readFileSync(path.resolve(import.meta.dirname, '../../.github/workflows/validation-consumer.yml'), 'utf8')
  assert.match(source, /contents: read/)
  assert.match(source, /actions: read/)
  assert.match(source, /persist-credentials: false/)
  assert.match(source, /ref: \$\{\{ github\.workflow_sha \}\}/)
  assert.equal(/(?:checks|contents|actions|id-token): write/.test(source), false)
  assert.equal(/ref:.*workflow_run\.head_sha/.test(source), false)
})
test('candidate-specific reports deny incomplete lanes and coverage-mode mismatches', async () => {
  const sha = 'c'.repeat(40)
  const records = ['verify-macos', 'verify-linux', 'verify-windows-hosted'].map(lane => {
    const manifest = inputs()
    if (lane === 'verify-windows-hosted') {
      manifest.selection.quality = true
      manifest.pins.quality = {applicable: true, commit: 'd'.repeat(40), tree: 'e'.repeat(40), declared: 'd'.repeat(40)}
      manifest.pins.control = {applicable: true, commit: 'f'.repeat(40), tree: '1'.repeat(40)}
    }
    return {lane, transportVerified: true, event: 'merge_group',
      observation: {candidateSha: sha, inputs: manifest, fingerprint: fingerprint(manifest)}}
  })
  const api = async endpoint => endpoint.includes('/workflows/') ? {workflow_runs: []}
    : endpoint.endsWith('/branches/main') ? {commit: {sha: 'a'.repeat(40)}} : {truncated: false, tree: []}
  const inspect = () => inspectCandidate({api, runId: 123, consumerContext: {}, observe: async () => records})
  const mismatch = await inspect()
  assert.equal(mismatch.requiredLaneSetComplete, false, 'merge-group Windows OFF does not satisfy ON profile')
  assert.equal(mismatch.lanes[2].profile.modeMatches, false)
  assert.equal(mismatch.lanes[2].candidateSha, sha)
  assert.equal(mismatch.reuseAuthorized, false)
  records[2].observation.inputs.coverage.enabled = true
  records[2].observation.fingerprint = fingerprint(records[2].observation.inputs)
  assert.equal((await inspect()).requiredLaneSetComplete, true)
  records[1].observation.candidateSha = 'd'.repeat(40)
  assert.equal((await inspect()).requiredLaneSetComplete, false, 'all lanes must name the exact candidate')
  records[1].observation.candidateSha = sha
  records[1].observation.fingerprint = '0'.repeat(64)
  assert.equal((await inspect()).requiredLaneSetComplete, false, 'corrupt input fingerprint cannot complete a lane')
  records[1].observation.fingerprint = fingerprint(records[1].observation.inputs)
  records.pop()
  assert.equal((await inspect()).requiredLaneSetComplete, false)
})
test('unsafe artifact cannot supply policy or invoke privileged execution', async () => {
  const manifest = inputs()
  const result = await inspectCandidate({api: async endpoint => endpoint.includes('/workflows/') ? {workflow_runs: []}
    : {commit: {sha: 'a'.repeat(40)}}, runId: 123,
    consumerContext: {eventName: 'pull_request', workflowSha: 'b'.repeat(40)},
    observe: async () => [{lane: 'verify-macos', transportVerified: true,
      observation: {candidateSha: 'b'.repeat(40), inputs: manifest, fingerprint: fingerprint(manifest),
        policy: {mode: 'enforce', trusted: true}, executable: 'candidate-controlled-script'}}]})
  assert.equal(result.lanes[0].boundary.consumerTrusted, false)
  assert.equal(result.reuseAuthorized, false)
  assert.equal(result.requiredWorkSkipped, false)
})
