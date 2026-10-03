import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, writeFileSync, rmSync, readFileSync, mkdirSync} from 'node:fs'
import path from 'node:path'
import {tmpdir} from 'node:os'
import {readCompilerObservation} from '../validation-compiler-inputs.mjs'
import {createHash} from 'node:crypto'
import {beforeCompile, afterCompile, compilerRoots} from '../validation-compiler-capture.mjs'
import {establishNuGetRoot} from '../validation-nuget-root.mjs'
const sha = value => createHash('sha256').update(value).digest('hex')
const captureSession = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'

const fixture = callback => {
  const root = mkdtempSync(path.join(tmpdir(), 'compiler-inputs-test-'))
  try {
    writeFileSync(path.join(root, 'project.csproj'), '<Project />')
    writeFileSync(path.join(root, 'source.cs'), 'abc')
    writeFileSync(path.join(root, 'reference.dll'), 'reference bytes')
    writeFileSync(path.join(root, 'validation-compiler.context'),
      `project=${path.join(root, 'project.csproj')}\nframework=net11.0\nconfiguration=Release\nskipCompilerExecution=\ndesignTimeBuild=\n`)
    writeFileSync(path.join(root, 'validation-compiler.args'), '/nologo\n/target:library\n/reference:reference.dll\nsource.cs\n')
    writeFileSync(path.join(root, 'validation-compiler.snapshot'), JSON.stringify({schemaVersion: 1, captureSession, beforeMatches: true,
      argsDigest: sha(readFileSync(path.join(root, 'validation-compiler.args'))),
      contextDigest: sha(readFileSync(path.join(root, 'validation-compiler.context'))),
      files: [{role: 'project', identity: 'source/project.csproj', sha256: sha('<Project />')},
        {role: 'reference', identity: 'source/reference.dll', sha256: sha('reference bytes')},
        {role: 'source', identity: 'source/source.cs', sha256: 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'}]}))
    callback(root, () => readCompilerObservation({argsFile: path.join(root, 'validation-compiler.args'),
      roots: [{name: 'source', directory: root}], expectedCaptureSession: captureSession}))
  } finally {rmSync(root, {recursive: true, force: true})}
}
test('observed Csc arguments include actual source and reference file hashes', () => fixture((root, read) => {
  const result = read()
  assert.equal(result.completeCompilerObservation, true)
  assert.equal(result.trustedProducer, false)
  assert.equal(result.files.find(item => item.role === 'source').sha256,
    'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
  const before = result.files.find(item => item.role === 'reference').sha256
  writeFileSync(path.join(root, 'reference.dll'), 'changed bytes')
  assert.notEqual(read().files.find(item => item.role === 'reference').sha256, before)
  assert.equal(read().completeCompilerObservation, false, 'post-capture changes invalidate execution snapshot')
}))

test('reference aliases and resource names/access remain in command semantics', () => fixture((root, read) => {
  const args = path.join(root, 'validation-compiler.args')
  const observe = argument => {
    writeFileSync(args, `${argument}\nsource.cs\n`)
    return read().arguments
  }
  assert.notDeepEqual(observe('/reference:First=reference.dll'), observe('/reference:Second=reference.dll'))
  writeFileSync(path.join(root, 'payload.txt'), 'payload')
  assert.notDeepEqual(observe('/resource:payload.txt,NameA,public'), observe('/resource:payload.txt,NameB,private'))
}))

test('stale or foreign-session snapshot is not compiler execution proof', () => fixture((root, read) => {
  const snapshot = path.join(root, 'validation-compiler.snapshot')
  const value = JSON.parse(readFileSync(snapshot, 'utf8'))
  writeFileSync(snapshot, JSON.stringify({...value, captureSession: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'}))
  assert.equal(read().completeCompilerObservation, false)
  rmSync(snapshot)
  assert.equal(read().completeCompilerObservation, false)
}))

test('input change between precompile and postcompile snapshots is refused', () => fixture((root, read) => {
  const argsFile = path.join(root, 'validation-compiler.args')
  writeFileSync(path.join(root, 'validation-compiler.inputs'),
    ['project.csproj', 'source.cs', 'reference.dll'].map(file => path.join(root, file)).join('\n'))
  const options = {argsFile, source: root, captureSession, env: {}}
  beforeCompile(options)
  assert.equal(afterCompile(options).beforeMatches, true)
  assert.equal(read().completeCompilerObservation, true)
  beforeCompile(options)
  writeFileSync(path.join(root, 'source.cs'), 'changed during compiler invocation')
  assert.equal(afterCompile(options).beforeMatches, false)
  assert.equal(read().completeCompilerObservation, false)
}))

test('unset NuGet environment and explicit override each resolve once and share the same capture/collector root', () => fixture(root => {
  const env = {}, argsFile = path.join(root, 'validation-compiler.args')
  const defaultRoot = path.join(root, 'default-global-packages'), overrideRoot = path.join(root, 'explicit-packages')
  for (const folder of [defaultRoot, overrideRoot]) {mkdirSync(folder); writeFileSync(path.join(folder, 'provider.dll'), 'abc')}
  for (const [folder, output] of [[defaultRoot, `global-packages: ${defaultRoot}${path.sep}\n`],
    [overrideRoot, `info : global-packages: ${overrideRoot}${path.sep}\n`]]) {
    if (folder === overrideRoot) env.NUGET_PACKAGES = overrideRoot
    const prior = env.NUGET_PACKAGES
    establishNuGetRoot({cwd: root, env, run: (executable, args, options) => {
      assert.equal(executable, 'dotnet')
      assert.deepEqual(args, ['nuget', 'locals', 'global-packages', '--list', '--force-english-output'])
      assert.equal(options.cwd, root); assert.equal(options.env.NUGET_PACKAGES, prior)
      return output
    }})
    assert.equal(env.NUGET_PACKAGES, folder)
    assert.equal(compilerRoots(root, env).find(item => item.name === 'packages').directory, folder)
    const reference = path.join(folder, 'provider.dll')
    writeFileSync(argsFile, `/reference:${reference}\nsource.cs\n`)
    writeFileSync(path.join(root, 'validation-compiler.inputs'), [path.join(root, 'project.csproj'), path.join(root, 'source.cs'), reference].join('\n'))
    const options = {argsFile, source: root, captureSession, env}
    beforeCompile(options); const snapshot = afterCompile(options)
    assert.equal(snapshot.resolvedPackageRoot, folder)
    const observed = readCompilerObservation({argsFile, roots: compilerRoots(root, env), expectedCaptureSession: captureSession})
    assert.equal(observed.completeCompilerObservation, true, observed.problems.join())
    assert.deepEqual(observed.files.find(file => file.identity === 'packages/provider.dll'), {role: 'reference',
      identity: 'packages/provider.dll', sha256: 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'})
    const wrongRoot = folder === defaultRoot ? overrideRoot : defaultRoot
    assert.equal(readCompilerObservation({argsFile, roots: compilerRoots(root, {NUGET_PACKAGES: wrongRoot}),
      expectedCaptureSession: captureSession}).completeCompilerObservation, false, 'snapshot metadata cannot grant a different root')
  }
}))

test('unknown, relative and ambiguous NuGet root output cannot expand approved roots', () => {
  for (const output of ['', 'global-packages: relative/packages\n', 'global-packages: /one\nglobal-packages: /two\n']) {
    const env = {}
    assert.throws(() => establishNuGetRoot({cwd: '.', env, run: () => output}), /unavailable/)
    assert.equal(env.NUGET_PACKAGES, undefined)
  }
})
test('missing, empty, unknown and nested-response compiler inputs fail closed', () => fixture((root, read) => {
  for (const args of ['', '/unknownswitch:x\nsource.cs', '/reference:absent.dll\nsource.cs', '@nested.rsp\nsource.cs']) {
    writeFileSync(path.join(root, 'validation-compiler.args'), args)
    assert.equal(read().completeCompilerObservation, false)
  }
  writeFileSync(path.join(root, 'validation-compiler.args'), '../unapproved-source.cs\n')
  assert.ok(read().problems.includes('compiler file outside approved roots'))
}))
test('design-time or skipped compilation is not execution proof', () => fixture((root, read) => {
  const file = path.join(root, 'validation-compiler.context')
  const original = readFileSync(file, 'utf8')
  for (const value of ['skipCompilerExecution=true', 'designTimeBuild=true']) {
    writeFileSync(file, original + value + '\n')
    assert.equal(read().completeCompilerObservation, false)
  }
}))
