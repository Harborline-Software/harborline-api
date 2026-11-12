import {test} from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, mkdirSync, writeFileSync, copyFileSync, rmSync, chmodSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {fileURLToPath} from 'node:url'
import {execFileSync, spawnSync} from 'node:child_process'

// External NuGet global-packages layout: lowercase ID directory and nuspec.
// Literal fixture does not derive either filename from the ledger implementation.
function runLedger(metadata, {resolved = true, restoreStatus = 0, missingAssets = false, extraVersion = false} = {}) {
  const temp = mkdtempSync(path.join(tmpdir(), 'dependency-ledger-'))
  try {
    const repo = path.join(temp, 'api'), control = path.join(temp, 'control'), cache = path.join(temp, 'cache')
    mkdirSync(path.join(repo, 'eng'), {recursive: true})
    mkdirSync(path.join(repo, 'obj'), {recursive: true})
    mkdirSync(path.join(temp, 'bin'), {recursive: true})
    mkdirSync(path.join(control, 'research/R-0001-fixture'), {recursive: true})
    mkdirSync(path.join(cache, 'example.package/1.2.3'), {recursive: true})
    copyFileSync(fileURLToPath(new URL('../dependency-ledger.mjs', import.meta.url)), path.join(repo, 'eng/dependency-ledger.mjs'))
    writeFileSync(path.join(repo, 'Directory.Packages.props'), '<Project><ItemGroup><PackageVersion Include="Example.Package" Version="1.2.3" Justification="R-0001" /></ItemGroup></Project>')
    writeFileSync(path.join(repo, 'Harborline.Api.slnx'), '<Solution><Project Path="Example.csproj" /></Solution>')
    writeFileSync(path.join(repo, 'Example.csproj'), '<Project />')
    if (!missingAssets) writeFileSync(path.join(repo, 'obj/project.assets.json'), JSON.stringify({packageFolders: {[`${cache}${path.sep}`]: {}}, libraries: resolved ? {'Example.Package/1.2.3': {type: 'package'}, ...(extraVersion ? {'Example.Package/2.0.0': {type: 'package'}} : {})} : {}}))
    if (extraVersion) {
      mkdirSync(path.join(cache, 'example.package/2.0.0'), {recursive: true})
      writeFileSync(path.join(cache, 'example.package/2.0.0/example.package.nuspec'), '<package><metadata><license type="expression">GPL-3.0-only</license></metadata></package>')
    }
    const dotnet = path.join(temp, 'bin', process.platform === 'win32' ? 'dotnet.cmd' : 'dotnet')
    writeFileSync(dotnet, process.platform === 'win32' ? `@exit /b ${restoreStatus}\r\n` : `#!/bin/sh\nexit ${restoreStatus}\n`)
    chmodSync(dotnet, 0o755)
    writeFileSync(path.join(control, 'research/R-0001-fixture/note.md'), 'reach: test\n')
    if (metadata !== undefined) writeFileSync(path.join(cache, 'example.package/1.2.3/example.package.nuspec'), metadata)
    execFileSync('git', ['init', '-q', repo])
    execFileSync('git', ['-C', repo, 'add', '.'])
    return spawnSync(process.execPath, ['eng/dependency-ledger.mjs'], {cwd: repo, encoding: 'utf8', env: {...process.env, PATH: `${path.join(temp, 'bin')}${path.delimiter}${process.env.PATH}`, NUGET_PACKAGES: cache, HARBORLINE_CONTROL_REPO: control}})
  } finally { rmSync(temp, {recursive: true, force: true}) }
}

test('mixed-case package ID reads lowercase NuGet metadata', () => {
  const result = runLedger('<package><metadata><license type="expression">MIT</license></metadata></package>')
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.doesNotMatch(result.stdout, /FAIL/)
})
test('missing metadata fails closed', () => {
  const result = runLedger(undefined)
  assert.equal(result.status, 1, result.stdout + result.stderr)
  assert.match(result.stdout, /FAIL nuspec missing for Example\.Package 1\.2\.3/)
})
test('unapproved licence fails closed', () => {
  const result = runLedger('<package><metadata><license type="expression">GPL-3.0-only</license></metadata></package>')
  assert.equal(result.status, 1, result.stdout + result.stderr)
  assert.match(result.stdout, /FAIL Example\.Package licence GPL-3\.0-only is not allowed/)
})
test('metadata without a licence fails closed', () => {
  const result = runLedger('<package><metadata /></package>')
  assert.equal(result.status, 1, result.stdout + result.stderr)
  assert.match(result.stdout, /FAIL Example\.Package licence unknown is not allowed/)
})
test('unresolved pins are reported without checking cached metadata', () => {
  const result = runLedger(undefined, {resolved: false})
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.match(result.stdout, /ok PackageVersion Example\.Package is pinned but not in the restore graph/)
})
test('restore failures retain their exit code', () => {
  const result = runLedger(undefined, {restoreStatus: 23})
  assert.equal(result.status, 23, result.stdout + result.stderr)
  assert.match(result.stdout, /FAIL.*restore.*23/i)
})
test('missing restore assets fail closed', () => {
  const result = runLedger(undefined, {missingAssets: true})
  assert.equal(result.status, 1, result.stdout + result.stderr)
  assert.match(result.stdout, /FAIL restore assets missing for Example\.csproj/)
})
test('all resolved versions are checked, including overrides', () => {
  const result = runLedger('<package><metadata><license type="expression">MIT</license></metadata></package>', {extraVersion: true})
  assert.equal(result.status, 1, result.stdout + result.stderr)
  assert.match(result.stdout, /FAIL Example\.Package licence GPL-3\.0-only is not allowed/)
})
