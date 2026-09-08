#!/usr/bin/env node

import { readFile, readdir, writeFile } from 'node:fs/promises'
import { relative, resolve } from 'node:path'
import process from 'node:process'

const root = resolve(import.meta.dirname, '../..')
export const inventoryPath = resolve(root, 'packages/contracts/protocol/boundary-inventory.json')

export const SCHEMA_VERSION = 'shipyard.runtime-boundary-inventory/v2'

// `.MapXxx(` spellings in apps/local-node-host that are NOT themselves a runtime boundary.
// Everything else that matches `.Map<Uppercase>(` is a HARD ERROR — the first version of this
// walker matched only `.MapGet/Post/Put/Delete/Patch(` and silently omitted the PATCH maintenance
// route, the /ws sync transport and /health, so the artifact read as complete while three live
// production boundaries were invisible. An unknown registration form must break the build, not
// disappear from the denominator.
const NON_BOUNDARY_MAP_CALLS = new Map([
  ['MapGroup', 'ASP.NET route grouping — contributes a prefix; the routes inside are discovered on their own'],
  ['MapApiRoutes', 'SharedHostedWebApp composition helper — runs a configure callback whose Map* calls are discovered'],
  ['MapCore', 'AdmissionRoutes private composition helper — its Map* calls are discovered'],
  ['MapDesktopPlaneOnlyGroup', 'route-group + web-plane fence helper — returns a RouteGroupBuilder, registers no route'],
  ['MapFounderWebAdmissionGroup', 'route-group + admission fence helper — returns a RouteGroupBuilder, registers no route'],
  ['MapHealthCheckIfAbsent', 'idempotent wrapper whose body calls MapHealthChecks — counting it too would double-count /health'],
  ['MapPermissions', 'SelectedSessionIdentityRoutes composition helper — its body calls MapGet(PermissionsPath), which the walk already discovers at that registration site; counting the call site too would double-count the permissions route'],
  ['MapInvoiceToMergeModel', 'domain object mapping — not routing'],
  ['MapToIPv6', 'IPAddress normalization — not routing'],
])

// These five composition helpers are qualified by their declaring file AND symbol. They are not
// global name exemptions: discovery verifies each declaration before allowing its call spelling.
// A same-named helper introduced elsewhere therefore remains unknown and fails closed.
const QUALIFIED_NON_BOUNDARY_MAP_CALLS = [
  { source: 'apps/local-node-host/Health/LocalNodeEndpointMapping.cs', symbol: 'LocalNodeEndpointMapping.MapAsync', reason: 'host startup orchestration; its hosted endpoints contain the route registrations' },
  { source: 'apps/local-node-host/Health/DeviceReachableProductDataRouteFence.cs', symbol: 'DeviceReachableProductDataRouteFence.MapDeviceReachableProductDataGroup', reason: 'route-group fence; it adds audience metadata but registers no route' },
  { source: 'apps/local-node-host/Health/LocalNodeHealthProbeEndpointRouteBuilderExtensions.cs', symbol: 'LocalNodeHealthProbeEndpointRouteBuilderExtensions.MapLocalNodeHealthProbes', reason: 'health composition helper; its three MapHealthChecks calls are discovered in the same file' },
  { source: 'apps/local-node-host/Health/PreAuthOperationalRouteFence.cs', symbol: 'PreAuthOperationalRouteFence.MapPreAuthOperationalGroup', reason: 'route-group fence; it adds pre-auth audience metadata but registers no route' },
  { source: 'apps/local-node-host/Health/SelectedSessionProductRouteFence.cs', symbol: 'SelectedSessionProductRouteFence.MapSelectedSessionProductGroup', reason: 'route-group fence; it adds selected-session audience metadata but registers no route' },
]

const HTTP_VERB_MAPS = new Set(['MapGet', 'MapPost', 'MapPut', 'MapDelete', 'MapPatch'])

export async function discoverRuntimeBoundaries() {
  return [...await discoverTauriCommands(), ...await discoverCapabilityFaces(), ...await discoverLocalNodeRoutes()]
    .sort((left, right) => left.id.localeCompare(right.id))
}

