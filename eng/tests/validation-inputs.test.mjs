import test from 'node:test'
import assert from 'node:assert/strict'
import {execFileSync} from 'node:child_process'
import {mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {collectInputs, persistInputShadow} from '../validation-inputs.mjs'
import {observeNuGetRoot} from '../validation-nuget-root.mjs'
import {beforeCompile, afterCompile} from '../validation-compiler-capture.mjs'

test('parent resolution state binds real compiler snapshots through collection with unset and preset environments', t => {
  const root = mkdtempSync(path.join(tmpdir(), 'validation-root-state-'))
  t.after(() => rmSync(root, {recursive: true, force: true}))
  const git = (...args) => execFileSync('git', ['-C', root, ...args], {stdio: 'pipe'})
  git('init', '--quiet'); git('config', 'user.name', 'Fixture'); git('config', 'user.email', 'fixture@example.invalid')
  writeFileSync(path.join(root, 'global.json'), '{"sdk":{"version":"11.0.100"}}')
  git('add', '.'); git('commit', '--quiet', '-m', 'fixture')
  const project = path.join(root, 'app'), packages = path.join(root, 'packages'), other = path.join(root, 'other-packages')
  for (const directory of [project, packages, other]) mkdirSync(directory)
  writeFileSync(path.join(packages, 'provider.dll'), 'abc')
  writeFileSync(path.join(project, 'project.csproj'), '<Project />')
  writeFileSync(path.join(project, 'source.cs'), 'abc')
  const argsFile = path.join(project, 'validation-compiler.args')
  const session = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
  writeFileSync(argsFile, `/reference:${path.join(packages, 'provider.dll')}\n${path.join(project, 'source.cs')}\n`)
  writeFileSync(path.join(project, 'validation-compiler.context'),
    `project=${path.join(project, 'project.csproj')}\nframework=net11.0\nconfiguration=Release\nskipCompilerExecution=\ndesignTimeBuild=\n`)
  writeFileSync(path.join(project, 'validation-compiler.inputs'),
    [path.join(project, 'project.csproj'), path.join(project, 'source.cs'), path.join(packages, 'provider.dll')].join('\n'))
  const captureEnv = {NUGET_PACKAGES: packages}
  const capture = {argsFile, source: root, captureSession: session, env: captureEnv}
  beforeCompile(capture); assert.equal(afterCompile(capture).beforeMatches, true)
  for (const preset of [false, true]) for (const outcome of ['success', 'exception', 'empty', 'relative', 'ambiguous']) {
    const env = {HARBORLINE_VALIDATION_CAPTURE_SESSION: session, ...(preset ? {NUGET_PACKAGES: packages} : {})}
    const state = observeNuGetRoot({cwd: root, env, run: () => {
      if (outcome === 'exception') throw new Error('native query unavailable')
      return outcome === 'success' ? `global-packages: ${packages}\n` : outcome === 'relative'
        ? 'global-packages: relative\n' : outcome === 'ambiguous'
          ? `global-packages: ${packages}\nglobal-packages: ${other}\n` : ''
    }})
    const collect = packageRootResolution => collectInputs({apiRoot: root, clone: root,
      hostBaseline: 'eng/baselines/host-test-baseline.json', coverage: false, quality: false, env,
      packageRootResolution, commandVersion: () => '11.0.100'})
    const observed = collect(state)
    const compiler = observed.inputs.dependencies.compilerInputs[0]
    assert.equal(compiler.completeCompilerObservation, outcome === 'success', `${preset}/${outcome}`)
    assert.equal(observed.inputs.unknownInputs.some(reason => reason.startsWith('NuGet package root')), outcome !== 'success')
    if (preset && outcome !== 'success') assert.equal(env.NUGET_PACKAGES, packages, 'build override stays available without granting capture approval')
    assert.equal(observed.reuseAuthorized, false)
    if (outcome === 'success') {
      assert.equal(compiler.files.find(file => file.identity === 'packages/provider.dll').sha256,
        'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
      for (const invalid of [undefined, {status: 'unavailable'}, {status: 'resolved', root: other},
        {status: 'resolved', root: [packages]}, {status: 'resolved', root: 'relative'}])
        assert.equal(collect(invalid).inputs.dependencies.compilerInputs[0].completeCompilerObservation, false)
      env.NUGET_PACKAGES = other
      assert.equal(collect(state).inputs.dependencies.compilerInputs[0].completeCompilerObservation, false,
        'environment changed after independently observed resolution')
    }
  }
  const runner = readFileSync(path.resolve(import.meta.dirname, '../run-exact-clone.mjs'), 'utf8')
  assert.match(runner, /packageRootResolution = observeNuGetRoot\(\{cwd: clone\}\)/)
  assert.match(runner, /persistInputShadow\(\{apiRoot, clone,[\s\S]*quality: qualityEnabled, packageRootResolution\}\)/)
})

test('collector observes restored dependency/native bytes, tools and coverage without running a build', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'validation-inputs-test-'))
  try {
    const git = (...args) => execFileSync('git', ['-C', root, ...args], {stdio: 'pipe'})
    git('init', '--quiet'); git('config', 'user.name', 'Fixture'); git('config', 'user.email', 'fixture@example.invalid')
    const write = (file, value) => {mkdirSync(path.dirname(path.join(root, file)), {recursive: true}); writeFileSync(path.join(root, file), value)}
    write('global.json', '{"sdk":{"version":"11.0.100"}}')
    write('eng/example.mjs', '// fixture\n')
    write('package-lock.json', '{"lockfileVersion":3,"packages":{"":{"name":"fixture"}}}')
    git('add', '.'); git('commit', '--quiet', '-m', 'fixture')
    write('app/obj/project.assets.json', JSON.stringify({targets: {'net11.0': {'provider/1.2.3': {runtime: {'provider.dll': {}}}}},
      libraries: {'provider/1.2.3': {sha512: 'fixture-package-hash', path: path.join(root, 'provider')}}, project: {frameworks: {'net11.0': {}}}}))
    write('app/bin/e_sqlcipher.dll', 'abc')
    const options = {apiRoot: root, clone: root, hostBaseline: 'eng/baselines/host-test-baseline.json', coverage: false,
      quality: false, env: {}, commandVersion: command => ({dotnet: '11.0.100', npm: '11.0.0', pnpm: '11.1.3'})[command]}
    const observed = collectInputs(options)
    assert.equal(observed.inputs.dependencies.native[0].sha256, 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
    assert.equal(observed.inputs.dependencies.evaluated[0].targets['net11.0']['provider/1.2.3'].runtime['provider.dll'] instanceof Object, true)
    assert.equal(observed.inputs.toolchain.tools.pnpm, '11.1.3')
    assert.equal(observed.inputs.dependencies.evaluated[0].libraries['provider/1.2.3'].path, '<clone>/provider')
    assert.equal(observed.reuseAuthorized, false)
    assert.ok(observed.inputs.unknownInputs.includes('evaluated compiler inputs are not yet captured by a trusted producer'))
    assert.notEqual(collectInputs({...options, coverage: true}).fingerprint, observed.fingerprint)
    write('app/bin/e_sqlcipher.dll', 'changed binary')
    assert.notEqual(collectInputs(options).fingerprint, observed.fingerprint)
    const persisted = persistInputShadow(options)
    assert.equal(JSON.parse(readFileSync(path.join(root, '.claude/gate-evidence/validation-inputs-shadow.json'))).fingerprint, persisted.fingerprint)
  } finally {rmSync(root, {recursive: true, force: true})}
})
test('collection failure produces an explicit non-authorizing observation', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'validation-inputs-failure-'))
  try {
    const result = persistInputShadow({apiRoot: root, clone: root, coverage: false, quality: false})
    assert.equal(result.collectionFailed, true)
    assert.equal(result.reuseAuthorized, false)
    assert.equal(JSON.parse(readFileSync(path.join(root, '.claude/gate-evidence/validation-inputs-shadow.json'))).collectionFailed, true)
  } finally {rmSync(root, {recursive: true, force: true})}
})

test('diagnostic storage failure cannot throw over the authoritative gate result', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'validation-inputs-storage-'))
  try {
    writeFileSync(path.join(root, '.claude'), 'file blocks diagnostic directory')
    assert.equal(persistInputShadow({apiRoot: root, clone: root}).collectionFailed, true)
  } finally {rmSync(root, {recursive: true, force: true})}
})
