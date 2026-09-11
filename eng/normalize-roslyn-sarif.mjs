#!/usr/bin/env node
// Roslyn's compiler ErrorLog includes assembly-level diagnostics that have no
// physical location, while CQG intentionally refuses such unanchored findings.
// Rewrite every retained location relative to the repository, and add a stable
// partial fingerprint derived from that clone-independent location and project output.
import {createHash} from 'node:crypto'
import {readFileSync, writeFileSync, realpathSync} from 'node:fs'
import {fileURLToPath} from 'node:url'
import path from 'node:path'

const slash = value => value.replaceAll('\\', '/')
const trimTrailingSlash = value => value.length > 1 ? value.replace(/\/+$/, '') : value

export const repositoryRelativePath = (uri, repoRoot) => {
  let location = uri
  if (/^file:/i.test(location)) {
    const parsed = new URL(location)
    location = parsed.pathname
    if (parsed.hostname && parsed.hostname !== 'localhost') location = `//${parsed.hostname}${location}`
  } else {
    location = decodeURIComponent(location)
  }
  location = slash(location).replace(/^\/([A-Za-z]:\/)/, '$1')
  // The compiler writes the RESOLVED path. On macOS the temp clone lives under /var/folders, a
  // symlink to /private/var, so the root as given and the root the compiler saw differ; both count.
  const asRoot = value => trimTrailingSlash(slash(value).replace(/^\/([A-Za-z]:\/)/, '$1'))
  const roots = [asRoot(repoRoot)]
  try { const real = asRoot(realpathSync.native(repoRoot)); if (!roots.includes(real)) roots.push(real) } catch { /* a root that does not exist compares as given */ }
  const windows = /^[A-Za-z]:\//.test(roots[0])
  const comparableLocation = windows ? location.toLowerCase() : location
  for (const root of roots) {
    const comparableRoot = windows ? root.toLowerCase() : root
    if (comparableLocation === comparableRoot) throw new Error('SARIF location names the repository directory')
    if (comparableLocation.startsWith(`${comparableRoot}/`)) return location.slice(root.length + 1)
  }
  if (!/^(?:[A-Za-z]:\/|\/|\/\/)/.test(location)) return location.replace(/^\.\//, '')
  throw new Error(`SARIF location is outside repository: ${uri}`)
}

const fingerprint = (result, project) => {
  const physical = result.locations[0].physicalLocation
  const location = physical.artifactLocation.uri
  const region = physical.region
  return 'sha256:' + createHash('sha256').update(JSON.stringify([
    ...(project === undefined ? [] : [project]),
    result.ruleId,
    location,
    region.startLine,
    region.startColumn ?? null,
    region.endLine ?? null,
    region.endColumn ?? null,
  ])).digest('hex')
}

const projectFingerprint = project => 'sha256:' + createHash('sha256').update(project).digest('hex')

export const normalizeSarifFile = (file, repoRoot) => {
  const sarif = JSON.parse(readFileSync(file, 'utf8'))
  if (sarif.version !== '2.1.0' || !Array.isArray(sarif.runs)) throw new Error('expected SARIF 2.1.0')
  // Directory.Build.targets names each compiler ErrorLog after MSBuildProjectName. Including that
  // stable output identity prevents diagnostics at one source location from different projects from
  // collapsing into one quality-baseline identity.
  const project = path.basename(file).replace(/\.sarif$/i, '')
  for (const run of sarif.runs) {
    if (!Array.isArray(run.results)) throw new Error('SARIF run has no results array')
    if (run.tool?.driver?.name === 'Microsoft (R) Visual C# Compiler') run.tool.driver.name = 'roslyn'
    run.results = run.results.filter(result => {
      const physical = result.locations?.[0]?.physicalLocation
      return typeof result.ruleId === 'string'
        && typeof physical?.artifactLocation?.uri === 'string'
        && Number.isInteger(physical?.region?.startLine)
        && physical.region.startLine > 0
    })
    for (const result of run.results) {
      for (const location of result.locations) {
        const artifact = location?.physicalLocation?.artifactLocation
        if (typeof artifact?.uri === 'string') artifact.uri = repositoryRelativePath(artifact.uri, repoRoot)
      }
      result.partialFingerprints = {
        ...(result.partialFingerprints && typeof result.partialFingerprints === 'object'
          ? result.partialFingerprints : {}),
        // Retain v1 for a compatibility bridge while the baseline is re-pinned. v2 adds
        // the project output, and project/v1 lets the baseline writer use the same input
        // without depending on a host-specific artifact path.
        'harborline/primary-location/v1': fingerprint(result),
        'harborline/project/v1': projectFingerprint(project),
        'harborline/primary-location/v2': fingerprint(result, project),
      }
    }
  }
  writeFileSync(file, JSON.stringify(sarif) + '\n')
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2)
  if (args[0] !== '--repo-root' || !args[1] || args.length < 3) {
    throw new Error('usage: normalize-roslyn-sarif.mjs --repo-root <path> <sarif-file> [sarif-file...]')
  }
  for (const file of args.slice(2)) normalizeSarifFile(file, args[1])
}
