import test from 'node:test'
import assert from 'node:assert/strict'
import {existsSync, writeFileSync} from 'node:fs'
import {pathToFileURL} from 'node:url'
const {safeContainerState, diagnosticContainerRun, crashSignatures} = await import(process.env.HARBORLINE_DIAGNOSTICS_MODULE
  ? pathToFileURL(process.env.HARBORLINE_DIAGNOSTICS_MODULE).href : '../platform-feed-crash-diagnostics.mjs')

const cid = 'a'.repeat(64)
// Literal Docker-format fixture; these fields are independent of the implementation.
const state = {exitCode: 139, oomKilled: false, running: false, memoryLimitBytes: 0,
  memorySwapLimitBytes: 0, pidsLimit: 256, nanoCpus: 4000000000}
const expected = {available: true, oomKilled: false, running: false, peakMemoryObserved: false,
  peakPidsObserved: false, exitCode: 139, memoryLimitBytes: 0, memorySwapLimitBytes: 0,
  nanoCpus: 4000000000, pidsLimit: 256}

test('container state retains only bounded numeric and Boolean fields, never raw inspect data', () => {
  assert.deepEqual(safeContainerState(JSON.stringify({...state, secret: '/private/token https://user:password@secret.invalid',
    Error: 'secret', Path: '/private/path', Args: ['secret']})), expected)
  assert.deepEqual(safeContainerState(JSON.stringify({...state, oomKilled: true, exitCode: 137,
    memorySwapLimitBytes: -1, pidsLimit: null})), {...expected, oomKilled: true, exitCode: 137, memorySwapLimitBytes: -1, pidsLimit: null})
  assert.deepEqual(safeContainerState(JSON.stringify({...state,
    errorMessage: 'OCI private-token /private/path https://private.invalid: Resource temporarily unavailable'})),
    {...expected, runtimeObservedSignatures: ['thread-or-process-resource-unavailable']})
  for (const invalid of [{exitCode: 256}, {exitCode: -1}, {pidsLimit: -2}, {pidsLimit: 1.5},
    {nanoCpus: Number.MAX_SAFE_INTEGER + 1}, {oomKilled: 'secret'}, {running: null}, {memoryLimitBytes: -1},
    {memorySwapLimitBytes: -2}, {memoryLimitBytes: 'secret'}]) {
    assert.throws(() => safeContainerState(JSON.stringify({...state, ...invalid})))
  }
  for (const raw of ['{"exitCode":1e400}', '[]', 'null', 'secret'.repeat(1000), '{bad']) assert.throws(() => safeContainerState(raw))
})

test('crash classification emits fixed observed signatures without inferring a cause from exit codes', () => {
  assert.deepEqual(crashSignatures('private-token https://private.invalid /private/path status 139'), [])
  assert.deepEqual(crashSignatures('Segmentation fault (core dumped)\nSystem.OutOfMemoryException: /private/token\n'
    + 'Cannot allocate memory\npthread_create failed: Resource temporarily unavailable\nNo space left on device'),
  ['segmentation-fault', 'managed-out-of-memory', 'allocation-failure', 'thread-or-process-resource-unavailable', 'disk-full'])
  assert.deepEqual(crashSignatures('Aborted (core dumped)\nStack overflow\nEnvironment.FailFast\nAssertion failed\nPermission denied'),
    ['abort', 'stack-overflow', 'runtime-fail-fast', 'runtime-assertion', 'access-denied'])
  assert.deepEqual(crashSignatures('Failed to create CoreCLR, HRESULT: 0x8007000E\nSystem.AccessViolationException: private-token'),
    ['access-violation', 'allocation-hresult'])
  assert.deepEqual(crashSignatures('0x80070008'), ['allocation-hresult'])
  assert.deepEqual(crashSignatures('secret0x8007000Esecret'), [])
  assert.deepEqual(crashSignatures('Fatal error. Internal CLR error. (0x80131506)'), ['runtime-fail-fast', 'internal-clr-error'])
  assert.deepEqual(crashSignatures('Error while checking for terminated children. errno = 10\n'
    + 'System.Environment.FailFast\nSystem.Diagnostics.ProcessWaitState.CheckChildren'),
    ['runtime-fail-fast', 'child-process-wait-failure', 'child-process-wait-frame'])
  assert.deepEqual(crashSignatures('System.Threading.Thread.StartCore\nResource temporarily unavailable'),
    ['thread-or-process-resource-unavailable', 'thread-creation-frame'])
})

