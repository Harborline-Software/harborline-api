import test from 'node:test'
import assert from 'node:assert/strict'
import {execFileSync, spawnSync} from 'node:child_process'
import {copyFileSync, mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {handoffRestored} from '../exact-clone-platform-feed.mjs'

const root = path.resolve(import.meta.dirname, '../..')
const publicUrl = 'https://github.com/Harborline-Software/harborline-platform.git'
const git = (cwd, ...args) => execFileSync('git', ['-C', cwd, ...args], {encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']}).trim()
function fixture(t) {
  // realpath: on macOS tmpdir() is /var/..., a symlink to /private/var, and the builder stub compares
  // process.argv[1] (resolved through the symlink) with import.meta.url (the real path) (ticket 345).
  const directory = mkdtempSync(path.join(realpathSync(tmpdir()), 'exact-feed-test-'))
  t.after(() => {
    assert.equal(path.dirname(directory), realpathSync(tmpdir()))
    rmSync(directory, {recursive: true, force: true})
  })
  const apiRoot = path.join(directory, 'api')
  const platform = path.join(directory, 'harborline-platform')
  const scratch = path.join(directory, 'scratch')
  for (const dir of [apiRoot, platform, scratch]) mkdirSync(dir)
  git(platform, 'init', '-q')
  writeFileSync(path.join(platform, 'source.txt'), 'pinned\n')
  git(platform, 'add', '.')
  const commit = message => git(platform, '-c', 'user.name=Feed test', '-c', 'user.email=feed@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '--no-verify', '-qam', message)
  commit('pin')
  const pin = {commit: git(platform, 'rev-parse', 'HEAD'), repository: 'Harborline-Software/harborline-platform'}
  const remote = path.join(directory, 'remote.git')
  git(directory, 'clone', '--bare', platform, remote)
  // Exercise real git clone/checkout without depending on network availability.
  const count = Number(process.env.GIT_CONFIG_COUNT ?? 0)
  const env = {...process.env, GIT_CONFIG_COUNT: String(count + 1),
    [`GIT_CONFIG_KEY_${count}`]: `url.${remote.replaceAll('\\', '/')}.insteadOf`,
    [`GIT_CONFIG_VALUE_${count}`]: publicUrl}
  delete env.HARBORLINE_PLATFORM_REPO
  // This fixture substitutes its own producer and checkout; a surrounding real-job handoff
  // belongs to another consumer. Dedicated same-job tests still bind and reject explicit inputs.
  delete env.HARBORLINE_PLATFORM_FEED_HANDOFF_PATH
  delete env.HARBORLINE_PLATFORM_FEED_HANDOFF_SHA256
  return {apiRoot, platform, scratch, pin, env, commit, directory}
}
const resolver = async () => (await import('../exact-clone-platform-feed.mjs')).resolvePlatformCheckout

test('clean explicit checkout at the pin wins over the sibling', async t => {
  const f = fixture(t)
  const explicit = path.join(f.directory, 'explicit')
  git(f.directory, 'clone', f.platform, explicit)
  f.env.HARBORLINE_PLATFORM_REPO = explicit
  const result = (await resolver())(f)
  assert.equal(result.platform, explicit)
  assert.equal(result.source, 'HARBORLINE_PLATFORM_REPO')
})
for (const state of ['dirty', 'wrong-commit']) {
  test(`explicit ${state} checkout falls back to a clean public clone at the pin`, async t => {
    const f = fixture(t)
    f.env.HARBORLINE_PLATFORM_REPO = f.platform
    writeFileSync(path.join(f.platform, 'source.txt'), 'changed\n')
    if (state === 'wrong-commit') f.commit('later')
    const before = git(f.platform, 'status', '--porcelain')
    const head = git(f.platform, 'rev-parse', 'HEAD')
    const result = (await resolver())(f)
    assert.equal(result.platform, path.join(f.scratch, 'platform'))
    assert.equal(result.source, 'public-clone')
    assert.equal(result.reason, state)
    assert.equal(git(result.platform, 'rev-parse', 'HEAD'), f.pin.commit)
    assert.equal(git(result.platform, 'status', '--porcelain'), '')
    assert.equal(git(f.platform, 'status', '--porcelain'), before)
    assert.equal(git(f.platform, 'rev-parse', 'HEAD'), head)
  })
}
test('without an environment override the source API sibling is used', async t => {
  const f = fixture(t)
  const result = (await resolver())(f)
  assert.equal(result.platform, f.platform)
  assert.equal(result.source, 'sibling')
})
test('missing checkout falls back to the public repository', async t => {
  const f = fixture(t)
  f.env.HARBORLINE_PLATFORM_REPO = path.join(f.directory, 'missing')
  const result = (await resolver())(f)
  assert.equal(result.source, 'public-clone')
  assert.equal(result.reason, 'missing')
  assert.equal(git(result.platform, 'rev-parse', 'HEAD'), f.pin.commit)
})
test('unavailable local checkout and public clone report platform-checkout-unavailable', async t => {
  const f = fixture(t)
  f.env.HARBORLINE_PLATFORM_REPO = path.join(f.directory, 'missing')
  const index = Number(f.env.GIT_CONFIG_COUNT) - 1
  f.env[`GIT_CONFIG_KEY_${index}`] = `url.${path.join(f.directory, 'unavailable.git').replaceAll('\\', '/')}.insteadOf`
  const resolve = await resolver()
  assert.throws(() => resolve(f), /platform-checkout-unavailable: missing/)
})
test('exact-clone records platform-feed between artifact check and dotnet-restore', () => {
  const source = readFileSync(path.join(root, 'eng/run-exact-clone.mjs'), 'utf8')
  const artifacts = source.indexOf("steps.push({id: 'clone-carries-no-artifacts'")
  const feed = source.indexOf("run('platform-feed'")
  const restore = source.indexOf("run('dotnet-restore'")
  assert.ok(artifacts >= 0 && artifacts < feed && feed < restore)
  assert.match(source, /steps\.push\(step\)/)
  assert.match(source, /steps: steps\.map\(/)
  assert.match(source, /exact-clone-platform-feed\.mjs/)
  // Execute the real step recorder and pre-restore route with controlled child exits.
  const runBlock = source.slice(source.indexOf('const run ='), source.indexOf('\nlet report'))
  const route = source.slice(source.indexOf("  steps.push({id: 'clone-carries-no-artifacts'"), source.indexOf("  run('dotnet-build'"))
  for (const qualityEnabled of [false, true]) for (const exitCode of [0, 1]) {
    const steps = []
    const calls = []
    // The stub below models the no-handoff route. A surrounding job's real handoff
    // must not make its literal selection output masquerade as a handoff result.
    const fixtureProcess = {execPath: process.execPath, env: {}}
    new Function('steps', 'resolveCommand', 'observedSpawnSync', 'progressFile', 'stripAnsi', 'redactEvidence', 'process', 'clone', 'apiRoot', 'scratch', 'artifacts', 'path', 'rmSync', 'mkdirSync', 'qualityEnabled', 'handoffRestored',
      runBlock + '\n' + route)(steps, (executable, args) => ({executable, args}),
      (_id, executable, args, options) => {
        calls.push({executable, args, cwd: options.cwd})
        return {status: args[0] === 'eng/exact-clone-platform-feed.mjs' ? exitCode : 0, stdout: 'selection evidence'}
      }, '/progress-fixture.jsonl', text => text, text => text, fixtureProcess, '/clone', '/source', '/scratch', [], path,
      () => {}, () => {}, qualityEnabled, handoffRestored)
    assert.deepEqual(steps.map(step => step.id), ['clone-carries-no-artifacts', 'platform-feed', 'dotnet-restore'])
    assert.equal(steps[1].passed, exitCode === 0)
    assert.equal(steps[1].exitCode, exitCode)
    assert.match(steps[1].tail, /selection evidence/)
    assert.deepEqual(calls[0], {executable: process.execPath,
      args: ['eng/exact-clone-platform-feed.mjs', '/source', '/scratch'], cwd: '/clone'})
  }
})

test('recorder fixture remains independent of a surrounding live handoff', () => {
  // Exercise the actual fixture in a child with an external ambient context. The
  // original fixture failed with "platform feed handoff result missing or ambiguous".
  const env = {...process.env, HARBORLINE_PLATFORM_FEED_HANDOFF_PATH: path.join(tmpdir(), 'literal-outer-transfer.json'),
    HARBORLINE_PLATFORM_FEED_HANDOFF_SHA256: 'a'.repeat(64)}
  // This is a separate test invocation, not an internal child of the parent runner.
  delete env.NODE_TEST_CONTEXT
  const result = spawnSync(process.execPath, ['--test', '--test-reporter=tap',
    '--test-name-pattern=^exact-clone records platform-feed between artifact check and dotnet-restore$',
    import.meta.filename], {encoding: 'utf8', env, timeout: 10000})
  assert.equal(result.error, undefined)
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.match(result.stdout, /\btests 1\b/)
  assert.match(result.stdout, /\bfail 0\b/)
})

test('composed exact-clone handoff isolates restore and refuses consumption failure before compilation', () => {
  const source = readFileSync(path.join(root, 'eng/run-exact-clone.mjs'), 'utf8')
  const runBlock = source.slice(source.indexOf('const run ='), source.indexOf('\nlet report'))
  const route = source.slice(source.indexOf("  steps.push({id: 'clone-carries-no-artifacts'"), source.indexOf("  run('dotnet-build'"))
  for (const [restored, proofExit] of [[true, 0], [true, 1], [false, 0]]) {
    const steps = [], calls = [], directories = []
    const environment = {HARBORLINE_PLATFORM_FEED_HANDOFF_PATH: '/verified-transfer.json',
      HARBORLINE_PLATFORM_FEED_HANDOFF_SHA256: 'a'.repeat(64), NUGET_PACKAGES: '/old-global-cache'}
    const fakeProcess = {env: environment, execPath: process.execPath}
    const execute = () => new Function('steps', 'resolveCommand', 'observedSpawnSync', 'progressFile', 'stripAnsi',
      'redactEvidence', 'process', 'clone', 'apiRoot', 'scratch', 'artifacts', 'path', 'rmSync', 'mkdirSync', 'qualityEnabled', 'handoffRestored',
      runBlock + '\n' + route)(steps, (executable, args) => ({executable, args}),
      (id, executable, args, options) => {
        calls.push({id, executable, args, cache: environment.NUGET_PACKAGES})
        return {status: id === 'platform-feed-consumption' ? proofExit : 0, stdout: id === 'platform-feed'
          ? `platform-feed-handoff-result:${JSON.stringify({restored, digest: 'a'.repeat(64)})}\n` : 'controlled step evidence'}
      }, '/progress.jsonl', text => text, text => text, fakeProcess, '/scratch/clone', '/source', '/scratch', [], path,
      () => {}, directory => directories.push(directory), false, handoffRestored)
    if (proofExit === 0) execute()
    else assert.throws(execute, /Verified dependency bytes were not consumed/)
    assert.deepEqual(steps.map(step => step.id), ['clone-carries-no-artifacts', 'platform-feed', 'dotnet-restore', ...(restored ? ['platform-feed-consumption'] : [])])
    if (!restored) {
      assert.equal(environment.NUGET_PACKAGES, '/old-global-cache')
      assert.deepEqual(directories, [])
      continue
    }
    assert.equal(steps[3].passed, proofExit === 0)
    assert.equal(calls[1].cache, path.join('/scratch', 'nuget-packages'))
    assert.equal(calls[2].cache, path.join('/scratch', 'nuget-packages'))
    assert.deepEqual(calls[2].args, ['eng/platform-feed-consumption.mjs', '/scratch/clone', path.join('/scratch', 'nuget-packages')])
    assert.deepEqual(directories, [path.join('/scratch', 'nuget-packages')])
  }
})

test('handoff result matrix distinguishes unavailable fallback from restored and refuses ambiguous claims', () => {
  const digest = 'a'.repeat(64), env = {HARBORLINE_PLATFORM_FEED_HANDOFF_PATH: '/transfer.json', HARBORLINE_PLATFORM_FEED_HANDOFF_SHA256: digest}
  const step = outcome => ({passed: true, fullOutput: `platform-feed-handoff-result:${JSON.stringify(outcome)}\n`})
  assert.equal(handoffRestored(step({restored: false, reason: 'handoff-unavailable'}), env), false)
  assert.equal(handoffRestored(step({restored: true, digest}), env), true)
  assert.equal(handoffRestored({passed: false, fullOutput: ''}, env), false)
  assert.equal(handoffRestored({passed: true, fullOutput: ''}, {}), false)
  for (const invalid of [step({restored: 'true', digest}), step({restored: true, digest: 'b'.repeat(64)}),
    step({restored: true, digest: [digest]}), {passed: true, fullOutput: ''},
    {passed: true, fullOutput: 'platform-feed-handoff-result:malformed\n'},
    {passed: true, fullOutput: step({restored: true, digest}).fullOutput.repeat(2)}])
    assert.throws(() => handoffRestored(invalid, env))
})

test('feed CLI invokes the builder inside the clone with the selected checkout and preserves failure', t => {
  const f = fixture(t)
  const clone = path.join(f.scratch, 'clone')
  mkdirSync(path.join(clone, 'eng'), {recursive: true})
  copyFileSync(path.join(root, 'eng/exact-clone-platform-feed.mjs'), path.join(clone, 'eng/exact-clone-platform-feed.mjs'))
  copyFileSync(path.join(root, 'eng/platform-feed-environment.mjs'), path.join(clone, 'eng/platform-feed-environment.mjs'))
  copyFileSync(path.join(root, 'eng/same-job-platform-feed.mjs'), path.join(clone, 'eng/same-job-platform-feed.mjs'))
  writeFileSync(path.join(clone, 'eng/build-local-feed.mjs'), `
    import {writeFileSync} from 'node:fs'
    import {fileURLToPath} from 'node:url'
    export const readPin = () => (${JSON.stringify(f.pin)})
    export const assertProducers = () => {throw new Error('unavailable handoff must not inspect a bundle')}
    if (process.argv[1] === fileURLToPath(import.meta.url)) {
      writeFileSync('builder-call.json', JSON.stringify({cwd: process.cwd(), platform: process.env.HARBORLINE_PLATFORM_REPO}))
      console.error('pack diagnostic\\n'.repeat(20))
      process.exit(Number(process.env.TEST_PACK_EXIT))
    }
  `)
  for (const exitCode of [0, 1]) for (const stale of [false, true]) {
    const result = spawnSync(process.execPath, [path.join(clone, 'eng/exact-clone-platform-feed.mjs'), f.apiRoot, f.scratch], {
      cwd: f.apiRoot, encoding: 'utf8', env: {...f.env, TEST_PACK_EXIT: String(exitCode), ...(stale ? {
        HARBORLINE_PLATFORM_FEED_HANDOFF_PATH: path.join(f.directory, 'missing-transfer.json'),
        HARBORLINE_PLATFORM_FEED_HANDOFF_SHA256: 'a'.repeat(64),
        HARBORLINE_FEED_PILOT_SESSION: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
        HARBORLINE_FEED_PILOT_OWNER: String(process.pid)} : {})},
    })
    assert.equal(result.status, exitCode, result.stderr)
    if (exitCode === 0) {
      const matches = [...result.stdout.matchAll(/^platform-feed-handoff-result:(.*)$/gm)]
      assert.equal(matches.length, 1)
      assert.deepEqual(JSON.parse(matches[0][1]), {restored: false, reason: stale ? 'handoff-unavailable' : 'not-requested'})
    }
    assert.deepEqual(JSON.parse(readFileSync(path.join(clone, 'builder-call.json'), 'utf8')), {cwd: clone, platform: f.platform})
    const tail = (result.stdout + result.stderr).trimEnd().split('\n').slice(-14).join('\n')
    assert.match(tail, /platform-feed: used sibling .* \(clean checkout\)/)
  }
})
