import test from 'node:test'
import assert from 'node:assert/strict'
import {cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {execFileSync, spawnSync} from 'node:child_process'
import {assertFeed, readPin} from '../build-local-feed.mjs'

const root = path.resolve(import.meta.dirname, '../..')
test('builder accepts the full manifest and refuses a second producer before packing', () => {
  const directory = mkdtempSync(path.join(tmpdir(), 'platform-producers-'))
  const file = path.join(directory, 'manifest.json')
  const manifest = Object.entries(readPin().producers).map(([id, assembly]) => ({id, assembly}))
  const run = () => spawnSync(process.execPath, [path.join(root, 'eng/build-local-feed.mjs'), '--check-manifest', file], {encoding: 'utf8'})
  try {
    writeFileSync(file, JSON.stringify(manifest))
    assert.equal(run().status, 0)
    for (const second of [manifest[0], {...manifest[0], assembly: 'SecondProducer.dll'},
      {...manifest[0], id: manifest[0].id.toLowerCase()}]) {
      writeFileSync(file, JSON.stringify([...manifest, second]))
      const red = run()
      assert.equal(red.status, 1)
      assert.match(red.stderr, /duplicate producer for/i)
    }
    writeFileSync(file, JSON.stringify(manifest))
    assert.equal(run().status, 0)
  } finally {
    rmSync(directory, {recursive: true, force: true})
  }
})
test('the checked-in platform pin parses and names a 40-hex commit and 24 producers', () => {
  const pin = readPin()
  assert.match(pin.commit, /^[a-f0-9]{40}$/)
  assert.equal(pin.repository, 'Harborline-Software/harborline-platform')
  assert.equal(Object.keys(pin.producers).length, 24)
})
test('nuget.config declares the built local feed beside nuget.org', () => {
  assertFeed()
  const config = readFileSync(path.join(root, 'nuget.config'), 'utf8')
  assert.match(config, /key="nuget.org" value="https:\/\/api.nuget.org\/v3\/index.json"/)
})

test('nested and sibling layouts plan the same 24 packages and version without enclosing build targets', () => {
  // Dry-run the real builder, then evaluate its pack properties with real MSBuild (no restore).
  const directory = mkdtempSync(path.join(tmpdir(), 'platform-layout-'))
  const api = path.join(directory, 'api')
  const sibling = path.join(directory, 'platform')
  const nested = path.join(api, '.platform')
  const write = (base, file, content) => {
    mkdirSync(path.dirname(path.join(base, file)), {recursive: true})
    writeFileSync(path.join(base, file), content)
  }
  const git = (...args) => execFileSync('git', ['-C', sibling, ...args], {encoding: 'utf8'}).trim()
  try {
    const pin = readPin()
    write(sibling, 'Directory.Build.props', '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n')
    write(sibling, 'Directory.Packages.props', '<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>\n')
    // A controlled version provider verifies that the builder uses the platform's answer in both layouts.
    write(sibling, 'tooling/package-version.mjs', "export function computePackageVersion() { return '0.0.0-test.layout' }\n")
    for (const [id, assembly] of Object.entries(pin.producers)) {
      write(sibling, `projects/${id}/${id}.csproj`, `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsPackable>true</IsPackable><PackageId>${id}</PackageId><AssemblyName>${assembly.slice(0, -4)}</AssemblyName></PropertyGroup></Project>\n`)
    }
    git('init', '-q')
    git('add', '.')
    git('-c', 'user.name=Feed test', '-c', 'user.email=feed-test@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '--no-verify', '-qm', 'layout fixture')
    write(api, 'eng/platform-pin.json', JSON.stringify({...pin, commit: git('rev-parse', 'HEAD')}))
    for (const file of ['eng/build-local-feed.mjs', 'nuget.config', 'Directory.Build.targets', 'Directory.Packages.props']) {
      write(api, file, readFileSync(path.join(root, file)))
    }
    cpSync(sibling, nested, {recursive: true})
    const plans = [sibling, nested].map(platform => {
      const result = spawnSync(process.execPath, [path.join(api, 'eng/build-local-feed.mjs'), '--dry-run'], {
        encoding: 'utf8', env: {...process.env, HARBORLINE_PLATFORM_REPO: platform},
      })
      assert.equal(result.status, 0, result.stderr)
      return JSON.parse(result.stdout)
    })
    assert.deepEqual(plans[0].producers, pin.producers)
    assert.deepEqual(plans[1].producers, plans[0].producers)
    assert.equal(plans[0].packedVersion, '0.0.0-test.layout')
    assert.equal(plans[1].packedVersion, plans[0].packedVersion)
    for (const [index, platform] of [sibling, nested].entries()) {
      const plan = plans[index]
      assert.equal(plan.commands.length, 24)
      assert.deepEqual(plan.commands.map(args => path.basename(args[1], '.csproj')).sort(), Object.keys(pin.producers).sort())
      for (const args of plan.commands) assert.ok(args.includes(`-p:HarborlinePackedVersion=${plan.packedVersion}`))
      const [, project, ...args] = plan.commands[0]
      const evaluated = JSON.parse(execFileSync('dotnet', ['msbuild', project,
        ...args.filter(arg => arg.startsWith('-p:')), '-nodeReuse:false', '-maxcpucount:6',
        '-getProperty:ManagePackageVersionsCentrally', '-getProperty:DirectoryPackagesPropsPath', '-getItem:PackageReference'],
      {cwd: platform, encoding: 'utf8'}))
      assert.equal(evaluated.Properties.ManagePackageVersionsCentrally, 'true')
      assert.equal(path.resolve(evaluated.Properties.DirectoryPackagesPropsPath), path.join(platform, 'Directory.Packages.props'))
      assert.deepEqual(evaluated.Items.PackageReference, [], `${index === 0 ? 'sibling' : 'nested'} layout inherited enclosing PackageReferences`)
    }
  } finally {
    assert.ok(directory.startsWith(path.join(tmpdir(), 'platform-layout-')))
    rmSync(directory, {recursive: true, force: true})
  }
})