export async function buildInventory(previous = undefined) {
  const priorById = new Map((previous?.boundaries ?? []).map((entry) => [entry.id, entry]))
  const defaultReasons = previous?.defaultReasons ?? {}
  const discovered = await discoverRuntimeBoundaries()
  return {
    schemaVersion: SCHEMA_VERSION,
    description:
      'Runtime boundaries discovered from producer registrations. This artifact is a DENOMINATOR, not a coverage claim: '
      + 'most entries are deferred. Covered operations remain generator-owned by manifest.json.',
    defaultReasons,
    boundaries: discovered.map((boundary) => {
      const prior = priorById.get(boundary.id)
      const operationId = coveredOperationId(boundary)
      if (prior) return { ...boundary, status: prior.status, ...(operationId ? { operationId } : {}), ...(prior.reason ? { reason: prior.reason } : {}) }
      if (operationId) return { ...boundary, status: 'covered', operationId }
      if (boundary.kind === 'local-node-http' && defaultReasons.deferred) return { ...boundary, status: 'deferred' }
      throw new Error(`new runtime boundary requires an explicit inventory status and reason: ${boundary.id}`)
    }),
  }
}

export function validateInventory(inventory, discovered, manifest) {
  const errors = []
  if (inventory?.schemaVersion !== SCHEMA_VERSION) errors.push('boundary inventory has an unsupported schemaVersion')
  const defaultReasons = inventory?.defaultReasons ?? {}
  const entries = Array.isArray(inventory?.boundaries) ? inventory.boundaries : []
  const byId = new Map()
  for (const entry of entries) {
    if (byId.has(entry.id)) errors.push(`duplicate boundary inventory entry: ${entry.id}`)
    byId.set(entry.id, entry)
    if (!['covered', 'deferred', 'blocked'].includes(entry.status)) errors.push(`boundary ${entry.id} has invalid status ${JSON.stringify(entry.status)}`)
    // A reason may come from the entry OR from the status-wide default. The default exists so that a
    // blanket policy reads as ONE blanket policy: 213 copies of the same sentence looked like 213
    // authored judgements and carried none.
    const hasOwnReason = typeof entry.reason === 'string' && entry.reason.trim() !== ''
    const hasDefaultReason = typeof defaultReasons[entry.status] === 'string' && defaultReasons[entry.status].trim() !== ''
    if (entry.status !== 'covered' && !hasOwnReason && !hasDefaultReason) errors.push(`boundary ${entry.id} is ${entry.status} but has no reason and no default reason for that status`)
    if (hasOwnReason && entry.reason === defaultReasons[entry.status]) errors.push(`boundary ${entry.id} restates the default ${entry.status} reason — omit it or say something specific`)
    if (entry.status === 'covered' && entry.reason !== undefined) errors.push(`covered boundary ${entry.id} must not carry a deferred/blocked reason`)
    if (entry.status === 'covered' && typeof entry.operationId !== 'string') errors.push(`covered boundary ${entry.id} has no manifest operationId`)
    if (entry.status !== 'covered' && entry.operationId !== undefined) errors.push(`${entry.status} boundary ${entry.id} must not claim a manifest operationId`)
  }
  for (const status of Object.keys(defaultReasons)) {
    if (!['deferred', 'blocked'].includes(status)) errors.push(`defaultReasons has an entry for invalid status ${JSON.stringify(status)}`)
    else if (!entries.some((entry) => entry.status === status && (entry.reason === undefined || entry.reason === ''))) errors.push(`defaultReasons.${status} is unused — remove it`)
  }
  const discoveredIds = new Set(discovered.map((entry) => entry.id))
  for (const boundary of discovered) if (!byId.has(boundary.id)) errors.push(`unlisted runtime boundary: ${boundary.id}`)
  for (const boundary of discovered) {
    if (JSON.stringify(byId.get(boundary.id)?.responseFieldDtos) !== JSON.stringify(boundary.responseFieldDtos))
      errors.push(`response field DTO drift: ${boundary.id}`)
  }
  for (const entry of entries) if (!discoveredIds.has(entry.id)) errors.push(`stale boundary inventory entry: ${entry.id}`)
  if (manifest) {
    const manifestIds = manifest.ports.flatMap((port) => port.operations).map((operation) => operation.id).sort()
    const coveredIds = entries.filter((entry) => entry.status === 'covered').map((entry) => entry.operationId).sort()
    for (const id of manifestIds) if (!coveredIds.includes(id)) errors.push(`covered manifest operation has no runtime boundary: ${id}`)
    for (const id of coveredIds) if (!manifestIds.includes(id)) errors.push(`covered runtime boundary has no manifest operation: ${id}`)
  }
  return errors
}

