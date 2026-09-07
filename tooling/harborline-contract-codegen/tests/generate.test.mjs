import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { test } from 'node:test'
import { join, resolve } from 'node:path'
import { tmpdir } from 'node:os'
import {
  discoverRuntimeBoundaries,
  inventoryPath as boundaryInventoryPath,
  parseCapabilityFaces,
  parseLocalNodeRoutes,
  parseTauriCommands,
  validateInventory,
} from '../boundary-inventory.mjs'

const root = resolve(import.meta.dirname, '../../..')
const generator = resolve(root, 'tooling/harborline-contract-codegen/generate.mjs')
const manifestPath = resolve(root, 'packages/contracts/protocol/manifest.json')
const schemaPath = resolve(root, 'packages/contracts/protocol/schemas/carrier-protocol.schema.json')

test('checked-in Harborline protocol bindings match the canonical schema', () => {
  const result = spawnSync(process.execPath, ['tooling/harborline-contract-codegen/generate.mjs', '--check'], {
    cwd: root,
    encoding: 'utf8',
  })
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`)
})

test('boundary inventory classifies every runtime producer operation', async () => {
  const inventory = JSON.parse(readFileSync(boundaryInventoryPath, 'utf8'))
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'))
  const discovered = await discoverRuntimeBoundaries()
  assert.deepEqual(validateInventory(inventory, discovered, manifest), [])
})

test('boundary inventory gate rejects a route added without regeneration', async () => {
  const inventory = JSON.parse(readFileSync(boundaryInventoryPath, 'utf8'))
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'))
  const discovered = await discoverRuntimeBoundaries()
  discovered.push(...parseLocalNodeRoutes(
    'app.MapGet("/planted-route", () => Results.Ok());',
    'apps/local-node-host/Health/PlantedRoute.cs',
  ))
  assert.deepEqual(validateInventory(inventory, discovered, manifest), [
    'unlisted runtime boundary: local-node-http:GET:apps/local-node-host/Health/PlantedRoute.cs:"/planted-route"',
  ])
})

// The three parsers each replaced a regex that FAILED OPEN on a plausible refactor: a route
// registered by anything other than .MapGet/Post/Put/Delete/Patch, a Tauri command listed without a
// module path, or a Capability face that is not `async` or is declared below the private helper. Those
// shapes vanished from the artifact silently, so "232, complete" was published while three live
// production boundaries were missing. Each case below is fed to the parser as a synthetic producer.

test('local-node walk sees the registration forms beyond the five HTTP verb helpers', () => {
  const source = 'apps/local-node-host/Health/Synthetic.cs'
  const found = parseLocalNodeRoutes(
    [
      'app.MapMethods($"{RouteBase}/{{name}}", ["PATCH", "HEAD"], async (string name) => Results.Ok());',
      '_sharedApp.MapWebSocketPath("/ws", async (ws, ct) => { });',
      '_app.MapHealthChecks("/health");',
      'app.MapGroup("/api").MapDesktopPlaneOnlyGroup();',
    ].join('\n'),
    source,
  ).map((boundary) => `${boundary.kind}:${boundary.method}:${boundary.routeExpression}`)

  assert.deepEqual(found, [
    'local-node-http:PATCH:$"{RouteBase}/{{name}}"',
    'local-node-http:HEAD:$"{RouteBase}/{{name}}"',
    'local-node-ws:WEBSOCKET:"/ws"',
    'local-node-http:GET:"/health"',
  ])
})

test('an unclassified endpoint registration form fails the walk instead of disappearing', () => {
  assert.throws(
    () => parseLocalNodeRoutes('app.MapSseStream("/events", null);', 'apps/local-node-host/Health/Synthetic.cs'),
    /unknown endpoint registration form MapSseStream\(/,
  )
})

test('qualified non-boundary Map names are not global exemptions', () => {
  for (const call of [
    'MapAsync',
    'MapDeviceReachableProductDataGroup',
    'MapLocalNodeHealthProbes',
    'MapPreAuthOperationalGroup',
    'MapSelectedSessionProductGroup',
  ]) {
    assert.throws(
      () => parseLocalNodeRoutes(`app.${call}();`, 'apps/local-node-host/Health/Synthetic.cs'),
      new RegExp(`unknown endpoint registration form ${call}\\(`),
    )
  }
})

test('tauri command discovery reads a bare command entry and rejects an unparsed one', () => {
  const source = 'apps/carrier/src-tauri/src/lib.rs'
  assert.deepEqual(
    parseTauriCommands('tauri::generate_handler![capability::capability_invoke, bare_command,]', source).map((entry) => entry.command),
    ['capability_invoke', 'bare_command'],
  )
  assert.throws(
    () => parseTauriCommands('tauri::generate_handler![capability::capability_invoke(), ]', source),
    /unparsed generate_handler! entry/,
  )
})

test('capability face discovery sees a non-async face and one declared below the private helper', () => {
  const source = 'apps/capability-host/src/protocol/capability-port-adapter.ts'
  const faces = parseCapabilityFaces(
    [
      'export class CapabilityShellPortAdapter implements CapabilityPort {',
      '  constructor(options: CapabilityPortAdapterOptions) { this.#options = options }',
      '  async announce(request: AnnounceRequest): Promise<AnnounceResult> { return this.#send(request) }',
      '  observe(request: ObserveRequest): HealthReport { return this.#report(request) }',
      '  private requireConnection(runtimeId: string): RuntimeConnection { return this.#lookup(runtimeId) }',
      '  async compose(request: ComposeRequest): Promise<ComposeResult> { return this.#send(request) }',
      '  #send(request: unknown): never { throw new Error("stub") }',
      '}',
    ].join('\n'),
    source,
  ).map((face) => face.face)

  assert.deepEqual(faces, ['announce', 'observe', 'compose'])
})

test('every declared schema constraint has a generated enforcement mechanism', () => {
  const typescript = readFileSync(resolve(root, 'packages/contracts/src/generated/harborline-protocol.generated.ts'), 'utf8')
  const csharp = readFileSync(resolve(root, 'packages/contracts/Generated/HarborlineProtocol.g.cs'), 'utf8')
  const rust = readFileSync(resolve(root, 'packages/contracts/rust/src/generated.rs'), 'utf8')

  assert.match(typescript, /parseHarborlineProtocolModel/)
  assert.match(typescript, /Number\.isFinite/)
  assert.match(typescript, /above maximum 1/)
  assert.match(typescript, /above maximum 9007199254740991/)
  assert.match(typescript, /unexpected property/)
  assert.match(csharp, /enum PrincipalKind/)
  assert.match(csharp, /ArgumentOutOfRangeException/)
  assert.match(csharp, /9007199254740991L/)
  assert.match(csharp, /JsonUnmappedMemberHandling\.Disallow/)
  assert.match(rust, /enum PrincipalKind/)
  assert.match(rust, /value outside schema range/)
  assert.match(rust, /> 9007199254740991/)
  assert.match(rust, /serialize_sync_cadence_round_interval_seconds/)
  assert.match(rust, /serde::ser::Error::custom\("value outside schema range"\)/)
  assert.match(rust, /serde\(deny_unknown_fields\)/)
})

test('generated projections preserve presence, null, and exact wire-name semantics', () => {
  const csharp = readFileSync(resolve(root, 'packages/contracts/Generated/HarborlineProtocol.g.cs'), 'utf8')
  const rust = readFileSync(resolve(root, 'packages/contracts/rust/src/generated.rs'), 'utf8')

  assert.match(csharp, /\[JsonConverter\(typeof\(PrincipalJsonConverter\)\)\]/)
  assert.match(csharp, /property name must match exact wire casing/)
  assert.match(csharp, /CapabilityResult: missing required property error/)
  assert.match(csharp, /RendererLogEntry\.stack: value must not be null/)
  assert.match(csharp, /IReadOnlyDictionary<string, JsonElement>\? Extensions/)
  assert.match(rust, /deserialize_capability_result_error/)
  assert.match(rust, /deserialize_renderer_log_entry_stack/)
  assert.match(rust, /value\.is_none\(\).*value must not be null/)
})

test('OpenAPI paths, methods, operation ids, models, and security project from the manifest', async () => {
  const manifest = (await import('../../../packages/contracts/protocol/manifest.json', {
    with: { type: 'json' },
  })).default
  const openapi = JSON.parse(readFileSync(
    resolve(root, 'packages/contracts/protocol/openapi/harborline-application.openapi.json'),
    'utf8',
  ))
  const operations = manifest.ports.flatMap((port) => port.operations)
    .filter((operation) => operation.path)
  for (const operation of operations) {
    const projection = openapi.paths[operation.path]?.[operation.httpMethod.toLowerCase()]
    assert.equal(projection?.operationId, operation.id)
    assert.equal(
      projection?.responses?.['200']?.content?.['application/json']?.schema?.$ref,
      `../schemas/carrier-protocol.schema.json#/$defs/${operation.response}`,
    )
    assert.deepEqual(
      projection?.security,
      operation.security?.map((scheme) => ({ [scheme]: [] })),
    )
  }
  assert.deepEqual(openapi.components?.securitySchemes, manifest.securitySchemes)
})

