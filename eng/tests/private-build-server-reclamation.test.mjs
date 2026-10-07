import {test} from 'node:test'
import assert from 'node:assert/strict'
import {runAfterPrivateReclamation} from '../private-build-server-reclamation.mjs'

test('ordinary/native host execution has no shutdown command and retains its result', () => {
  const result = {rawOutput: 'unchanged', exitCode: 1}
  let calls = 0
  assert.equal(runAfterPrivateReclamation(() => { calls++; return result },
    () => assert.fail('native/shared host must not invoke shutdown'), {}), result)
  assert.equal(calls, 1)
})

test('private host test starts only after its fixed immutable cleanup command succeeds', () => {
  const events = []
  const result = runAfterPrivateReclamation(() => { events.push('test'); return 'raw coverage output' },
    (id, command, args) => {
      // Oracle: fixed, credential-free immutable container executor contract.
      assert.deepEqual([id, command, args], ['private-build-server-reclamation', 'python3',
        ['-I', '/opt/trusted/reclaim.py', 'exact-clone-host-tests']])
      events.push('reclaimed')
      return {passed: true, exitCode: 0}
    }, {HARBORLINE_PRIVATE_BUILD_RECLAIM: '1'})
  assert.deepEqual(events, ['reclaimed', 'test'])
  assert.equal(result, 'raw coverage output')
})

test('shutdown error refuses test execution instead of reporting reclaimed memory', () => {
  const failure = new Error('owned server did not drain')
  assert.throws(() => runAfterPrivateReclamation(() => assert.fail('tests must not start'),
    () => { throw failure }, {HARBORLINE_PRIVATE_BUILD_RECLAIM: '1'}), error => error === failure)
})

test('a recorded failed or incomplete cleanup step refuses host tests', () => {
  for (const result of [{passed: false, exitCode: 1}, {passed: true, exitCode: null}, undefined]) {
    assert.throws(() => runAfterPrivateReclamation(() => assert.fail('tests must not start'),
      () => result, {HARBORLINE_PRIVATE_BUILD_RECLAIM: '1'}), /refusing host tests/)
  }
})

test('invalid resource activation refuses both commands and tests', () => {
  for (const value of ['0', '', 'true', '/host/script']) {
    assert.throws(() => runAfterPrivateReclamation(() => assert.fail('test started'),
      () => assert.fail('command started'), {HARBORLINE_PRIVATE_BUILD_RECLAIM: value}),
    /Invalid private server reclamation profile/)
  }
})