function coveredOperationId(boundary) {
  if (boundary.kind === 'tauri-command') {
    if (boundary.command === 'get_data_location_status') return 'carrier.host.dataLocationStatus'
    if (boundary.command === 'get_device_capability_profile') return 'carrier.host.deviceCapabilityProfile'
    const suffix = boundary.command.split('_').map((part, index) => index === 0 ? part : `${part[0].toUpperCase()}${part.slice(1)}`).join('')
    return `carrier.host.${suffix}`
  }
  if (boundary.kind === 'capability-face') return `capability.${boundary.face}`
  if (boundary.kind === 'local-node-http' && boundary.method === 'GET' && boundary.routeExpression === 'RouteBase' && boundary.source.endsWith('/SyncStatusRoutes.cs')) return 'carrier.application.getSyncStatus'
  return undefined
}

async function discoverTauriCommands() {
  const source = 'apps/carrier/src-tauri/src/lib.rs'
  const fixtureSource = 'tooling/harborline-contract-codegen/fixtures/harborline-generate-handler.rs'
  const fixture = parseTauriCommands(await readFile(resolve(root, fixtureSource), 'utf8'), source)
  try {
    const live = parseTauriCommands(await readFile(resolve(root, source), 'utf8'), source)
    const commands = (entries) => entries.map((entry) => entry.command).sort()
    if (JSON.stringify(commands(live)) !== JSON.stringify(commands(fixture))) {
      throw new Error(`${fixtureSource} does not match the live generate_handler! command list in ${source}`)
    }
    return live
  } catch (error) {
    if (error?.code !== 'ENOENT') throw error
    return fixture
  }
}

// Exported so the shape-drift cases can be PROVEN against synthetic producers. The three parsers
// below each replaced a regex that failed open on a plausible refactor, and a claim that a parser
// now handles a shape is worth nothing unless a test feeds it that shape.
export function parseTauriCommands(text, source) {
  const handler = text.match(/generate_handler!\[([\s\S]*?)\]/)?.[1]
  if (!handler) throw new Error(`cannot find generate_handler! command list in ${source}`)
  // Parse by SPLITTING rather than by matching a `mod::command` shape: a bare `command,` entry (legal
  // Tauri, and the shape a future refactor most plausibly produces) matched nothing under the old
  // regex and vanished from the inventory without a word.
  return handler
    .split(',')
    .map((entry) => entry.replace(/\/\/[^\n]*/g, '').trim())
    .filter((entry) => entry !== '')
    .map((entry) => {
      const command = /^(?:[A-Za-z_]\w*::)*([A-Za-z_]\w*)$/.exec(entry)?.[1]
      if (!command) throw new Error(`unparsed generate_handler! entry in ${source}: ${JSON.stringify(entry)}`)
      return { id: `tauri-command:${command}`, kind: 'tauri-command', command, source }
    })
}

async function discoverCapabilityFaces() {
  const source = 'apps/capability-host/src/protocol/capability-port-adapter.ts'
  return parseCapabilityFaces(await readFile(resolve(root, source), 'utf8'), source)
}

