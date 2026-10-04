import test from 'node:test'
import assert from 'node:assert/strict'
import {execFileSync} from 'node:child_process'
import {mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {collectInputs, persistInputShadow} from '../validation-inputs.mjs'

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
      libraries: {'provider/1.2.3': {sha512: 'fixture-package-hash'}}, project: {frameworks: {'net11.0': {}}}}))
    write('app/bin/e_sqlcipher.dll', 'abc')
    const options = {apiRoot: root, clone: root, hostBaseline: 'eng/baselines/host-test-baseline.json', coverage: false,
      quality: false, env: {}, commandVersion: command => ({dotnet: '11.0.100', npm: '11.0.0', pnpm: '11.1.3'})[command]}
    const observed = collectInputs(options)
    assert.equal(observed.inputs.dependencies.native[0].sha256, 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
    assert.equal(observed.inputs.dependencies.evaluated[0].targets['net11.0']['provider/1.2.3'].runtime['provider.dll'] instanceof Object, true)
    assert.equal(observed.inputs.toolchain.tools.pnpm, '11.1.3')
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
