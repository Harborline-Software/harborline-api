import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, mkdirSync, writeFileSync, copyFileSync, rmSync} from 'node:fs'
import {execFileSync, spawnSync} from 'node:child_process'
import {tmpdir} from 'node:os'
import path from 'node:path'

test('actual standalone builder strips broker credentials before importing platform-controlled JavaScript', t => {
  const directory = mkdtempSync(path.join(tmpdir(), 'feed-credentials-'))
  t.after(() => rmSync(directory, {recursive: true, force: true}))
  const api = path.join(directory, 'api'), platform = path.join(directory, 'platform')
  for (const folder of [path.join(api, 'eng'), path.join(platform, 'tooling')]) mkdirSync(folder, {recursive: true})
  for (const name of ['build-local-feed.mjs', 'platform-feed-environment.mjs'])
    copyFileSync(path.join(import.meta.dirname, '..', name), path.join(api, 'eng', name))
  writeFileSync(path.join(api, 'nuget.config'), '<configuration><add key="harborline-local" value=".feed" /></configuration>')
  writeFileSync(path.join(platform, 'A.csproj'), '<Project><IsPackable>true</IsPackable><PackageId>Harborline.A</PackageId><AssemblyName>Harborline.A</AssemblyName></Project>')
  const keys = ['GH_TOKEN', 'GITHUB_TOKEN', 'GH_ENTERPRISE_TOKEN', 'GITHUB_ENTERPRISE_TOKEN', 'ACTIONS_RUNTIME_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN']
  writeFileSync(path.join(platform, 'tooling/package-version.mjs'),
    `for (const key of ${JSON.stringify(keys)}) if (process.env[key]) throw Error('broker credential reached platform code');
     export function computePackageVersion() { return '0.0.0-fixture.credential-boundary' }`)
  const git = (...args) => execFileSync('git', ['-c', `safe.directory=${platform}`, '-C', platform, ...args], {encoding: 'utf8'}).trim()
  git('init', '-q'); git('add', '.'); git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '--no-verify', '-qm', 'credential fixture')
  writeFileSync(path.join(api, 'eng/platform-pin.json'), JSON.stringify({schemaVersion: 1, commit: git('rev-parse', 'HEAD'), producers: {'Harborline.A': 'Harborline.A.dll'}}))
  const result = spawnSync(process.execPath, [path.join(api, 'eng/build-local-feed.mjs'), '--dry-run'], {encoding: 'utf8',
    env: {...process.env, HARBORLINE_PLATFORM_REPO: platform, ...Object.fromEntries(keys.map(key => [key, 'synthetic-private-broker-value']))}})
  assert.equal(result.status, 0, result.stderr)
  assert.equal(JSON.parse(result.stdout).packedVersion, '0.0.0-fixture.credential-boundary')
  assert.doesNotMatch(result.stdout + result.stderr, /synthetic-private-broker-value/)
})