test('manifest contains the complete native host command inventory', async () => {
  const manifest = (await import('../../../packages/contracts/protocol/manifest.json', {
    with: { type: 'json' },
  })).default
  const commands = manifest.ports
    .flatMap((port) => port.operations)
    .map((operation) => operation.hostCommand)
    .filter(Boolean)
  assert.deepEqual(commands.sort(), [
    'append_renderer_log',
    'capability_cp_demo_execute',
    'capability_health',
    'capability_invoke',
    'current_principal',
    'get_data_location_status',
    'get_device_capability_profile',
    'get_peer_sync_config',
    'node_status',
    'set_peer_sync_config',
  ])
})

// RETIRED 2026-08-23, control ticket 084. Two tests scraped the Tauri earlier app's own sources:
// 'production renderer transport code reaches Tauri only through its adapter' (that app's `src`)
// and 'Rust Tauri registration implements the generated host command inventory'
// (its `src-tauri/src/lib.rs`). Both were pinned expected failures because that app never
// lived in this repository.
//
// The disposition is now settled: the earlier app's successor is harborline-app, whose native seam is
// src/Harborline.App.Blazor.Hybrid/NativeHostContracts.cs - a .NET contract. There is no Tauri and
// no Rust anywhere in it, so these two fences have no successor to be re-pointed at. Carrying them
// as permanent expected failures would assert that a Tauri earlier app is still coming.
//
// What is NOT lost: findTauriImportOffenders is still proven by 'renderer guard flags a Tauri API
// subpath the old guard missed', which feeds the parser a synthetic source, so the guard's BEHAVIOUR
// stays covered even though nothing in this repository is scanned by it any more.

