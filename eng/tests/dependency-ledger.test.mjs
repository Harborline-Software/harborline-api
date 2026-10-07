import {test} from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, mkdirSync, writeFileSync, copyFileSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {fileURLToPath} from 'node:url'
import {execFileSync, spawnSync} from 'node:child_process'

// External NuGet global-packages layout: lowercase ID directory and nuspec.
// Literal fixture does not derive either filename from the ledger implementation.
function runLedger(metadata) {
  const temp = mkdtempSync(path.join(tmpdir(), 'dependency-ledger-'))
  try {
    const repo = path.join(temp, 'api'), control = path.join(temp, 'control'), cache = path.join(temp, 'cache')
    mkdirSync(path.join(repo, 'eng'), {recursive: true})
    mkdirSync(path.join(control, 'research/R-0001-fixture'), {recursive: true})
    mkdirSync(path.join(cache, 'example.package/1.2.3'), {recursive: true})
    copyFileSync(fileURLToPath(new URL('../dependency-ledger.mjs', import.meta.url)), path.join(repo, 'eng/dependency-ledger.mjs'))
    writeFileSync(path.join(repo, 'Directory.Packages.props'), '<Project><ItemGroup><PackageVersion Include="Example.Package" Version="1.2.3" Justification="R-0001" /></ItemGroup></Project>')
    writeFileSync(path.join(control, 'research/R-0001-fixture/note.md'), 'reach: test\n')
    if (metadata !== undefined) writeFileSync(path.join(cache, 'example.package/1.2.3/example.package.nuspec'), metadata)
    execFileSync('git', ['init', '-q', repo])
    execFileSync('git', ['-C', repo, 'add', '.'])
    return spawnSync(process.execPath, ['eng/dependency-ledger.mjs'], {cwd: repo, encoding: 'utf8', env: {...process.env, NUGET_PACKAGES: cache, HARBORLINE_CONTROL_REPO: control}})
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