const workload = ['run', '--rm', '--platform=linux/amd64', '--read-only', '--cap-drop=ALL',
  '--security-opt=no-new-privileges', '--pids-limit=256', '--cpus=4', '--user', '1001:1001',
  '--tmpfs', '/tmp:rw,nosuid,nodev,size=1073741824', '--network', 'bridge', '--workdir', '/platform',
  'mcr.microsoft.com/dotnet/sdk@sha256:' + 'b'.repeat(64), 'dotnet', 'restore', '/platform/project.csproj',
  '--packages', '/packages', '--configfile', '/platform/NuGet.Config', '-nodeReuse:false', '-maxcpucount:4']

function fixture({failure, inspectFailure, cleanupFailure, observerFailure, invalidCid, missingCid} = {}) {
  const calls = [], observations = [], options = {workloadMarker: true}
  let ownedFile
  const run = (command, args, childOptions) => {
    assert.equal(command, 'docker'); calls.push(args)
    if (args[0] === 'run') {
      assert.equal(childOptions, options)
      assert.equal(args[1], '--cidfile'); ownedFile = args[2]
      assert.deepEqual(args.slice(3), workload.slice(2)) // Every workload/security/image argument unchanged.
      if (!missingCid) writeFileSync(ownedFile, invalidCid ?? cid)
      if (failure) throw failure
      return 'workload result'
    }
    assert.equal(childOptions.timeout, 5000); assert.equal(childOptions.maxBuffer, 4096)
    for (const key of ['GH_TOKEN', 'GITHUB_TOKEN', 'GH_ENTERPRISE_TOKEN', 'GITHUB_ENTERPRISE_TOKEN',
      'ACTIONS_RUNTIME_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN']) assert.equal(childOptions.env[key], undefined)
    assert.equal(args.at(-1), cid)
    if (args[0] === 'inspect') {
      assert.equal(args[1], '--format')
      assert.ok(!args[2].includes('.Config.Env'))
      if (inspectFailure) throw inspectFailure
      return JSON.stringify({...state, secret: 'private-token /private/path https://private.invalid'})
    }
    assert.deepEqual(args, ['rm', '--force', cid])
    if (cleanupFailure) throw cleanupFailure
    return ''
  }
  const invoke = diagnosticContainerRun({run, observe: observation => {
    observations.push(observation); if (observerFailure) throw observerFailure
  }})
  return {calls, observations, invoke: () => invoke('docker', workload, options), get ownedFile() {return ownedFile}}
}

test('successful workload retains exact arguments and result, bounds inspection, removes only owned container', () => {
  const f = fixture()
  assert.equal(f.invoke(), 'workload result')
  assert.deepEqual(f.observations, [{...expected, cleanupSucceeded: true}])
  assert.equal(f.calls.length, 3)
  assert.equal(existsSync(f.ownedFile), false)
})

test('original workload failure survives inspection, cleanup and observer failures', () => {
  const original = Object.assign(new Error('private-token /private/path'), {status: 139, stdout: 'original stdout', stderr: 'original stderr'})
  for (const faults of [{}, {inspectFailure: new Error('private inspect secret')},
    {cleanupFailure: new Error('private cleanup secret')}, {observerFailure: new Error('private observer secret')},
    {inspectFailure: new Error('inspect'), cleanupFailure: new Error('cleanup'), observerFailure: new Error('observer')}]) {
    const f = fixture({failure: original, ...faults})
    assert.throws(f.invoke, error => error === original && error.status === 139 && error.stderr === 'original stderr')
    assert.equal(existsSync(f.ownedFile), false)
    assert.equal(f.calls.filter(args => args[0] === 'rm').length, 1)
    assert.equal(JSON.stringify(f.observations).includes('private'), false)
  }
})

test('diagnostic failures do not turn successful workloads into failures', () => {
  const f = fixture({inspectFailure: new Error('inspect secret'), cleanupFailure: new Error('cleanup secret'), observerFailure: new Error('observer secret')})
  assert.equal(f.invoke(), 'workload result')
  assert.deepEqual(f.observations, [{available: false, cleanupSucceeded: false}])
})

test('missing or malicious owned CID never targets a foreign container', () => {
  for (const faults of [{missingCid: true}, {invalidCid: 'foreign-container'}, {invalidCid: cid + '\nother'}, {invalidCid: 'b'.repeat(129)}]) {
    const f = fixture(faults)
    assert.equal(f.invoke(), 'workload result')
    assert.equal(f.calls.length, 1)
    assert.deepEqual(f.observations, [{available: false, cleanupSucceeded: false}])
    assert.equal(existsSync(f.ownedFile), false)
  }
})

test('non-container commands retain their original invocation', () => {
  const args = ['version'], options = {marker: true}
  const invoke = diagnosticContainerRun({run: (command, actual, passed) => {
    assert.equal(command, 'docker'); assert.equal(actual, args); assert.equal(passed, options); return 'version'
  }, observe: () => assert.fail('non-container observation')})
  assert.equal(invoke('docker', args, options), 'version')
})