test('renderer guard flags a Tauri API subpath the old guard missed', () => {
  const sourceRoot = mkdtempSync(join(tmpdir(), 'codegen-tauri-guard-'))
  try {
    writeFileSync(join(sourceRoot, 'missed-tauri-subpath.ts'),
      "import { listen } from '@tauri-apps/api/event'\n")
    assert.deepEqual(findTauriImportOffenders(sourceRoot), ['missed-tauri-subpath.ts'])
  } finally {
    rmSync(sourceRoot, { recursive: true, force: true })
  }
})

test('Capability protocol adapter cannot call the raw shell invoke path', () => {
  const adapter = readFileSync(
    resolve(root, 'apps/capability-host/src/protocol/capability-port-adapter.ts'),
    'utf8',
  )
  assert.equal(adapter.match(/shell\.invoke\.bind\(shell\)/g)?.length, 1)
  const adapterClass = adapter.slice(
    adapter.indexOf('export class CapabilityShellPortAdapter'),
  )
  const adapterOptions = adapter.slice(
    adapter.indexOf('export interface CapabilityPortAdapterOptions'),
    adapter.indexOf('export class CapabilityShellPortAdapter'),
  )
  assert.doesNotMatch(adapterOptions, /securedInvoke|executor/)
  assert.match(adapterClass, /this\.#shell = options\.shell/)
  assert.match(adapterClass, /this\.#securedInvoke = createSecuredCapabilityInvoke\(options\)/)
  assert.doesNotMatch(adapterClass, /this\.options/)
  assert.doesNotMatch(adapterClass, /\.invoke\s*\(/)
  assert.match(adapterClass, /this\.#securedInvoke\(invokeRequest\)/)
})

test('array-item enums generate closed projections in all three targets', () => {
  withSyntheticSchema('array-item-enum', (schema) => {
    schema.$defs.ArrayEnum = objectDefinition({
      values: { type: 'array', items: { type: 'string', enum: ['one', 'two'] } },
    })
  }, ({ result, outputDir }) => {
    assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`)
    const typescript = readFileSync(join(outputDir, 'harborline-protocol.generated.ts'), 'utf8')
    const csharp = readFileSync(join(outputDir, 'HarborlineProtocol.g.cs'), 'utf8')
    const rust = readFileSync(join(outputDir, 'generated.rs'), 'utf8')

    assert.match(typescript, /values: \("one" \| "two"\)\[\]/)
    assert.match(typescript, /value is outside enum/)
    assert.match(csharp, /\[JsonConverter\(typeof\(ArrayEnumValuesJsonConverter\)\)\]\npublic enum ArrayEnumValues/)
    assert.match(csharp, /public required IReadOnlyList<ArrayEnumValues> Values/)
    assert.match(csharp, /public sealed class ArrayEnumValuesJsonConverter : JsonConverter<ArrayEnumValues>/)
    assert.match(rust, /pub enum ArrayEnumValues/)
    assert.match(rust, /pub values: Vec<ArrayEnumValues>/)
    assert.match(rust, /#\[serde\(rename = "one"\)\]/)
  })
})

test('generation rejects required properties that are not declared before writing projections', () => {
  assertSyntheticRejected('MissingRequired', (schema) => {
    schema.$defs.MissingRequired = {
      type: 'object',
      additionalProperties: true,
      required: ['mustExist'],
      properties: {},
    }
  }, "MissingRequired: required property 'mustExist'", 'properties')
})

test('generation rejects object definitions without explicit additionalProperties before writing projections', () => {
  assertSyntheticRejected('ImplicitAdditionalProperties', (schema) => {
    schema.$defs.ImplicitAdditionalProperties = {
      type: 'object',
      required: [],
      properties: {},
    }
  }, 'ImplicitAdditionalProperties: object definition must declare additionalProperties', 'TypeScript, C#, and Rust')
})

test('generation rejects scalar additionalProperties before writing projections', () => {
  assertSyntheticRejected('ScalarAdditionalProperties', (schema) => {
    schema.$defs.ScalarAdditionalProperties = {
      type: 'object',
      additionalProperties: 'string',
      required: [],
      properties: {},
    }
  }, 'ScalarAdditionalProperties: additionalProperties must be a boolean or schema object', 'TypeScript, C#, and Rust')
})

test('generation rejects non-enum integers without a maximum before writing projections', () => {
  assertSyntheticRejected('UnboundedInteger', (schema) => {
    schema.$defs.UnboundedInteger = objectDefinition({
      value: { type: 'integer', minimum: 0 },
    })
  }, 'UnboundedInteger.value: non-enum integer must declare maximum', 'TypeScript, C#, and Rust')
})

test('generation rejects nested array-item enums before writing projections', () => {
  assertSyntheticRejected('NestedArrayEnum', (schema) => {
    schema.$defs.NestedArrayEnum = objectDefinition({
      values: {
        type: 'array',
        items: { type: 'array', items: { type: 'string', enum: ['one', 'two'] } },
      },
    })
  }, 'NestedArrayEnum.values[]', 'TypeScript')
})

test('open projections absorb case-variant properties while closed projections reject them', () => {
  const typescript = readFileSync(resolve(root, 'packages/contracts/src/generated/harborline-protocol.generated.ts'), 'utf8')
  const csharp = readFileSync(resolve(root, 'packages/contracts/Generated/HarborlineProtocol.g.cs'), 'utf8')
  const rust = readFileSync(resolve(root, 'packages/contracts/rust/src/generated.rs'), 'utf8')
  const artifactTypeScript = typescript.slice(
    typescript.indexOf('function assertArtifact'),
    typescript.indexOf('function assertCapabilityResult'),
  )
  const section = (name) => {
    const start = csharp.indexOf(`private static void Validate${name}`)
    const end = csharp.indexOf('\n}\n\n[JsonConverter', start)
    return csharp.slice(start, end)
  }
  const artifact = section('Artifact')
  const principal = section('Principal')
  const artifactRust = rust.slice(rust.indexOf('pub struct Artifact'), rust.indexOf('pub struct CapabilityResult'))
  assert.doesNotMatch(artifactTypeScript, /unexpected property/)
  assert.doesNotMatch(artifact, /property name must match exact wire casing/)
  assert.match(principal, /property name must match exact wire casing/)
  assert.match(artifactRust, /#\[serde\(flatten\)\]/)
  assert.match(csharp, /PropertyNameCaseInsensitive = false/)
})

test('generation rejects enums in unsupported map-value positions before writing projections', () => {
  assertSyntheticRejected('NestedEnum', (schema) => {
    schema.$defs.NestedEnum = objectDefinition({
      values: { type: 'object', additionalProperties: { type: 'string', enum: ['one', 'two'] } },
    })
  }, 'NestedEnum.values{}', 'C#')
})

test('generation rejects scalar enum definitions before writing projections', () => {
  assertSyntheticRejected('ScalarEnum', (schema) => {
    schema.$defs.ScalarEnum = { type: 'string', enum: ['one', 'two'] }
  }, 'ScalarEnum', 'C#')
})

test('generation rejects identifiers that require escaping', () => {
  assertSyntheticRejected('Hyphenated', (schema) => {
    schema.$defs.Hyphenated = objectDefinition({ 'bad-name': { type: 'string' } })
  }, 'Hyphenated.bad-name', 'TypeScript')
})

test('generation rejects normalized property-name collisions', () => {
  assertSyntheticRejected('CaseCollision', (schema) => {
    schema.$defs.CaseCollision = objectDefinition({
      foo: { type: 'string' },
      Foo: { type: 'string' },
    })
  }, 'CaseCollision', 'C#')
})

test('generation rejects Rust reserved property identifiers', () => {
  assertSyntheticRejected('RustReserved', (schema) => {
    schema.$defs.RustReserved = objectDefinition({ type: { type: 'string' } })
  }, 'RustReserved.type', 'Rust')
})

test('generation rejects unsupported schema keywords for every projection target', () => {
  assertSyntheticRejected('Unsupported', (schema) => {
    schema.$defs.Unsupported = objectDefinition({ value: { type: 'string', pattern: '^ok$' } })
  }, "Unsupported.value: unsupported schema keyword 'pattern'", 'TypeScript')
})

test('generation rejects extensions on models outside the explicit metadata allowlist', () => {
  assertSyntheticRejected('UndeclaredExtensions', (schema) => {
    schema.$defs.Principal.properties.extensions = { type: 'object', additionalProperties: true }
  }, 'Principal: extensions is reserved', 'extensions')
})

test('integer enum C# projections include a numeric converter', () => {
  withSyntheticSchema('integer-enum', (schema) => schema, ({ outputDir }) => {
    const csharp = readFileSync(join(outputDir, 'HarborlineProtocol.g.cs'), 'utf8')
    assert.match(csharp, /\[JsonConverter\(typeof\(DeviceCapabilityProfileSchemaVersionJsonConverter\)\)\]/)
    assert.match(csharp, /public override DeviceCapabilityProfileSchemaVersion Read[\s\S]*reader\.GetInt64\(\)/)
    assert.match(csharp, /public override void Write[\s\S]*writer\.WriteNumberValue/)
  })
})

test('array-of-union TypeScript projections parenthesize the item union', () => {
  withSyntheticSchema('array-union', (schema) => {
    schema.$defs.ArrayUnion = objectDefinition({
      values: {
        type: 'array',
        items: {
          anyOf: [
            { $ref: '#/$defs/ProtocolError' },
            { type: 'null' },
          ],
        },
      },
    })
  }, ({ outputDir }) => {
    const typescript = readFileSync(join(outputDir, 'harborline-protocol.generated.ts'), 'utf8')
    assert.match(typescript, /export interface ArrayUnion \{\n  values: \(ProtocolError \| null\)\[\]/)
  })
})

function objectDefinition(properties) {
  return {
    type: 'object',
    additionalProperties: false,
    required: Object.keys(properties),
    properties,
  }
}

const tauriRendererImport = /(?:from\s+|import\s*\(\s*|require\s*\(\s*)['"]@tauri-apps\/(?:api(?:\/[^'"]*)?|plugin-http(?:\/[^'"]*)?|plugin-shell(?:\/[^'"]*)?)['"]/
// Ticket 267: two exemption lists used to sit here — `tauriAdapterFiles` (two `protocol/tauri-*`
// adapter modules) and `tauriRendererExceptions` (one `copilot/` UI hook). Every path in both named
// a source file of the desktop shell retired under control ticket 084; none exists here or in any
// sibling repository,
// and the only caller of findTauriImportOffenders below feeds it a synthetic directory. So neither
// list could ever match a scanned file: they exempted nothing and documented a scan that no longer
// happens. Removed rather than repointed — there is no successor tree to point them at (the
// successor's native seam is a .NET contract, with no Tauri and no Rust). The guard's behaviour is
// unchanged and still proven by 'renderer guard flags a Tauri API subpath the old guard missed'.

function findTauriImportOffenders(sourceRoot) {
  return readdirSync(sourceRoot, { recursive: true })
    .filter((file) => /\.(ts|tsx)$/.test(file))
    .map((file) => file.replaceAll('\\', '/'))
    .filter((file) => !/\.test\.|\/__tests__\//.test(file))
    .filter((file) => tauriRendererImport.test(readFileSync(resolve(sourceRoot, file), 'utf8')))
}

function assertSyntheticRejected(name, mutate, construct, target) {
  withSyntheticSchema(name, mutate, ({ result, outputDir }) => {
    const output = `${result.stdout}\n${result.stderr}`
    assert.notEqual(result.status, 0, `${name} unexpectedly generated successfully`)
    assert.ok(output.includes(construct), output)
    assert.ok(output.includes(target), output)
    assert.deepEqual(readdirSync(outputDir), [])
  })
}

function withSyntheticSchema(name, mutate, callback) {
  const temporaryDirectory = mkdtempSync(join(tmpdir(), `codegen-contract-${name}-`))
  const outputDir = join(temporaryDirectory, 'generated')
  mkdirSync(outputDir)
  try {
    const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'))
    const schema = JSON.parse(readFileSync(schemaPath, 'utf8'))
    mutate(schema)
    const syntheticManifestPath = join(temporaryDirectory, 'manifest.json')
    const syntheticSchemaPath = join(temporaryDirectory, 'schema.json')
    writeFileSync(syntheticManifestPath, `${JSON.stringify(manifest, null, 2)}\n`)
    writeFileSync(syntheticSchemaPath, `${JSON.stringify(schema, null, 2)}\n`)
    const result = spawnSync(process.execPath, [
      generator,
      '--manifest', syntheticManifestPath,
      '--schema', syntheticSchemaPath,
      '--output-dir', outputDir,
    ], { cwd: root, encoding: 'utf8' })
    return callback({ result, outputDir })
  } finally {
    rmSync(temporaryDirectory, { recursive: true, force: true })
  }
}
