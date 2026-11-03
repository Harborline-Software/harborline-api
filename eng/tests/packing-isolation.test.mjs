// T-1139 (owner ruling 2026-11-03, route B): the jobs that pack and publish the packages run no code or config the
// checkout supplies implicitly. Each prevention flag below has a case that fails when it is removed, and the two
// attacks a flag stops are also run for real: a root unittest.py shadowing the standard library (CWE-427), and a
// repository .npmrc redirecting the npm registry. The literals are the oracle, not anything read from production code.
import assert from 'node:assert/strict'
import {spawnSync} from 'node:child_process'
import {existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import test from 'node:test'

const root = path.resolve(import.meta.dirname, '../..')
const read = file => readFileSync(path.join(root, file), 'utf8').replaceAll('\r\n', '\n')
const workflow = read('.github/workflows/packages.yml')
const job = name => {
  const rest = workflow.split(`\n  ${name}:\n`)[1]
  assert.ok(rest, `packages.yml has a ${name} job`)
  return rest.split(/\n  [a-z][\w-]*:\n/)[0]
}
// Comment lines dropped and shell line continuations joined, so each command reads as one line.
const code = text => text.split('\n').filter(line => !/^\s*#/.test(line)).join('\n').replace(/\\\n\s*/g, ' ')
const packing = {
  'pack-consume': code(job('pack-consume')),
  publish: code(job('publish')),
  'eng/verify-packages.sh': code(read('eng/verify-packages.sh')),
}
const scratch = t => {
  const dir = mkdtempSync(path.join(tmpdir(), 'packing-isolation-'))
  t.after(() => rmSync(dir, {recursive: true, force: true}))
  return dir
}

test('every Python step in the packing and publishing jobs runs in isolated mode', () => {
  let seen = 0
  for (const [where, text] of Object.entries(packing)) {
    for (const call of text.match(/\bpython3\b[^\n]*/g) ?? []) {
      seen++
      assert.match(call, /^python3 -I /, `${where}: ${call}`)
    }
  }
  assert.ok(seen >= 4, `expected the unittest, consume, seal and verify steps, saw ${seen}`)
})

test('a root unittest.py runs in place of the standard library unless Python is isolated (finding 1)', t => {
  if (spawnSync('python3', ['--version']).status !== 0) return t.skip('python3 is not available here; CI runs this')
  const dir = scratch(t)
  writeFileSync(path.join(dir, 'unittest.py'), "open('SHADOWED', 'w').write('ran')\n")
  const shadowed = flags => {
    rmSync(path.join(dir, 'SHADOWED'), {force: true})
    spawnSync('python3', [...flags, '-m', 'unittest', 'discover', '-s', '.', '-p', 'none_*.py'], {cwd: dir})
    return existsSync(path.join(dir, 'SHADOWED'))
  }
  assert.equal(shadowed([]), true, 'positive control: without -I the root unittest.py runs')
  assert.equal(shadowed(['-I']), false, 'with -I the standard library unittest runs')
})

test('pnpm never loads a pnpmfile while packing', () => {
  const installs = packing['pack-consume'].match(/pnpm install[^\n]*/g) ?? []
  assert.ok(installs.length > 0)
  for (const install of installs) assert.match(install, /--ignore-pnpmfile\b/, install)
})

test('dotnet pack restores only from the root nuget.config', () => {
  const packs = packing['eng/verify-packages.sh'].match(/dotnet pack[^\n]*/g) ?? []
  assert.ok(packs.length > 0)
  for (const pack of packs) assert.match(pack, /-p:RestoreConfigFile="\$repo_root\/nuget\.config"/, pack)
})

test('publish pushes NuGet packages outside the checkout, to an explicit source, writing no config', () => {
  const publish = packing.publish
  assert.doesNotMatch(publish, /dotnet nuget add source/)
  assert.match(publish, /cd "\$\(mktemp -d\)"\n\s*dotnet nuget push "\$packages\/\*\.nupkg"\s+--source "https:\/\/nuget\.pkg\.github\.com\//)
})

test('publish runs npm with only its own user config, outside the checkout, and no package scripts', () => {
  const publish = packing.publish
  assert.match(publish, /export NPM_CONFIG_USERCONFIG="\$RUNNER_TEMP\/publish\.npmrc"/)
  assert.match(publish, /cd "\$\(mktemp -d\)"\n\s*for tgz in "\$packages"\/\*\.tgz; do\n\s*npm publish "\$tgz" --access restricted --ignore-scripts --registry https:\/\/npm\.pkg\.github\.com\n/)
})

test('a repository .npmrc steers npm, and an empty directory with its own user config does not', t => {
  const windows = process.platform === 'win32'
  const registry = (cwd, userconfig) => spawnSync(windows ? 'npm.cmd' : 'npm', ['config', 'get', '@harborline-software:registry'],
    {cwd, encoding: 'utf8', shell: windows, env: {...process.env, NPM_CONFIG_USERCONFIG: userconfig}}).stdout.trim()
  const dir = scratch(t)
  const repo = path.join(dir, 'repo')
  const outside = path.join(dir, 'outside')
  mkdirSync(repo)
  mkdirSync(outside)
  writeFileSync(path.join(repo, 'package.json'), '{"name":"x","version":"1.0.0"}\n')
  writeFileSync(path.join(repo, '.npmrc'), '@harborline-software:registry=https://registry.attacker.example/\n')
  const userconfig = path.join(dir, 'publish.npmrc')
  writeFileSync(userconfig, '@harborline-software:registry=https://npm.pkg.github.com\n')
  assert.equal(registry(repo, userconfig), 'https://registry.attacker.example/', 'positive control: inside the checkout the project .npmrc wins')
  assert.equal(registry(outside, userconfig), 'https://npm.pkg.github.com', "outside the checkout only the step's own config applies")
})
