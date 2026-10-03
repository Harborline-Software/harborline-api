import test from 'node:test'
import assert from 'node:assert/strict'
import {generateKeyPairSync, sign} from 'node:crypto'
import {canonical, digest, fingerprint, compareInputs, verifyReceipt, shadowVerdict, inputProblems} from '../validation-reuse.mjs'
import {compareObservations} from '../validation-shadow-report.mjs'

import {inputs} from './validation-fixtures.mjs'
const {privateKey, publicKey} = generateKeyPairSync('ed25519')
const bytes = Buffer.from('immutable test artifact')
const observation = {repository: 'Harborline-Software/harborline-api', runId: '123', runAttempt: '1', jobId: '456',
  workflowPath: '.github/workflows/trusted-validation.yml', workflowCommit: 'e'.repeat(40), headSha: 'f'.repeat(40),
  candidateTree: 'a'.repeat(40), artifactId: '789', status: 'completed', conclusion: 'success', expired: false, artifactDigest: digest(bytes)}
const policy = {mode: 'shadow', keys: {test: publicKey}, producers: [{repository: observation.repository,
  workflowPath: observation.workflowPath, workflowCommit: observation.workflowCommit}], requiredInvocations: ['host-tests', 'baseline-match']}
const envelope = (change = () => {}) => {
  const payload = {inputs: inputs(), artifactDigest: digest(bytes), provenance: {...observation}, status: 'PASS', completed: true,
    invocations: [{id: 'host-tests', status: 'completed', verdict: 'PASS'}, {id: 'baseline-match', status: 'completed', verdict: 'PASS'}]}
  change(payload)
  payload.inputFingerprint = fingerprint(payload.inputs)
  return {schemaVersion: 1, keyId: 'test', payload,
    signature: sign(null, Buffer.from(`harborline-validation-receipt/v1\n${canonical(payload)}`), privateKey).toString('base64')}
}
const verify = (receipt = envelope(), overrides = {}) => verifyReceipt({envelope: receipt, artifactBytes: bytes, observation, policy, ...overrides})

