import {test} from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, writeFileSync, rmSync, readFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import {join} from 'node:path'
import {execFileSync} from 'node:child_process'
import {selectPlatformPin} from './select-platform-pin.mjs'
test('full mutation rejects unmerged code before checkout; hosted PR retains proposed pins', () => {
  const directory = mkdtempSync(join(tmpdir(), 'platform-pin-trust-'))
  const git = (...args) => execFileSync('git', ['-C', directory, ...args], {encoding:'utf8'}).trim()
  try {
    git('init', '-q', '-b', 'main')
    writeFileSync(join(directory, 'package-version.mjs'), 'throw new Error("MUST NOT EXECUTE")')
    git('add', '.')
    git('-c','user.name=Trust Test','-c','user.email=trust-test@example.invalid','-c','core.hooksPath=','commit','-qm','approved')
    const approved = git('rev-parse','HEAD')
    git('update-ref','refs/remotes/origin/main',approved)
    git('checkout','-qb','unmerged')
    writeFileSync(join(directory,'untrusted.txt'),'unmerged change')
    git('add','.')
    git('-c','user.name=Trust Test','-c','user.email=trust-test@example.invalid','-c','core.hooksPath=','commit','-qm','unmerged')
    const unmerged = git('rev-parse','HEAD')
    git('checkout','-q','main')
    const pin = {schemaVersion:1,repository:'Harborline-Software/harborline-platform',commit:approved}
    assert.equal(selectPlatformPin(pin,directory),approved)
    assert.throws(() => selectPlatformPin({...pin,commit:unmerged},directory),/outside fetched main history/)
    assert.equal(git('rev-parse','HEAD'),approved)
    for(const commit of ['main','refs/pull/1/head','a'.repeat(39),'a'.repeat(40)+'\n']) assert.throws(() => selectPlatformPin({...pin,commit},directory),/immutable/)
    assert.throws(() => selectPlatformPin({...pin,repository:'attacker/fork'},directory),/fixed repository/)
    assert.equal(selectPlatformPin({...pin,commit:unmerged},directory,false),unmerged)
  } finally { rmSync(directory,{recursive:true,force:true}) }
})
test('fixed main checkout precedes validation and code execution', () => {
  const source=readFileSync(new URL('../workflows/mutation.yml',import.meta.url),'utf8')
  const checkout=source.indexOf('      - name: Check out Platform main without executing it')
  const validate=source.indexOf('      - name: Validate and select the recorded Platform pin')
  const execute=source.indexOf('      - name: '+'Reserved full mutation and isolated feed')
  assert.ok(checkout>=0 && checkout<validate && validate<execute)
  assert.match(source.slice(checkout,validate),/ref: main/)
  assert.match(source.slice(checkout,validate),/fetch-depth: 0/)
  assert.match(source.slice(checkout,validate),/persist-credentials: false/)
})
