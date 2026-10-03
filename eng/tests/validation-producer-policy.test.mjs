import test from 'node:test'
import assert from 'node:assert/strict'
import {compareProducerDefinitions, validateConsumerRoot, inspectProducerBoundary} from '../validation-producer-policy.mjs'

const sha = 'a'.repeat(40)
const context = {eventName: 'workflow_run', repository: 'Harborline-Software/harborline-api', workflowSha: sha,
  workflowRef: 'Harborline-Software/harborline-api/.github/workflows/validation-consumer.yml@refs/heads/main'}
const tree = () => ({truncated: false, tree: ['.github/workflows/verify.yml', '.github/workflows/validation-consumer.yml',
  'eng/validation-github-shadow.mjs', 'eng/validation-reuse.mjs', 'eng/validation-inputs.mjs',
  '.github/actions/platform-feed/action.yml', 'apps/local-node-host/Program.cs'].map(path => ({path, mode: '100644', type: 'blob', sha}))})
test('consumer root must be independently resolved protected-main workflow context', () => {
  assert.equal(validateConsumerRoot({...context, defaultBranchSha: sha}), true)
  for (const change of [{eventName: 'pull_request'}, {workflowSha: 'b'.repeat(40)}, {repository: 'attacker/fork'},
    {workflowRef: context.workflowRef.replace('refs/heads/main', 'refs/heads/feature')}])
    assert.equal(validateConsumerRoot({...context, defaultBranchSha: sha, ...change}), false)
})
test('candidate-modified verifier, producer workflow and action definitions are denied', () => {
  assert.equal(compareProducerDefinitions({trustedTree: tree(), candidateTree: tree()}).definitionMatches, true)
  for (const file of ['eng/validation-reuse.mjs', 'eng/validation-inputs.mjs', '.github/workflows/verify.yml',
    '.github/workflows/validation-consumer.yml', '.github/actions/platform-feed/action.yml']) {
    const candidate = tree(); candidate.tree.find(item => item.path === file).sha = 'b'.repeat(40)
    assert.equal(compareProducerDefinitions({trustedTree: tree(), candidateTree: candidate}).definitionMatches, false, file)
  }
  const missing = tree(); missing.tree = missing.tree.filter(item => item.path !== 'eng/validation-reuse.mjs')
  assert.equal(compareProducerDefinitions({trustedTree: tree(), candidateTree: missing}).definitionMatches, false)
  assert.equal(compareProducerDefinitions({trustedTree: {...tree(), truncated: true}, candidateTree: tree()}).definitionMatches, false)
})
test('same-repository successful source is not a trusted producer until protected-main ancestry is proved', async () => {
  const candidateCommit = 'b'.repeat(40)
  const api = async endpoint => endpoint.endsWith('/branches/main') ? {protected: true, commit: {sha}}
    : endpoint.includes('/compare/') ? {status: 'diverged'} : tree()
  const result = await inspectProducerBoundary({api, candidateCommit, consumerContext: context})
  assert.equal(result.definitionMatches, true)
  assert.equal(result.sourceOnProtectedMain, false)
  assert.equal(result.reuseAuthorized, false)
  const forged = await inspectProducerBoundary({api, candidateCommit, consumerContext: {...context, workflowSha: candidateCommit}})
  assert.equal(forged.consumerTrusted, false)
  const unprotected = await inspectProducerBoundary({api: async endpoint => endpoint.endsWith('/branches/main')
    ? {protected: false, commit: {sha}} : api(endpoint), candidateCommit, consumerContext: context})
  assert.equal(unprotected.consumerTrusted, false)
})