export function parseCapabilityFaces(text, source) {
  // Brace-match the whole class rather than stopping at `private requireConnection`: the old bound
  // meant any face declared BELOW that helper, or declared without `async`, was silently not a
  // boundary. Every method is now enumerated and each one is either a face or explicitly private.
  const classBody = braceMatchedBody(text, /export class CapabilityShellPortAdapter implements CapabilityPort \{/, source)
  const faces = []
  for (const match of classBody.matchAll(/^ {2}(?<modifiers>(?:(?:private|public|protected|static|async|readonly)\s+)*)(?<name>#?[A-Za-z_]\w*)\s*(?:<[^>(]*>)?\s*\(/gm)) {
    const { modifiers, name } = match.groups
    if (name === 'constructor') continue
    if (name.startsWith('#') || /\bprivate\b/.test(modifiers)) continue
    faces.push({ id: `capability-face:${name}`, kind: 'capability-face', face: name, source })
  }
  if (faces.length === 0) throw new Error(`cannot find CapabilityShellPortAdapter faces in ${source}`)
  return faces
}

async function discoverLocalNodeRoutes() {
  const base = resolve(root, 'apps/local-node-host')
  const boundaries = []
  const paths = await walkCSharp(base)
  const qualifiedNonBoundaryCalls = await validateQualifiedNonBoundaryMapCalls(paths)
  for (const path of paths) {
    const source = relative(root, path).replaceAll('\\', '/')
    boundaries.push(...parseLocalNodeRoutes(await readFile(path, 'utf8'), source, qualifiedNonBoundaryCalls))
  }
  return boundaries
}

export function parseLocalNodeRoutes(text, source, qualifiedNonBoundaryCalls = new Set()) {
  const boundaries = []
  {
    const seen = new Map()
    const push = (kind, method, routeExpression) => {
      const structural = `${source}:${method}:${routeExpression}`
      const ordinal = (seen.get(structural) ?? 0) + 1
      seen.set(structural, ordinal)
      const boundary = { id: `${kind}:${method}:${source}:${routeExpression}${ordinal > 1 ? `:${ordinal}` : ''}`, kind, method, routeExpression, source }
      if (method === 'GET' && source.endsWith('/FormsRoutes.cs')) {
        const declaration = text.match(/public sealed record FormViewFieldDto\(([\s\S]*?)\)\s*\{/)
        if (!declaration) throw new Error(`cannot find FormViewFieldDto in ${source}`)
        const fields = [...declaration[1].matchAll(/JsonPropertyName\("([^"]+)"\)/g)].map(match => match[1])
        if (fields.length === 0) throw new Error(`no wire fields discovered for FormViewFieldDto in ${source}`)
        boundary.responseFieldDtos = { FormViewFieldDto: fields }
      }
      boundaries.push(boundary)
    }
    for (const match of text.matchAll(/\.(Map[A-Z]\w*)\s*\(/g)) {
      const call = match[1]
      const argumentStart = match.index + match[0].length
      if (NON_BOUNDARY_MAP_CALLS.has(call)) continue
      if (qualifiedNonBoundaryCalls.has(call)) continue
      if (HTTP_VERB_MAPS.has(call)) {
        push('local-node-http', call.slice(3).toUpperCase(), normalizeExpression(argumentAt(text, argumentStart, 0)))
        continue
      }
      if (call === 'MapMethods') {
        const routeExpression = normalizeExpression(argumentAt(text, argumentStart, 0))
        const verbs = [...normalizeExpression(argumentAt(text, argumentStart, 1)).matchAll(/"([A-Za-z]+)"/g)].map((verb) => verb[1].toUpperCase())
        if (verbs.length === 0) throw new Error(`cannot read the HTTP verbs of a MapMethods registration in ${source}`)
        for (const verb of verbs) push('local-node-http', verb, routeExpression)
        continue
      }
      if (call === 'MapWebSocketPath') {
        push('local-node-ws', 'WEBSOCKET', normalizeExpression(argumentAt(text, argumentStart, 0)))
        continue
      }
      if (call === 'MapHealthChecks') {
        push('local-node-http', 'GET', normalizeExpression(argumentAt(text, argumentStart, 0)))
        continue
      }
      throw new Error(
        `unknown endpoint registration form ${call}( in ${source} — classify it: add it to this walker `
        + 'as a boundary, or to NON_BOUNDARY_MAP_CALLS with the reason it registers no route',
      )
    }
  }
  return boundaries
}

async function validateQualifiedNonBoundaryMapCalls(paths) {
  const sources = new Map(await Promise.all(paths.map(async (path) => [
    relative(root, path).replaceAll('\\', '/'),
    await readFile(path, 'utf8'),
  ])))
  const calls = new Set()
  for (const exemption of QUALIFIED_NON_BOUNDARY_MAP_CALLS) {
    const [type, method] = exemption.symbol.split('.')
    const text = sources.get(exemption.source)
    if (!text) throw new Error(`qualified Map* exemption source is missing: ${exemption.source}`)
    if (!new RegExp(`\\b(?:class|static\\s+class)\\s+${type}\\b`).test(text)
      || !new RegExp(`\\b${method}\\s*\\(`).test(text)) {
      throw new Error(`qualified Map* exemption symbol is missing: ${exemption.source}:${exemption.symbol}`)
    }
    const declaration = new RegExp(`^\\s*(?:public|internal|private|protected)\\s+[^\\n;=]+\\b${method}\\s*\\(`, 'm')
    const declaringSources = [...sources]
      .filter(([, candidate]) => declaration.test(candidate))
      .map(([candidateSource]) => candidateSource)
    if (declaringSources.length !== 1 || declaringSources[0] !== exemption.source) {
      throw new Error(
        `qualified Map* exemption must resolve uniquely to ${exemption.source}:${exemption.symbol}; `
        + `found declarations in ${declaringSources.join(', ') || 'no production file'}`,
      )
    }
    calls.add(method)
  }
  return calls
}

async function walkCSharp(directory) {
  const output = []
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    // `tools/` holds the sync + two-user dev harnesses. They are separate csproj files and the host
    // excludes them from compilation (`DefaultItemExcludes` … `tools/**`), so their routes never ship
    // — counting them inflated the denominator by 10 with harness surface.
    if (entry.name === 'tests' || entry.name === 'tools' || entry.name === 'bin' || entry.name === 'obj') continue
    const path = resolve(directory, entry.name)
    if (entry.isDirectory()) output.push(...await walkCSharp(path))
    else if (entry.isFile() && entry.name.endsWith('.cs')) output.push(path)
  }
  return output.sort()
}

function braceMatchedBody(text, opener, source) {
  const start = opener.exec(text)
  if (!start) throw new Error(`cannot find ${opener} in ${source}`)
  let depth = 0
  for (let index = start.index + start[0].length - 1; index < text.length; index += 1) {
    if (text[index] === '{') depth += 1
    else if (text[index] === '}') {
      depth -= 1
      if (depth === 0) return text.slice(start.index + start[0].length, index)
    }
  }
  throw new Error(`unterminated class body in ${source}`)
}

function argumentAt(text, start, position) {
  let quote = null
  let escaped = false
  let depth = 0
  let index = start
  let argumentStart = start
  let argument = 0
  for (; index < text.length; index += 1) {
    const char = text[index]
    if (quote) {
      if (escaped) escaped = false
      else if (char === '\\') escaped = true
      else if (char === quote) quote = null
      continue
    }
    if (char === '"' || char === "'") quote = char
    else if ('([{'.includes(char)) depth += 1
    else if (')]}'.includes(char)) {
      if (depth === 0) break
      depth -= 1
    } else if (char === ',' && depth === 0) {
      if (argument === position) return text.slice(argumentStart, index)
      argument += 1
      argumentStart = index + 1
    }
  }
  if (argument === position) return text.slice(argumentStart, index)
  throw new Error('unterminated local-node Map* route registration')
}

function normalizeExpression(expression) {
  return expression.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/[^\n]*/g, '').replace(/\s+/g, ' ').trim()
}

async function main() {
  const discovered = await discoverRuntimeBoundaries()
  let current
  try { current = JSON.parse(await readFile(inventoryPath, 'utf8')) } catch { current = undefined }
  if (process.argv.includes('--write')) {
    const inventory = await buildInventory(current)
    await writeFile(inventoryPath, `${JSON.stringify(inventory, null, 2)}\n`)
    process.stdout.write(`wrote ${inventory.boundaries.length} runtime boundaries to ${relative(root, inventoryPath)}\n`)
    return
  }
  const manifest = JSON.parse(await readFile(resolve(root, 'packages/contracts/protocol/manifest.json'), 'utf8'))
  const errors = validateInventory(current, discovered, manifest)
  if (errors.length) {
    for (const error of errors) process.stderr.write(`${error}\n`)
    process.exitCode = 1
  } else {
    // Report the SPLIT, never "covers all N". The earlier wording said "covers all 232 discovered
    // operations" while 213 of them were deferred, which is the exact reads-as-coverage failure this
    // artifact exists to prevent.
    const tally = (status) => (current?.boundaries ?? []).filter((entry) => entry.status === status).length
    process.stdout.write(`runtime boundary inventory: ${discovered.length} discovered — ${tally('covered')} covered, ${tally('deferred')} deferred, ${tally('blocked')} blocked\n`)
  }
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(import.meta.filename)) await main()
