// Prove fresh restore consumed the verified dependency bytes before API build.
import {readFileSync, readdirSync, lstatSync, realpathSync, writeFileSync, mkdirSync, existsSync} from 'node:fs'
import path from 'node:path'
import {inflateRawSync} from 'node:zlib'
import {sha256, packageMetadata, verifyFeed} from './same-job-platform-feed.mjs'
import {readPin} from './build-local-feed.mjs'

const safeRelative = value => typeof value === 'string' && value.length > 0 && !value.includes('\\')
  && !value.includes(':') && !value.startsWith('/') && !value.split('/').some(part => ['', '.', '..'].includes(part))
function approvedFile(root, relative) {
  if (!safeRelative(relative)) throw new Error('unsafe consumed package path')
  const file = path.join(root, relative), physical = path.relative(realpathSync(root), realpathSync(file))
  if (physical === '..' || physical.startsWith(`..${path.sep}`) || path.isAbsolute(physical)
    || !lstatSync(file).isFile() || lstatSync(file).isSymbolicLink()) throw new Error('consumed package path escaped scratch cache')
  return readFileSync(file)
}
function entries(raw) {
  // Same bounded local-header archive forms accepted by the reviewed bundle reader.
  const result = new Map(); let offset = 0, total = 0
  while (offset + 30 <= raw.length && raw.readUInt32LE(offset) === 0x04034b50) {
    const flags = raw.readUInt16LE(offset + 6), method = raw.readUInt16LE(offset + 8)
    const compressed = raw.readUInt32LE(offset + 18), length = raw.readUInt32LE(offset + 22)
    const nameLength = raw.readUInt16LE(offset + 26), extraLength = raw.readUInt16LE(offset + 28)
    const start = offset + 30 + nameLength + extraLength, end = start + compressed
    const name = raw.toString('utf8', offset + 30, offset + 30 + nameLength)
    if (end > raw.length || flags & 9 || ![0, 8].includes(method) || !safeRelative(name)
      || result.has(name.toLowerCase()) || length > 64 * 1024 * 1024 || (total += length) > 256 * 1024 * 1024)
      throw new Error('unsupported consumed package archive')
    const bytes = method === 8 ? inflateRawSync(raw.subarray(start, end), {maxOutputLength: 64 * 1024 * 1024}) : raw.subarray(start, end)
    if (bytes.length !== length) throw new Error('consumed package entry length differs')
    result.set(name.toLowerCase(), {name, bytes}); offset = end
  }
  return result
}
export function verifyConsumedFeed({clone, packages, bundlePath, bundleDigest, pin}) {
  if (!path.isAbsolute(packages) || path.relative(path.dirname(clone), packages) !== 'nuget-packages')
    throw new Error('dependency restore cache must be scratch-owned')
  if (lstatSync(packages).isSymbolicLink() || !lstatSync(packages).isDirectory()
    || path.relative(realpathSync(path.dirname(clone)), realpathSync(packages)) !== 'nuget-packages')
    throw new Error('dependency restore cache escaped scratch')
  const raw = readFileSync(bundlePath)
  if (raw.length > 64 * 1024 * 1024 || typeof bundleDigest !== 'string' || !/^[a-f0-9]{64}$/.test(bundleDigest)
    || sha256(raw) !== bundleDigest) throw new Error('verified handoff digest differs')
  const bundle = JSON.parse(raw)
  if (bundle.identity?.platform?.repository !== pin.repository || bundle.identity?.platform?.commit !== pin.commit)
    throw new Error('verified handoff platform pin differs from current consumer pin')
  if (!Array.isArray(bundle.files)) throw new Error('verified handoff inventory absent')
  const files = bundle.files.map(file => {
    if (!safeRelative(file.name) || typeof file.base64 !== 'string') throw new Error('invalid verified handoff file')
    const bytes = Buffer.from(file.base64, 'base64')
    if (bytes.toString('base64') !== file.base64 || bytes.length !== file.size || sha256(bytes) !== file.sha256
      || !approvedFile(path.join(clone, '.feed'), file.name).equals(bytes)) throw new Error('materialized feed bytes differ')
    return {name: file.name, bytes}
  })
  verifyFeed(files, pin, bundle.identity?.packageGraph?.packedVersion)
  const expected = new Map(files.filter(file => file.name.endsWith('.nupkg')).map(file => {
    const metadata = packageMetadata(file.bytes)
    return [`${metadata.id}/${metadata.version}`.toLowerCase(), {...file, metadata, entries: entries(file.bytes)}]
  }))
  const assets = []
  const visit = directory => {
    for (const entry of readdirSync(directory, {withFileTypes: true})) {
      if (entry.isSymbolicLink()) throw new Error('restore assets cannot be symbolic links')
      if (['.git', '.feed', 'node_modules'].includes(entry.name)) continue
      const file = path.join(directory, entry.name)
      if (entry.isDirectory()) visit(file)
      else if (entry.name === 'project.assets.json') assets.push(JSON.parse(readFileSync(file, 'utf8')))
    }
  }
  visit(clone)
  if (!assets.length) throw new Error('restored project assets absent')
  const consumed = new Map()
  const producerIds = new Set(Object.keys(pin.producers).map(id => id.toLowerCase()))
  for (const asset of assets) {
    const roots = Object.keys(asset.packageFolders ?? {})
    if (roots.length !== 1 || path.relative(packages, roots[0]) !== '') throw new Error('restore assets use a different cache')
    for (const [identity, library] of Object.entries(asset.libraries ?? {})) {
      if (!/^Harborline\./i.test(identity)) continue
      const id = identity.split('/')[0]
      if (library.type === 'project' && !producerIds.has(id.toLowerCase())) {
        // Project references compile fresh API source, rather than consume feed archives.
        const owner = asset.project?.restore?.projectPath
        if (typeof owner !== 'string' || !path.isAbsolute(owner) || !owner.endsWith('.csproj')
          || typeof library.path !== 'string' || library.msbuildProject !== library.path)
          throw new Error('API project reference identity incomplete')
        approvedFile(clone, path.relative(clone, owner).replaceAll('\\', '/'))
        const referenced = path.resolve(path.dirname(owner), library.path)
        const source = approvedFile(clone, path.relative(clone, referenced).replaceAll('\\', '/')).toString('utf8')
          .replace(/<!--[\s\S]*?-->/g, '')
        const packageIds = [...source.matchAll(/<PackageId>([^<]+)<\/PackageId>/g)].map(match => match[1])
        const assemblyIds = [...source.matchAll(/<AssemblyName>([^<]+)<\/AssemblyName>/g)].map(match => match[1])
        const declared = packageIds.length === 1 ? packageIds[0] : packageIds.length === 0 && assemblyIds.length === 1
          ? assemblyIds[0] : packageIds.length === 0 && assemblyIds.length === 0 ? path.basename(referenced, '.csproj') : null
        if (!referenced.endsWith('.csproj') || declared?.toLowerCase() !== id.toLowerCase())
          throw new Error('API project reference differs from source identity')
        continue
      }
      const item = expected.get(identity.toLowerCase())
      if (!item || library.type !== 'package' || library.path !== identity.toLowerCase()) throw new Error('first-party restored identity differs')
      const archiveName = `${item.metadata.id}.${item.metadata.version}.nupkg`.toLowerCase()
      const archive = approvedFile(packages, `${library.path}/${archiveName}`)
      if (!archive.equals(item.bytes)) throw new Error('NuGet consumed a different package archive')
      for (const {name, bytes} of item.entries.values()) {
        // SDK images may use NUGET_XMLDOC_MODE=skip. Unused lib documentation
        // need not be extracted; a target selecting it still requires its bytes below.
        if (/^lib\/[^/]+\/[^/]+\.xml$/i.test(name) && !existsSync(path.join(packages, library.path, name))) continue
        if (/^(lib|ref|analyzers|build|buildmultitargeting|buildtransitive|runtimes|content|contentfiles)\//i.test(name)
          && !approvedFile(packages, `${library.path}/${name}`).equals(bytes))
          throw new Error('extracted dependency bytes differ from verified archive')
      }
      let selected = 0
      for (const target of Object.values(asset.targets ?? {})) {
        const resolved = target[identity]
        if (!resolved) continue
        for (const role of ['compile', 'runtime', 'native', 'resource', 'build', 'buildMultiTargeting', 'contentFiles', 'runtimeTargets'])
          for (const name of Object.keys(resolved[role] ?? {})) {
            if (name.endsWith('/_._')) continue
            const trusted = item.entries.get(name.toLowerCase())
            if (!trusted || !approvedFile(packages, `${library.path}/${name}`).equals(trusted.bytes))
              throw new Error('compiler-selected package bytes differ from verified archive')
            selected++
          }
      }
      if (!selected) throw new Error('first-party package has no observed consumed files')
      consumed.set(identity, {package: identity, archiveSha256: sha256(archive), selectedFiles: selected})
    }
  }
  if (!consumed.size) throw new Error('no first-party dependency consumption observed')
  return {schemaVersion: 1, dependencyBytesVerified: true, apiValidationReused: false, packages: [...consumed.values()]}
}
if (import.meta.main) {
  try {
    const [clone, packages] = process.argv.slice(2)
    const result = verifyConsumedFeed({clone, packages, bundlePath: process.env.HARBORLINE_PLATFORM_FEED_HANDOFF_PATH,
      bundleDigest: process.env.HARBORLINE_PLATFORM_FEED_HANDOFF_SHA256, pin: readPin()})
    mkdirSync(path.join(clone, '.claude/gate-evidence'), {recursive: true})
    writeFileSync(path.join(clone, '.claude/gate-evidence/platform-feed-consumption.json'), JSON.stringify(result, null, 2))
    // The exact-clone recorder persists stdout before deleting scratch.
    console.log(JSON.stringify(result))
    console.log(`platform-feed: verified consumed bytes for ${result.packages.length} first-party packages; fresh API validation`)
  } catch {console.error('platform-feed consumption proof failed'); process.exitCode = 1}
}
