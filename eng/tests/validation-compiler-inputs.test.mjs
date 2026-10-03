import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, writeFileSync, rmSync, readFileSync} from 'node:fs'
import path from 'node:path'
import {tmpdir} from 'node:os'
import {readCompilerObservation} from '../validation-compiler-inputs.mjs'

const fixture = callback => {
  const root = mkdtempSync(path.join(tmpdir(), 'compiler-inputs-test-'))
  try {
    writeFileSync(path.join(root, 'project.csproj'), '<Project />')
    writeFileSync(path.join(root, 'source.cs'), 'abc')
    writeFileSync(path.join(root, 'reference.dll'), 'reference bytes')
    writeFileSync(path.join(root, 'validation-compiler.context'),
      `project=${path.join(root, 'project.csproj')}\nframework=net11.0\nconfiguration=Release\nskipCompilerExecution=\ndesignTimeBuild=\n`)
    writeFileSync(path.join(root, 'validation-compiler.args'), '/nologo\n/target:library\n/reference:reference.dll\nsource.cs\n')
    callback(root, () => readCompilerObservation({argsFile: path.join(root, 'validation-compiler.args'), roots: [{name: 'source', directory: root}]}))
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
}))
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
