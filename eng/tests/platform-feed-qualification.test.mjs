import test from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import {safeFailure} from '../platform-feed-qualification.mjs'

test('API restore uses the GA image while platform packing keeps its own profile', () => {
  const source = readFileSync(new URL('../platform-feed-qualification.mjs', import.meta.url), 'utf8')
  assert.match(source, /const apiImage = 'mcr\.microsoft\.com\/dotnet\/sdk@sha256:7913685c883a29dd8ba79da96172f5c6b29ed9be706ebe95935e3ef45f19c9c1'/)
  assert.match(source, /prepareContainer\(\{apiRoot: root, platform, pin/)
  assert.match(source, /'--workdir', clone, apiImage, 'dotnet', 'restore', 'Harborline\.Api\.slnx'/)
})

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

test('qualification records bounded crash signatures but does not infer OOM from exit 139', () => {
  assert.deepEqual(safeFailure({status: 139, stderr: 'private-token /private/path https://private.invalid'}),
    {kind: 'command-exit', exitCode: 139})
  assert.deepEqual(safeFailure({status: 139, stderr: 'Segmentation fault (core dumped)\nSystem.OutOfMemoryException: private-token'}),
    {kind: 'command-exit', exitCode: 139, observedSignatures: ['segmentation-fault', 'managed-out-of-memory']})
  assert.deepEqual(safeFailure({status: 137}), {kind: 'command-exit', exitCode: 137})
})
