// Capture observed inputs before exact-clone scratch is deleted. Unknowns remain explicit blockers.
import {execFileSync} from 'node:child_process'
import {readFileSync, readdirSync, existsSync, mkdirSync, writeFileSync} from 'node:fs'
import path from 'node:path'
import {platform, arch, release} from 'node:os'
import {digest, fingerprint} from './validation-reuse.mjs'
import {resolveCommand} from './lib/resolve-command.mjs'
import {readCompilerObservation} from './validation-compiler-inputs.mjs'
import {compilerRoots} from './validation-compiler-capture.mjs'

const git = (root, ...args) => execFileSync('git', ['-C', root, ...args], {encoding: 'utf8', stdio: 'pipe'}).trim()
const filesUnder = root => {
  const files = []
  const visit = directory => {
    for (const entry of readdirSync(directory, {withFileTypes: true})) {
      if (entry.isSymbolicLink() || ['.git', 'node_modules', '.feed'].includes(entry.name)) continue
      const absolute = path.join(directory, entry.name)
      if (entry.isDirectory()) visit(absolute)
      else if (entry.isFile()) files.push(absolute)
    }
  }
  if (existsSync(root)) visit(root)
  return files.sort()
}
export function collectInputs({apiRoot, clone, hostBaseline, coverage, quality, env = process.env,
  commandVersion = command => {
    const resolved = resolveCommand(command, ['--version'])
    return execFileSync(resolved.executable, resolved.args, {encoding: 'utf8', timeout: 10000}).trim()
  }}) {
  const unknownInputs = ['evaluated compiler inputs are not yet captured by a trusted producer',
    'runner image is not an immutable toolchain image digest']
  const normalized = text => text.replaceAll('\\', '/').replaceAll(clone.replaceAll('\\', '/'), '<clone>')
    .replaceAll(apiRoot.replaceAll('\\', '/'), '<api>')
  // Normalize decoded values, not serialized JSON (Windows JSON escape pairs otherwise
  // become doubled separators and can leave checkout paths inside the fingerprint).
  const normalizeValue = value => {
    if (typeof value === 'string') return normalized(value)
    if (Array.isArray(value)) return value.map(normalizeValue)
    if (value && typeof value === 'object') {
      const result = {}
      for (const [key, item] of Object.entries(value)) {
        const name = normalized(key)
        if (Object.hasOwn(result, name)) throw new Error('normalization identity collision')
        Object.defineProperty(result, name, {value: normalizeValue(item), enumerable: true})
      }
      return result
    }
    return value
  }
  const external = (name, variable, pinPath) => {
    if (!env[variable]) return {applicable: false}
    try {
      const commit = git(env[variable], 'rev-parse', 'HEAD')
      const dirty = git(env[variable], 'status', '--porcelain', '--untracked-files=no')
      const declared = pinPath ? JSON.parse(readFileSync(path.join(apiRoot, pinPath), 'utf8')).commit : null
      if (dirty || (declared && declared !== commit)) unknownInputs.push(`${name} checkout is dirty or differs from its pin`)
      return {applicable: true, commit, tree: git(env[variable], 'rev-parse', 'HEAD^{tree}'), declared}
    } catch { unknownInputs.push(`${name} checkout could not be resolved`); return {applicable: true, unresolved: true} }
  }
  const allFiles = filesUnder(clone)
  if (git(apiRoot, 'status', '--porcelain', '--untracked-files=no')) unknownInputs.push('source checkout is dirty')
  if (git(apiRoot, 'rev-parse', 'HEAD^{tree}') !== git(clone, 'rev-parse', 'HEAD^{tree}'))
    unknownInputs.push('scratch tree differs from candidate tree')
  const assetFiles = allFiles.filter(file => file.endsWith(`${path.sep}project.assets.json`))
  if (!assetFiles.length) unknownInputs.push('no evaluated NuGet dependency assets')
  const evaluated = assetFiles.map(file => {
    const assets = JSON.parse(readFileSync(file, 'utf8'))
    return {project: path.relative(clone, file).replaceAll('\\', '/'),
      targets: normalizeValue(assets.targets),
      libraries: normalizeValue(assets.libraries),
      frameworks: normalizeValue(assets.project?.frameworks ?? {})}
  })
  const native = allFiles.filter(file => /(?:e_sqlcipher|e_sqlite3|sqlite3|y_crdt)\.(?:dll|so|dylib)$/i.test(file))
    .map(file => ({file: path.relative(clone, file).replaceAll('\\', '/'), sha256: digest(readFileSync(file))}))
  if (!native.length) unknownInputs.push('native provider binaries not observed')
  const compilerInputs = []
  const roots = compilerRoots(clone, env)
  for (const argsFile of allFiles.filter(file => file.endsWith(`${path.sep}validation-compiler.args`))) {
    try { compilerInputs.push(readCompilerObservation({argsFile, roots, expectedCaptureSession: env.HARBORLINE_VALIDATION_CAPTURE_SESSION})) }
    catch { unknownInputs.push('compiler observation could not be read') }
  }
  if (!compilerInputs.length) unknownInputs.push('no executed compiler command-line observations')
  for (const observation of compilerInputs)
    unknownInputs.push(...observation.problems.map(problem => `compiler ${observation.project}: ${problem}`))
  // Actual command lines are now observed, but arbitrary build tasks and candidate-produced
  // observations are not a trusted hermetic closure. Do not remove the existing blocker yet.
  const tools = {}
  for (const tool of ['dotnet', 'npm', 'pnpm']) {
    try { tools[tool] = commandVersion(tool) }
    catch { tools[tool] = null; unknownInputs.push(`${tool} version not observed`) }
  }
  const restoredLocks = allFiles.filter(file => /(?:package-lock\.json|pnpm-lock\.yaml)$/.test(file))
    .map(file => ({file: path.relative(clone, file).replaceAll('\\', '/'), sha256: digest(readFileSync(file))}))
  if (!restoredLocks.length) unknownInputs.push('resolved JavaScript dependency locks not observed')
  const tracked = git(apiRoot, 'ls-files', '-z').split('\0').filter(Boolean)
  const inputFiles = tracked.filter(file => file.startsWith('eng/') || file.startsWith('.github/')
    || /(?:lock|\.props$|\.targets$|global\.json$|\.csproj$|NuGet\.Config$)/i.test(file))
  const manifest = {schemaVersion: 1, repository: 'Harborline-Software/harborline-api',
    candidateTree: git(apiRoot, 'rev-parse', 'HEAD^{tree}'), lane: 'host',
    dependencies: {evaluated, native, restoredLocks, compilerInputs: normalizeValue(compilerInputs)}, producer: {files: inputFiles.map(file => ({file,
      sha256: digest(readFileSync(path.join(apiRoot, file)))}))},
    toolchain: {tools, node: process.versions, sdkPolicy: JSON.parse(readFileSync(path.join(apiRoot, 'global.json'), 'utf8'))},
    platform: {os: platform(), architecture: arch(), release: release(), image: env.ImageOS ?? null,
      imageVersion: env.ImageVersion ?? null},
    pins: {platform: external('platform', 'HARBORLINE_PLATFORM_REPO', 'eng/platform-pin.json'),
      quality: external('quality', 'HARBORLINE_QUALITY_REPO', 'eng/quality-pin.json'),
      control: external('control', 'HARBORLINE_CONTROL_REPO')},
    selection: {host: 'Lane!=perf', contracts: 'all', capability: 'all', hostBaseline, quality},
    coverage: {enabled: coverage},
    // Commit-dependent work is deliberately not reusable across commits until evaluated explicitly.
    commitInputs: {scope: 'host-exact-clone', packaging: 'separate lane; no package verdict covered',
      mutation: 'separate PR-only lane; no mutation verdict covered'}, unknownInputs}
  return {schemaVersion: 1, mode: 'shadow', candidateSha: git(apiRoot, 'rev-parse', 'HEAD'),
    fingerprint: fingerprint(manifest), inputs: manifest, reuseAuthorized: false}
}
export function persistInputShadow(options) {
  const directory = path.join(options.apiRoot, '.claude/gate-evidence')
  try {
    mkdirSync(directory, {recursive: true})
    const result = collectInputs(options)
    writeFileSync(path.join(directory, 'validation-inputs-shadow.json'), JSON.stringify(result, null, 2) + '\n')
    return result
  } catch (error) {
    // Diagnostic collection cannot erase or redefine the existing gate result.
    const result = {schemaVersion: 1, mode: 'shadow', reuseAuthorized: false,
      collectionFailed: true, reason: /^[A-Z0-9_]{1,64}$/.test(error?.code ?? '') ? error.code : 'INPUT_COLLECTION_FAILED'}
    try { writeFileSync(path.join(directory, 'validation-inputs-shadow.json'), JSON.stringify(result, null, 2) + '\n') }
    catch { /* Diagnostic storage failures cannot replace the gate verdict. */ }
    return result
  }
}