test('canonical JSON is stable and SHA-256 matches the published abc test vector', () => {
  assert.equal(canonical({z: 1, a: {y: 2, b: 3}}), '{"a":{"b":3,"y":2},"z":1}')
  assert.equal(digest('abc'), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
  assert.throws(() => canonical({value: undefined}))
})
test('every lane input dimension invalidates equality and names the changed field', () => {
  for (const field of ['candidateTree', 'lane', 'dependencies', 'producer', 'toolchain', 'platform', 'pins', 'selection', 'coverage', 'commitInputs']) {
    const changed = structuredClone(inputs())
    changed[field] = field === 'candidateTree' ? 'b'.repeat(40) : field === 'lane' ? 'packages' : {changed: true}
    const result = compareInputs(inputs(), changed)
    assert.equal(result.sameInputs, false, field)
    assert.ok(result.differences.some(key => key.startsWith(`inputs.${field}`)), field)
  }
})
test('coverage mismatch is precise; missing and unknown inputs fail closed', () => {
  const changed = inputs(); changed.coverage.enabled = true
  assert.deepEqual(compareInputs(inputs(), changed).differences, ['inputs.coverage.enabled'])
  const missing = inputs(); delete missing.toolchain
  assert.equal(compareInputs(inputs(), missing).completeInputs, false)
  const unknown = inputs(); unknown.unknownInputs.push('native version')
  assert.equal(compareInputs(unknown, unknown).completeInputs, false)
})

test('nonempty placeholder dimensions and absent typed fields are incomplete evidence', () => {
  assert.deepEqual(inputProblems(inputs()), [])
  for (const field of ['dependencies', 'producer', 'toolchain', 'platform', 'pins', 'selection', 'coverage', 'commitInputs']) {
    const item = inputs(); item[field] = {placeholder: true}
    assert.equal(compareInputs(item, item).completeInputs, false, field)
  }
  const missingArchitecture = inputs(); delete missingArchitecture.platform.architecture
  assert.ok(inputProblems(missingArchitecture).includes('platform architecture is unobserved or unsupported'))
  const missingSdk = inputs(); delete missingSdk.toolchain.tools.dotnet
  assert.ok(inputProblems(missingSdk).includes('toolchain.tools.dotnet is unobserved'))
  const missingNative = inputs(); missingNative.dependencies.native = []
  assert.equal(compareInputs(missingNative, missingNative).completeInputs, false)
  const forgedFile = inputs(); forgedFile.producer.files[0].sha256 = 'placeholder'
  assert.equal(compareInputs(forgedFile, forgedFile).completeInputs, false)
  const missingSelection = inputs(); delete missingSelection.selection.hostBaseline
  assert.equal(compareInputs(missingSelection, missingSelection).completeInputs, false)
  const mutation = inputs(); mutation.lane = 'mutation'
  assert.ok(inputProblems(mutation).includes('mutation lane requires comparison base commit identity'))
})
test('only authenticated successful observed producer evidence is trusted', () => {
  assert.equal(verify().trusted, true)
  assert.equal(verify(envelope(), {policy: undefined}).trusted, false)
  assert.equal(verify(envelope(), {policy: {...policy, keys: {}}}).trusted, false)
  assert.equal(verify(envelope(), {artifactBytes: Buffer.from('changed')}).trusted, false)
  assert.equal(verify(envelope(), {artifactBytes: undefined}).trusted, false)
  assert.equal(verify(envelope(), {policy: {...policy, producers: {}}}).trusted, false)
  assert.equal(verify(envelope(), {observation: {...observation, expired: true}}).trusted, false)
  assert.equal(verify(envelope(), {policy: {...policy, producers: []}}).trusted, false)
  const forged = envelope(); forged.payload.inputs.coverage.enabled = true
  assert.equal(verify(forged).trusted, false)
})
test('signed source identities bind to independently observed prior and current commit trees', () => {
  assert.equal(verify().trusted, true)
  for (const candidateTree of [undefined, null, 'b'.repeat(40), ['a'.repeat(40)], {}, 123]) {
    assert.equal(verify(envelope(), {observation: {...observation, candidateTree}}).trusted, false)
  }
  // Re-signing the forged claim with an allowed fixture key does not change GitHub's tree.
  assert.equal(verify(envelope(payload => {payload.inputs.candidateTree = 'b'.repeat(40)})).trusted, false)
  for (const field of ['headSha', 'workflowCommit']) for (const value of [undefined, ['f'.repeat(40)], {}, 123]) {
    const independent = {...observation, [field]: value}
    const signed = envelope(payload => {payload.provenance[field] = value ?? null})
    assert.equal(verify(signed, {observation: independent}).trusted, false, field)
  }
  const current = {candidateSha: '1'.repeat(40), candidateTree: 'a'.repeat(40)}
  const verdict = currentObservation => shadowVerdict({candidateSha: '1'.repeat(40), currentInputs: inputs(),
    currentObservation, priorReceipt: envelope(), artifactBytes: bytes, observation, policy})
  assert.equal(verdict(current).wouldReuse, true)
  for (const invalid of [undefined, {}, {...current, candidateSha: '2'.repeat(40)},
    {...current, candidateSha: [current.candidateSha]}, {...current, candidateTree: 'b'.repeat(40)},
    {...current, candidateTree: [current.candidateTree]}, {...current, candidateTree: 123}]) {
    const result = verdict(invalid)
    assert.equal(result.trusted, false)
    assert.equal(result.wouldReuse, false)
    assert.equal(result.reuseAuthorized, false)
    assert.equal(result.requiredWorkSkipped, false)
  }
  const packages = envelope(payload => {payload.inputs.lane = 'packages';
    payload.inputs.commitInputs.candidateCommit = 'b'.repeat(40)
    payload.packageProof = {artifactDigest: digest(bytes), consumedDigest: digest(bytes)}})
  assert.equal(verify(packages).trusted, false)
  const mutation = envelope(payload => {payload.inputs.lane = 'mutation';
    payload.inputs.commitInputs.candidateCommit = observation.headSha; payload.inputs.commitInputs.baseCommit = 'b'.repeat(40)})
  assert.equal(verify(mutation).trusted, false)
  assert.equal(verify(mutation, {observation: {...observation, baseSha: 'c'.repeat(40)}}).trusted, false)
  assert.equal(verify(mutation, {observation: {...observation, baseSha: 'b'.repeat(40)}}).trusted, true)
})

test('failure, cancellation, skip, missing or duplicate invocation cannot be reused', () => {
  for (const status of ['failure', 'cancelled', 'skipped', null])
    assert.equal(verify(envelope(), {observation: {...observation, conclusion: status}}).trusted, false)
  for (const status of ['skipped', 'cancelled', 'running'])
    assert.equal(verify(envelope(payload => {payload.invocations[0].status = status})).trusted, false)
  assert.equal(verify(envelope(payload => {payload.status = 'FAIL'})).trusted, false)
  assert.equal(verify(envelope(payload => {payload.invocations.pop()})).trusted, false)
  assert.equal(verify(envelope(payload => {payload.invocations.push({...payload.invocations[0]})})).trusted, false)
})
test('all observed producer identity fields must match signed provenance', () => {
  for (const field of ['repository', 'runId', 'runAttempt', 'jobId', 'workflowPath', 'workflowCommit', 'headSha', 'artifactId'])
    assert.equal(verify(envelope(), {observation: {...observation, [field]: 'different'}}).trusted, false, field)
})
test('package consumption stays bound to the transferred artifact', () => {
  assert.equal(verify(envelope(payload => {payload.inputs.lane = 'packages'; payload.inputs.commitInputs.candidateCommit = 'f'.repeat(40)})).trusted, false)
  assert.equal(verify(envelope(payload => {payload.inputs.lane = 'packages'; payload.inputs.commitInputs.candidateCommit = 'f'.repeat(40); payload.packageProof = {
    artifactDigest: digest(bytes), consumedDigest: digest(bytes)}})).trusted, true)
  assert.equal(verify(envelope(payload => {payload.inputs.lane = 'packages'; payload.inputs.commitInputs.candidateCommit = 'f'.repeat(40); payload.packageProof = {
    artifactDigest: digest(bytes), consumedDigest: 'different'}})).trusted, false)
})
test('even a trusted same-input result issues only a new candidate-specific shadow verdict', () => {
  const result = shadowVerdict({candidateSha: '1'.repeat(40), currentInputs: inputs(),
    currentObservation: {candidateSha: '1'.repeat(40), candidateTree: 'a'.repeat(40)},
    priorReceipt: envelope(), artifactBytes: bytes, observation, policy})
  assert.equal(result.candidateSha, '1'.repeat(40))
  assert.equal(result.wouldReuse, true)
  assert.equal(result.reuseAuthorized, false)
  assert.equal(result.requiredWorkSkipped, false)
})
test('unsigned branch observations are diagnostic only, including matching trees', () => {
  const item = {candidateSha: '1'.repeat(40), inputs: inputs(), fingerprint: fingerprint(inputs())}
  assert.equal(compareObservations(item, item).sameInputs, true)
  assert.equal(compareObservations(item, item).trusted, false)
  assert.equal(compareObservations(item, item).wouldReuse, false)
  assert.equal(compareObservations(item, {...item, fingerprint: 'forged'}).sameInputs, false)
})
