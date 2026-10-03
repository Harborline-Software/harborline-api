import test from 'node:test'
import assert from 'node:assert/strict'
import {safeFailure} from '../platform-feed-qualification.mjs'

test('qualification failure evidence retains fixed causal codes without raw credentials, paths or URLs', () => {
  const error = {status: 127, code: 'ENOENT', signal: 'SIGTERM',
    message: 'private-token https://user:private-password@example.invalid',
    stdout: 'private-token /private/path error NU1301',
    stderr: Buffer.from('/private/node: error while loading shared libraries: libatomic.so.1: cannot open shared object file\nhttps://private.invalid NU1301 MSB1009 NETSDK1045')}
  assert.deepEqual(safeFailure(error), {kind: 'command-exit', exitCode: 127, osCode: 'ENOENT', signal: 'SIGTERM',
    diagnosticCodes: ['NU1301', 'MSB1009', 'NETSDK1045'], missingSharedLibrary: 'libatomic.so.1'})
  assert.deepEqual(safeFailure({code: 'EPRIVATE_CREDENTIAL', signal: 'private-token', stdout: {secret: 'private-token'}}),
    {kind: 'validation-or-spawn'})
  assert.deepEqual(safeFailure(new Error('https://user:private-password@example.invalid/private-token')), {kind: 'validation-or-spawn'})
})
