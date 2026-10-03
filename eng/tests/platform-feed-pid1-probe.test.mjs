import test from 'node:test'
import assert from 'node:assert/strict'
import {existsSync, mkdirSync, writeFileSync} from 'node:fs'
import path from 'node:path'
import {pathToFileURL} from 'node:url'
const {probePidOneRestore} = await import(process.env.HARBORLINE_PID1_MODULE
  ? pathToFileURL(process.env.HARBORLINE_PID1_MODULE).href : '../platform-feed-pid1-probe.mjs')
import {safeFailure} from '../platform-feed-qualification.mjs'
const image = 'mcr.microsoft.com/dotnet/sdk@sha256:' + 'b'.repeat(64), commit = 'c'.repeat(40)
const source = path.resolve('synthetic-source'), tools = path.resolve('synthetic-tools'), cache = path.resolve('synthetic-cache')
const args = ['run', '--rm', '--platform=linux/amd64', '--read-only', '--cap-drop=ALL', '--security-opt=no-new-privileges',
  '--pids-limit=256', '--cpus=4', '--user', '1001:1001', '--tmpfs', '/tmp:rw,nosuid,nodev,size=1073741824',
  '-e', 'HOME=/tmp', '-e', 'DOTNET_CLI_HOME=/tmp', '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
  '--mount', `type=bind,source=${source},target=/platform`, '--mount', `type=bind,source=${tools},target=/tool,readonly`,
  '--mount', `type=bind,source=${cache},target=/packages`, '--network', 'bridge', '--workdir', '/platform', image,
  'dotnet', 'restore', '/platform/project.csproj', '--packages', '/packages', '--configfile', '/platform/NuGet.Config',
  '-p:Configuration=Release', '-nodeReuse:false', '-maxcpucount:4']

test('PID1 probe makes three isolated interleaved pairs, changes only init and owned input mounts, never grants qualification', () => {
  const cases = [], clones = [], packageRoots = [], evidence = [], cid = 'd'.repeat(64)
  const run = (command, actual, options) => {
    for (const key of ['GH_TOKEN', 'GITHUB_TOKEN', 'ACTIONS_RUNTIME_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN']) assert.equal(options.env[key], undefined)
    if (command === 'git') {
      if (actual.includes('clone')) {clones.push(actual.at(-1)); mkdirSync(actual.at(-1)); return ''}
      assert.deepEqual(actual.slice(-2), ['rev-parse', 'HEAD']); return commit + '\n'
    }
    assert.equal(command, 'docker')
    if (actual[0] === 'inspect') return JSON.stringify({exitCode: 0, oomKilled: false, running: false,
      memoryLimitBytes: 0, memorySwapLimitBytes: 0, pidsLimit: 256, nanoCpus: 4000000000, errorMessage: ''})
    if (actual[0] === 'rm') return ''
    assert.equal(actual[0], 'run'); assert.equal(actual[1], '--cidfile'); writeFileSync(actual[2], cid)
    const init = actual.includes('--init'), normalized = ['run', '--rm', ...actual.slice(3).filter(value => value !== '--init')]
    for (let i = 0; i < normalized.length; i++) {
      if (normalized[i].startsWith('type=bind,source=') && normalized[i].endsWith(',target=/platform')) {
        const root = normalized[i].slice('type=bind,source='.length, -',target=/platform'.length)
        assert.equal(root, clones.at(-1)); assert.equal(existsSync(root), true)
        normalized[i] = `type=bind,source=${source},target=/platform`
      } else if (normalized[i].startsWith('type=bind,source=') && normalized[i].endsWith(',target=/packages')) {
        const root = normalized[i].slice('type=bind,source='.length, -',target=/packages'.length)
        packageRoots.push(root); assert.equal(existsSync(root), true)
        normalized[i] = `type=bind,source=${cache},target=/packages`
      }
    }
    assert.deepEqual(normalized, args)
    assert.equal(options.timeout, 180000); cases.push(init)
    if (!init) throw Object.assign(new Error('private-token'), {status: 139,
      stderr: 'Error while reaping child. errno = 10\n at System.Environment.FailFast(System.String)\n at System.Diagnostics.ProcessWaitState.TryReapChild(Boolean)'})
    return ''
  }
  const results = probePidOneRestore({args, image, commit, run, classifyFailure: safeFailure,
    observe: (stage, details) => evidence.push({stage, ...details})})
  assert.deepEqual(cases, [false, true, true, false, false, true])
  assert.equal(new Set(clones).size, 6); assert.equal(new Set(packageRoots).size, 6)
  for (const root of [...clones, ...packageRoots]) assert.equal(existsSync(root), false)
  assert.deepEqual(results.map(({variant, succeeded}) => ({variant, succeeded})), [
    {variant: 'direct-pid1', succeeded: false}, {variant: 'docker-init', succeeded: true},
    {variant: 'docker-init', succeeded: true}, {variant: 'direct-pid1', succeeded: false},
    {variant: 'direct-pid1', succeeded: false}, {variant: 'docker-init', succeeded: true},
  ])
  assert.deepEqual(results[0].failure, {kind: 'command-exit', exitCode: 139,
    observedSignatures: ['runtime-fail-fast', 'child-process-reap-failure', 'child-process-wait-frame']})
  assert.deepEqual(evidence.at(-1), {stage: 'pid1-probe-complete', cases: 6, productionQualificationPassed: false,
    productionSettingsChanged: false, causalConclusionEstablished: false})
  assert.equal(JSON.stringify(evidence).includes('private-token'), false)
  assert.equal(JSON.stringify(evidence).includes(source), false)
  assert.equal(JSON.stringify(evidence).includes(cid), false)
})

test('probe observer failure and clone failure cannot publish raw errors or skip remaining cases', () => {
  const recorded = [], error = Object.assign(new Error('private-token https://private.invalid /private/path'), {status: 1})
  const results = probePidOneRestore({args, image, commit, run: () => {throw error}, classifyFailure: safeFailure,
    observe: (stage, details) => {recorded.push({stage, ...details}); throw new Error('observer secret')}})
  assert.equal(results.length, 6)
  assert.ok(results.every(result => !result.succeeded && result.failure.exitCode === 1))
  assert.equal(JSON.stringify(recorded).includes('private'), false)
})

test('unsupported restore context refuses before running any command', () => {
  for (const options of [{image: 'unapproved'}, {commit: 'wrong'}, {args: ['run', '--rm', image, 'sh', '-c', 'secret']},
    {args: [...args, '--init']}, {args: args.map(value => value === '/platform/project.csproj' ? '/platform/../secret.csproj' : value)}]) {
    assert.throws(() => probePidOneRestore({args, image, commit, classifyFailure: safeFailure,
      run: () => assert.fail('unsafe command'), ...options}))
  }
})
