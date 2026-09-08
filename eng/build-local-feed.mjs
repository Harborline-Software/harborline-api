#!/usr/bin/env node
// Adapted from harborline-app/apps/blazor/scripts/build-local-feed.mjs (tickets 105, 143, 314).
// Pack every platform library, then prove identity, version and first-party dependency closure.
import {execFileSync} from 'node:child_process'
import {mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync} from 'node:fs'
import path from 'node:path'
import {pathToFileURL} from 'node:url'
import {inflateRawSync} from 'node:zlib'

const root = path.resolve(import.meta.dirname, '..')
export function readPin(file = path.join(root, 'eng/platform-pin.json')) {
  const pin = JSON.parse(readFileSync(file, 'utf8'))
  if (pin.schemaVersion !== 1 || !/^[a-f0-9]{40}$/.test(pin.commit)) throw new Error('platform pin must name a 40-hex commit')
  return pin
}
export function assertProducers(manifest, pin) {
  const built = new Map()
  for (const {id, assembly} of manifest) {
    const key = id.toLowerCase() // NuGet package identities are case insensitive.
    if (built.has(key)) throw new Error(`duplicate producer for ${id}`)
    built.set(key, assembly)
  }
  const identities = Object.fromEntries(manifest.map(({id, assembly}) => [id, assembly]).sort(([a], [b]) => a.localeCompare(b)))
  if (JSON.stringify(identities) !== JSON.stringify(pin.producers)) throw new Error('packed identities do not match eng/platform-pin.json producers')
  return identities
}
export function assertFeed(file = path.join(root, 'nuget.config')) {
  const sources = readFileSync(file, 'utf8').replace(/<!--[\s\S]*?-->/g, '')
  if (!/<add\s+key="harborline-local"\s+value="\.feed"\s*\/>/.test(sources)) throw new Error('nuget.config must declare the local .feed')
}
async function main() {
  const pin = readPin()
  if (process.argv[2] === '--check-manifest') {
    assertProducers(JSON.parse(readFileSync(process.argv[3], 'utf8')), pin)
    console.log('platform producer manifest OK')
    return
  }
  const dryRun = process.argv.length === 3 && process.argv[2] === '--dry-run'
  if (process.argv.length !== 2 && !dryRun) throw new Error('usage: node eng/build-local-feed.mjs [--check-manifest file | --dry-run]')
  assertFeed()
  const platform = process.env.HARBORLINE_PLATFORM_REPO ?? path.resolve(root, '../harborline-platform')
  const git = (...args) => execFileSync('git', ['-C', platform, ...args], {encoding: 'utf8'}).trim()
  if (git('rev-parse', 'HEAD') !== pin.commit) throw new Error('platform checkout does not match the recorded pin')
  if (git('status', '--porcelain', '--untracked-files=normal')) throw new Error('platform checkout must be clean before packing')
  // Discover the pinned platform's explicit packable inventory; the producer pin catches drift.
  const manifest = git('ls-files', '*.csproj').split('\n').flatMap(project => {
    const source = readFileSync(path.join(platform, project), 'utf8').replace(/<!--[\s\S]*?-->/g, '')
    if (!/<IsPackable>\s*true\s*<\/IsPackable>/.test(source)) return []
    const id = /<PackageId>([^<]+)<\/PackageId>/.exec(source)?.[1] ?? path.basename(project, '.csproj')
    const assembly = /<AssemblyName>([^<]+)<\/AssemblyName>/.exec(source)?.[1] ?? path.basename(project, '.csproj')
    return [{project, id, assembly: `${assembly}.dll`}]
  })
  assertProducers(manifest, pin) // Reject duplicates before dotnet can overwrite one nupkg with another.
  const {computePackageVersion} = await import(pathToFileURL(path.join(platform, 'tooling/package-version.mjs')).href)
  const packedVersion = computePackageVersion(platform)
  const feed = path.join(root, '.feed')
  // Stop MSBuild's upward targets search at the platform boundary. The API's targets add MinVer;
  // the pinned platform has no targets file. An explicit path also honors one if a future pin adds it.
  const commands = manifest.map(({project}) => ['pack', path.join(platform, project), '-c', 'Release', '--output', feed,
    `-p:DirectoryBuildTargetsPath=${path.resolve(platform, 'Directory.Build.targets')}`,
    `-p:HarborlinePackedVersion=${packedVersion}`, '-nodeReuse:false', '-maxcpucount:6'])
  if (dryRun) {
    console.log(JSON.stringify({packedVersion, producers: assertProducers(manifest, pin), commands}, null, 2))
    return
  }
  rmSync(feed, {recursive: true, force: true})
  mkdirSync(feed, {recursive: true})
  for (const args of commands) execFileSync('dotnet', args, {cwd: platform, stdio: 'inherit'})
  const packed = readdirSync(feed).filter(name => name.endsWith('.nupkg'))
  const packages = packed.map(name => readNuspec(path.join(feed, name)))
  const producers = assertProducers(packages, pin)
  const ids = new Set(packages.map(({id}) => id.toLowerCase()))
  for (const {id, dependencies} of packages) {
    for (const dependency of dependencies) {
      if (/^Harborline\./i.test(dependency) && !ids.has(dependency.toLowerCase())) throw new Error(`missing first-party dependency: ${id} -> ${dependency}`)
    }
  }
  if (packed.some(name => !name.endsWith(`.${packedVersion}.nupkg`))) throw new Error(`feed was not packed at ${packedVersion}`)
  writeFileSync(path.join(feed, 'packed-version.props'), [
    '<!-- Generated by eng/build-local-feed.mjs; .feed is gitignored. -->', '<Project>', '  <PropertyGroup>',
    `    <HarborlinePackedVersion>${packedVersion}</HarborlinePackedVersion>`, '  </PropertyGroup>', '</Project>', '',
  ].join('\n'))
  console.log(JSON.stringify({feed, packed, packedVersion, projects: manifest.map(({project}) => project), producers}, null, 2))
}
// Same dependency-free local-header walk as the app: read the nuspec and lib assembly identity.
function readNuspec(archivePath) {
  const raw = readFileSync(archivePath)
  let nuspec = null, assembly = ''
  for (let offset = 0; offset + 30 <= raw.length;) {
    if (raw.readUInt32LE(offset) !== 0x04034b50) break
    const method = raw.readUInt16LE(offset + 8), compressed = raw.readUInt32LE(offset + 18)
    const nameLength = raw.readUInt16LE(offset + 26), extraLength = raw.readUInt16LE(offset + 28)
    const name = raw.toString('utf8', offset + 30, offset + 30 + nameLength)
    const dataStart = offset + 30 + nameLength + extraLength
    if (!assembly && /^lib\/[^/]+\/[^/]+\.dll$/.test(name)) assembly = path.posix.basename(name)
    if (name.endsWith('.nuspec') && !name.includes('/')) {
      const body = raw.subarray(dataStart, dataStart + compressed)
      if (method === 0) nuspec = body.toString('utf8')
      else if (method === 8) nuspec = inflateRawSync(body).toString('utf8')
      else throw new Error(`${archivePath}: unsupported compression method ${method}`)
    }
    if (compressed === 0 && method === 8) throw new Error(`${archivePath}: streamed zip entry; repack required`)
    offset = dataStart + compressed
  }
  if (nuspec === null) throw new Error(`${archivePath}: no nuspec found`)
  return {id: /<id>([^<]+)<\/id>/.exec(nuspec)?.[1] ?? path.basename(archivePath), assembly,
    dependencies: [...nuspec.matchAll(/<dependency\s+id="([^"]+)"/g)].map(match => match[1])}
}
if (import.meta.main) await main()
