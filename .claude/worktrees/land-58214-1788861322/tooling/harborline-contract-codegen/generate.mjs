#!/usr/bin/env node

import { readFile, writeFile, mkdir } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import process from 'node:process'

const root = resolve(import.meta.dirname, '../..')
const manifestPath = resolve(root, option('--manifest', 'packages/contracts/protocol/manifest.json'))
const schemaPath = resolve(root, option('--schema', 'packages/contracts/protocol/schemas/carrier-protocol.schema.json'))
const outputRoot = resolve(root, option('--output-dir', '.'))
const outputs = {
  ts: resolve(outputRoot, outputRoot === root ? 'packages/contracts/src/generated/harborline-protocol.generated.ts' : 'harborline-protocol.generated.ts'),
  cs: resolve(outputRoot, outputRoot === root ? 'packages/contracts/Generated/HarborlineProtocol.g.cs' : 'HarborlineProtocol.g.cs'),
  rust: resolve(outputRoot, outputRoot === root ? 'packages/contracts/rust/src/generated.rs' : 'generated.rs'),
  openapi: resolve(outputRoot, outputRoot === root ? 'packages/contracts/protocol/openapi/harborline-application.openapi.json' : 'harborline-application.openapi.json'),
}
const check = process.argv.includes('--check')
// The C# namespace follows ticket 063's move to Harborline.* (harborline-api
// 0df02af, "Phase B"). That rename was applied to the generated output but never to this generator,
// which stayed behind in the source repository; it is the one place this copy diverges from
// the earlier repository at a5036ff8. The protocol id retains its earlier name -- it is wire
// data carried in the manifest, and changing it would break the contract rather than move a symbol.
const csharpNamespace = 'Harborline.Api.Protocol'
const targetLabels = {
  typescript: 'TypeScript',
  csharp: 'C#',
  rust: 'Rust',
}
const targetNames = Object.keys(targetLabels)
const extensibleMetadataModels = new Set([
  'RuntimeHealth',
  'RuntimeCapability',
  'AnnounceResult',
  'AddressResult',
  'ResolutionResult',
  'NodeStatus',
  'DataLocationStatus',
  'DeviceCapabilityProfile',
  'HarborlineSyncStatus',
])
const keywordCapabilities = Object.freeze({
  '$ref': { kind: 'semantic', targets: { typescript: true, csharp: true, rust: true }, positions: ['any'] },
  type: { kind: 'semantic', targets: { typescript: true, csharp: true, rust: true }, positions: ['definition', 'property', 'array-item', 'nested-array-item', 'map-value', 'union-alternative'] },
  enum: {
    kind: 'semantic',
    targets: { typescript: true, csharp: true, rust: true },
    positions: {
      typescript: ['definition', 'property', 'array-item', 'map-value'],
      csharp: ['property', 'array-item'],
      rust: ['property', 'array-item'],
    },
  },
  description: { kind: 'metadata', targets: { typescript: false, csharp: false, rust: false }, positions: ['any'] },
  properties: { kind: 'semantic', targets: { typescript: true, csharp: true, rust: true }, positions: ['definition'] },
  required: { kind: 'semantic', targets: { typescript: true, csharp: true, rust: true }, positions: ['definition'] },
  additionalProperties: { kind: 'semantic', targets: { typescript: true, csharp: true, rust: true }, positions: ['definition', 'property'] },
  items: { kind: 'semantic', targets: { typescript: true, csharp: true, rust: true }, positions: ['property', 'definition', 'array-item', 'nested-array-item', 'map-value'] },
  minimum: { kind: 'semantic', targets: { typescript: true, csharp: true, rust: true }, positions: ['property'] },
  maximum: { kind: 'semantic', targets: { typescript: true, csharp: true, rust: true }, positions: ['property'] },
  title: { kind: 'metadata', targets: { typescript: false, csharp: false, rust: false }, positions: ['any'] },
  anyOf: { kind: 'semantic', targets: { typescript: true, csharp: true, rust: true }, positions: ['property', 'array-item', 'map-value'] },
})
const documentKeywords = new Set(['$schema', '$id', 'title', 'description', 'type', '$defs'])
const reservedWords = {
  typescript: new Set([
    'break', 'case', 'catch', 'class', 'const', 'continue', 'debugger', 'default', 'delete', 'do',
    'else', 'enum', 'export', 'extends', 'false', 'finally', 'for', 'function', 'if', 'import',
    'in', 'instanceof', 'new', 'null', 'return', 'super', 'switch', 'this', 'throw', 'true',
    'try', 'typeof', 'var', 'void', 'while', 'with', 'as', 'implements', 'interface', 'let',
    'package', 'private', 'protected', 'public', 'static', 'yield', 'any', 'boolean', 'constructor',
    'declare', 'get', 'infer', 'is', 'keyof', 'module', 'namespace', 'never', 'number', 'object',
    'readonly', 'require', 'set', 'string', 'symbol', 'type', 'undefined', 'unique', 'unknown',
  ]),
  csharp: new Set([
    'abstract', 'as', 'base', 'bool', 'break', 'byte', 'case', 'catch', 'char', 'checked', 'class',
    'const', 'continue', 'decimal', 'default', 'delegate', 'do', 'double', 'else', 'enum', 'event',
    'explicit', 'extern', 'false', 'finally', 'fixed', 'float', 'for', 'foreach', 'goto', 'if',
    'implicit', 'in', 'int', 'interface', 'internal', 'is', 'lock', 'long', 'namespace', 'new',
    'null', 'object', 'operator', 'out', 'override', 'params', 'private', 'protected', 'public',
    'readonly', 'ref', 'return', 'sbyte', 'sealed', 'short', 'sizeof', 'stackalloc', 'static',
    'string', 'struct', 'switch', 'this', 'throw', 'true', 'try', 'typeof', 'uint', 'ulong',
    'unchecked', 'unsafe', 'ushort', 'using', 'virtual', 'void', 'volatile', 'while', 'add', 'alias',
    'ascending', 'async', 'await', 'by', 'descending', 'dynamic', 'equals', 'from', 'get', 'global',
    'group', 'init', 'into', 'join', 'let', 'nameof', 'on', 'orderby', 'partial', 'remove', 'select',
    'set', 'unmanaged', 'value', 'var', 'when', 'where', 'yield',
  ]),
  rust: new Set([
    'as', 'async', 'await', 'break', 'const', 'continue', 'crate', 'dyn', 'else', 'enum', 'extern',
    'false', 'fn', 'for', 'if', 'impl', 'in', 'let', 'loop', 'match', 'mod', 'move', 'mut', 'pub',
    'ref', 'return', 'self', 'Self', 'static', 'struct', 'super', 'trait', 'true', 'type', 'unsafe',
    'use', 'where', 'while', 'abstract', 'become', 'box', 'do', 'final', 'macro', 'override',
    'priv', 'typeof', 'unsized', 'virtual', 'yield', 'try',
  ]),
}

const manifest = JSON.parse(await readFile(manifestPath, 'utf8'))
const schema = JSON.parse(await readFile(schemaPath, 'utf8'))

validateInputs(manifest, schema)

const generated = {
  ts: generateTypeScript(manifest, schema.$defs),
  cs: generateCSharp(manifest, schema.$defs),
  rust: generateRust(manifest, schema.$defs),
  openapi: generateOpenApi(manifest),
}

let drift = false
for (const [language, path] of Object.entries(outputs)) {
  if (check) {
    let current = ''
    try {
      current = await readFile(path, 'utf8')
    } catch {
      // Missing output is drift.
    }
    if (current !== generated[language]) {
      console.error(`generated ${language} binding is stale: ${path}`)
      drift = true
    }
  } else {
    await mkdir(dirname(path), { recursive: true })
    await writeFile(path, generated[language])
    console.log(`generated ${path}`)
  }
}

if (drift) process.exitCode = 1

function validateInputs(candidateManifest, candidateSchema) {
  if (typeof candidateSchema !== 'object' || candidateSchema === null || Array.isArray(candidateSchema)) {
    throw new Error('schema must be an object')
  }
  if (candidateManifest.schemaDraft !== 'https://json-schema.org/draft/2020-12/schema') {
    throw new Error('manifest must declare JSON Schema Draft 2020-12')
  }
  if (candidateSchema.$schema !== candidateManifest.schemaDraft) {
    throw new Error('schema draft and manifest schemaDraft differ')
  }
  if (!/^\d+\.\d+\.\d+$/.test(candidateManifest.protocolVersion)) {
    throw new Error('protocolVersion must be semantic version syntax')
  }
  for (const key of Object.keys(candidateSchema)) {
    if (!documentKeywords.has(key)) throw new Error(`schema: unsupported schema keyword '${key}' (TypeScript, C#, Rust)`)
  }
  const definitions = candidateSchema.$defs ?? {}
  if (typeof definitions !== 'object' || definitions === null || Array.isArray(definitions)) {
    throw new Error('schema: $defs must be an object')
  }
  const securitySchemes = candidateManifest.securitySchemes ?? {}
  for (const [name, scheme] of Object.entries(securitySchemes)) {
    if (!/^[A-Za-z][A-Za-z0-9_-]*$/.test(name)) {
      throw new Error(`invalid security scheme name ${name}`)
    }
    if (scheme.type === 'http' && typeof scheme.scheme !== 'string') {
      throw new Error(`security scheme ${name}: http schemes must declare scheme`)
    }
    if (scheme.type === 'apiKey' && !['header', 'query', 'cookie'].includes(scheme.in)) {
      throw new Error(`security scheme ${name}: apiKey schemes must declare header, query, or cookie in`)
    }
  }
  const portNames = new Set()
  const operationIds = new Set()
  const hostCommands = new Set()
  for (const port of candidateManifest.ports ?? []) {
    if (portNames.has(port.name)) throw new Error(`duplicate port ${port.name}`)
    portNames.add(port.name)
    const methodNames = new Set()
    for (const operation of port.operations ?? []) {
      if (operationIds.has(operation.id)) throw new Error(`duplicate operation id ${operation.id}`)
      if (methodNames.has(operation.methodName)) {
        throw new Error(`duplicate method ${port.name}.${operation.methodName}`)
      }
      operationIds.add(operation.id)
      methodNames.add(operation.methodName)
      if (!(operation.request in definitions)) throw new Error(`unknown request ${operation.request}`)
      if (!(operation.response in definitions)) throw new Error(`unknown response ${operation.response}`)
      if (operation.hostCommand) {
        if (hostCommands.has(operation.hostCommand)) {
          throw new Error(`duplicate host command ${operation.hostCommand}`)
        }
        hostCommands.add(operation.hostCommand)
      }
      if ((operation.path === undefined) !== (operation.httpMethod === undefined)) {
        throw new Error(`${operation.id}: path and httpMethod must be declared together`)
      }
      for (const security of operation.security ?? []) {
        if (!(security in securitySchemes)) {
          throw new Error(`${operation.id}: unknown security scheme ${security}`)
        }
      }
      for (const trusted of operation.trustedRequestFields ?? []) {
        if (definitions[operation.request].properties?.[trusted]) {
          throw new Error(`${operation.id}: trusted request field '${trusted}' must be host context, not serialized input`)
        }
      }
    }
  }
  if (hostCommands.size !== 10) {
    throw new Error(`HarborlineHostPort must enumerate exactly 10 native commands; found ${hostCommands.size}`)
  }
  validateMetadataExtensions(definitions)
  validateIdentifiers(candidateManifest, definitions)
  for (const [name, definition] of Object.entries(definitions)) validateDefinition(name, definition, definitions)
}

function validateMetadataExtensions(definitions) {
  for (const name of extensibleMetadataModels) {
    if (!(name in definitions)) throw new Error(`metadata extension allowlist names unknown model ${name}`)
    const property = definitions[name].properties?.extensions
    if (!property || property.type !== 'object' || property.additionalProperties !== true) {
      throw new Error(`${name}: allowlisted metadata carrier must declare an open extensions object`)
    }
  }
  for (const [name, definition] of Object.entries(definitions)) {
    if (definition.properties?.extensions && !extensibleMetadataModels.has(name)) {
      throw new Error(`${name}: extensions is reserved for allowlisted metadata carriers`)
    }
  }
}

function validateDefinition(path, value, definitions, position = 'definition') {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error(`${path}: schema value has no TypeScript, C#, or Rust projection`)
  }
  for (const key of Object.keys(value)) {
    const capability = keywordCapabilities[key]
    if (!capability) throw new Error(`${path}: unsupported schema keyword '${key}' (TypeScript, C#, Rust)`)
    if (capability.kind === 'semantic') {
      const missingTarget = targetNames.find((target) => !capability.targets[target])
      if (missingTarget) {
        throw new Error(`${path}: schema keyword '${key}' has no ${targetLabels[missingTarget]} projection`)
      }
      const unsupportedPosition = targetNames.find((target) => {
        const positions = Array.isArray(capability.positions) ? capability.positions : capability.positions[target]
        return !positions.includes('any') && !positions.includes(position)
      })
      if (unsupportedPosition) {
        throw new Error(`${path}: schema keyword '${key}' has no ${targetLabels[unsupportedPosition]} projection at ${position}`)
      }
    }
  }
  if (value.$ref && !/^#\/\$defs\/[A-Za-z][A-Za-z0-9]*$/.test(value.$ref)) {
    throw new Error(`${path}: only local $defs references are supported`)
  }
  if (value.$ref && !(refName(value.$ref) in definitions)) {
    throw new Error(`${path}: unknown schema reference ${value.$ref}`)
  }
  if (Array.isArray(value.type)) {
    if (value.type.length !== 2 || !value.type.includes('null') || value.type.filter((item) => item !== 'null').length !== 1) {
      throw new Error(`${path}: type union has no TypeScript, C#, or Rust projection`)
    }
  }
  if (value.enum !== undefined) validateEnum(path, value)
  if (position === 'definition' && value.type !== 'object' && value.enum === undefined) {
    throw new Error(`${path}: non-object definition has no Rust projection at definition position`)
  }
  if (position === 'definition' && value.type === 'object' && !Object.hasOwn(value, 'additionalProperties')) {
    throw new Error(`${path}: object definition must declare additionalProperties for consistent TypeScript, C#, and Rust strictness`)
  }
  if (Object.hasOwn(value, 'additionalProperties')
    && typeof value.additionalProperties !== 'boolean'
    && (typeof value.additionalProperties !== 'object' || value.additionalProperties === null || Array.isArray(value.additionalProperties))) {
    throw new Error(`${path}: additionalProperties must be a boolean or schema object for consistent TypeScript, C#, and Rust projections`)
  }
  if (value.properties !== undefined && (value.type !== 'object' || position !== 'definition')) {
    throw new Error(`${path}: inline properties have no TypeScript, C#, or Rust projection at ${position}`)
  }
  if (value.required !== undefined && (!Array.isArray(value.required) || value.type !== 'object')) {
    throw new Error(`${path}: required has no TypeScript, C#, or Rust projection at ${position}`)
  }
  if (Array.isArray(value.required) && value.type === 'object') {
    const properties = value.properties ?? {}
    for (const requiredProperty of value.required) {
      if (typeof requiredProperty !== 'string' || !Object.hasOwn(properties, requiredProperty)) {
        throw new Error(`${path}: required property '${requiredProperty}' is not declared in properties`)
      }
    }
  }
  if (value.items !== undefined && value.type !== 'array') {
    throw new Error(`${path}: items has no TypeScript, C#, or Rust projection at ${position}`)
  }
  if (position === 'nested-array-item') {
    throw new Error(`${path}: nested arrays have no TypeScript, C#, or Rust projection`)
  }
  if ((value.minimum !== undefined || value.maximum !== undefined) && !['integer', 'number'].includes(nonNullType(value))) {
    throw new Error(`${path}: numeric bounds have no TypeScript, C#, or Rust projection`)
  }
  if (nonNullType(value) === 'integer' && value.enum === undefined && !Object.hasOwn(value, 'maximum')) {
    throw new Error(`${path}: non-enum integer must declare maximum for consistent TypeScript, C#, and Rust precision`)
  }
  if (value.anyOf !== undefined) nullableReference(value)
  for (const [name, property] of Object.entries(value.properties ?? {})) {
    validateDefinition(`${path}.${name}`, property, definitions, 'property')
  }
  if (typeof value.items === 'object') {
    const itemPosition = value.items.type === 'array' ? 'nested-array-item' : 'array-item'
    validateDefinition(`${path}[]`, value.items, definitions, itemPosition)
  }
  if (typeof value.additionalProperties === 'object') {
    validateDefinition(`${path}{}`, value.additionalProperties, definitions, 'map-value')
  }
  if (value.anyOf !== undefined) {
    for (const [index, alternative] of value.anyOf.entries()) {
      validateDefinition(`${path}.anyOf[${index}]`, alternative, definitions, 'union-alternative')
    }
  }
}

function validateEnum(path, value) {
  if (!Array.isArray(value.enum) || value.enum.length === 0) {
    throw new Error(`${path}: enum has no TypeScript, C#, or Rust projection`)
  }
  const enumType = value.type === 'string' ? 'string' : value.type === 'integer' ? 'integer' : null
  if (!enumType || value.enum.some((item) => enumType === 'string' ? typeof item !== 'string' : !Number.isInteger(item))) {
    throw new Error(`${path}: enum has no TypeScript, C#, or Rust projection for its value type`)
  }
  if (new Set(value.enum).size !== value.enum.length) {
    throw new Error(`${path}: enum values must be unique for TypeScript, C#, and Rust projections`)
  }
}

function nonNullType(value) {
  return Array.isArray(value.type) ? value.type.find((item) => item !== 'null') : value.type
}

function validateIdentifiers(candidateManifest, definitions) {
  const typeNames = {
    typescript: new Map([['HarborlineProtocolModels', 'generated protocol model registry']]),
    csharp: new Map([['HarborlineProtocol', 'generated protocol constants']]),
    rust: new Map(),
  }
  for (const [name, definition] of Object.entries(definitions)) {
    for (const target of Object.values(targetLabels)) validateIdentifier(name, name, 'definition', target)
    registerGeneratedName(typeNames.typescript, name, name, 'TypeScript')
    registerGeneratedName(typeNames.csharp, name, name, 'C#')
    registerGeneratedName(typeNames.rust, name, name, 'Rust')
    const propertyNames = Object.keys(definition.properties ?? {})
    validatePropertyIdentifiers(name, propertyNames)
    for (const [propertyName, property] of Object.entries(definition.properties ?? {})) {
      const enumValue = enumValueForProperty(property)
      if (!enumValue) continue
      const enumName = `${name}${pascal(propertyName)}`
      registerGeneratedName(typeNames.csharp, enumName, `${name}.${propertyName}`, 'C#')
      registerGeneratedName(typeNames.rust, enumName, `${name}.${propertyName}`, 'Rust')
      validateEnumMembers(`${name}.${propertyName}`, enumValue)
    }
    if (definition.additionalProperties === true) {
      if (Object.keys(definition.properties ?? {}).some((propertyName) => pascal(propertyName).toLowerCase() === 'additionalproperties')) {
        throw new Error(`${name}: property identifier collides with generated C# member 'AdditionalProperties'`)
      }
      if (Object.keys(definition.properties ?? {}).some((propertyName) => snake(propertyName) === 'additional_properties')) {
        throw new Error(`${name}: property identifier collides with generated Rust field 'additional_properties'`)
      }
    }
  }

  const tsPortNames = new Map()
  const csPortNames = new Map()
  const rustPortNames = new Map()
  const operationIdNames = new Map()
  const hostCommandNames = new Map()
  const applicationRouteNames = new Map()
  for (const port of candidateManifest.ports ?? []) {
    for (const target of Object.values(targetLabels)) validateIdentifier(port.name, `port ${port.name}`, 'port', target)
    registerGeneratedName(tsPortNames, port.name, port.name, 'TypeScript')
    registerGeneratedName(csPortNames, `I${port.name}`, port.name, 'C#')
    registerGeneratedName(rustPortNames, port.name, port.name, 'Rust')
    const methodNames = {
      typescript: new Map(),
      csharp: new Map(),
      rust: new Map(),
    }
    for (const operation of port.operations ?? []) {
      validateIdentifier(operation.methodName, `${port.name}.${operation.methodName}`, 'method', 'TypeScript')
      validateIdentifier(pascal(operation.methodName), `${port.name}.${operation.methodName}`, 'method', 'C#')
      validateIdentifier(snake(operation.methodName), `${port.name}.${operation.methodName}`, 'method', 'Rust')
      registerGeneratedName(methodNames.typescript, operation.methodName, operation.methodName, 'TypeScript')
      registerGeneratedName(methodNames.csharp, pascal(operation.methodName), operation.methodName, 'C#')
      registerGeneratedName(methodNames.rust, snake(operation.methodName), operation.methodName, 'Rust')

      const operationIdKey = constantKey(operation.id)
      registerGeneratedName(operationIdNames, operationIdKey, operation.id, 'TypeScript')
      if (operation.hostCommand) {
        registerGeneratedName(hostCommandNames, operation.methodName, operation.methodName, 'TypeScript')
        registerGeneratedName(hostCommandNames, constantKey(operation.methodName), operation.methodName, 'Rust')
      }
      if (operation.path) {
        registerGeneratedName(applicationRouteNames, operation.methodName, operation.methodName, 'TypeScript')
        registerGeneratedName(applicationRouteNames, constantKey(operation.methodName), operation.methodName, 'Rust')
      }
    }
  }
}

function validatePropertyIdentifiers(owner, propertyNames) {
  for (const propertyName of propertyNames) {
    validateRawIdentifier(propertyName, `${owner}.${propertyName}`, 'property', 'TypeScript')
    validateIdentifier(pascal(propertyName), `${owner}.${propertyName}`, 'property', 'C#')
    validateIdentifier(snake(propertyName), `${owner}.${propertyName}`, 'property', 'Rust')
  }
  registerPropertyCollisions(owner, propertyNames, (name) => name, 'TypeScript')
  registerPropertyCollisions(owner, propertyNames, pascal, 'C#')
  registerPropertyCollisions(owner, propertyNames, snake, 'Rust')
}

function registerPropertyCollisions(owner, propertyNames, projection, target) {
  const seen = new Map()
  for (const propertyName of propertyNames) {
    const generated = projection(propertyName)
    const key = target === 'TypeScript' ? generated : generated.toLowerCase()
    const previous = seen.get(key)
    if (previous) {
      throw new Error(`${owner}: properties '${previous}' and '${propertyName}' collide as ${target} identifier '${generated}'`)
    }
    seen.set(key, propertyName)
  }
}

function validateEnumMembers(path, value) {
  for (const item of value.enum) {
    const member = enumMember(item)
    if (!member) throw new Error(`${path}: enum value ${JSON.stringify(item)} requires escaping in C# and Rust`)
    validateIdentifier(member, `${path} enum value ${JSON.stringify(item)}`, 'enum member', 'C#')
    validateIdentifier(member, `${path} enum value ${JSON.stringify(item)}`, 'enum member', 'Rust')
  }
  registerEnumMemberCollisions(path, value, 'C#')
  registerEnumMemberCollisions(path, value, 'Rust')
}

function enumValueForProperty(property) {
  if (property.enum !== undefined) return property
  if (property.type === 'array' && property.items?.enum !== undefined) return property.items
  return null
}

function registerEnumMemberCollisions(path, value, target) {
  const seen = new Map()
  for (const item of value.enum) {
    const member = enumMember(item)
    const previous = seen.get(member.toLowerCase())
    if (previous !== undefined) {
      throw new Error(`${path}: enum values ${JSON.stringify(previous)} and ${JSON.stringify(item)} collide as ${target} enum member '${member}'`)
    }
    seen.set(member.toLowerCase(), item)
  }
}

function validateRawIdentifier(value, path, role, target) {
  if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(value)) {
    throw new Error(`${path}: ${role} '${value}' requires escaping in ${target}; the emitter does not escape identifiers`)
  }
}

function validateIdentifier(value, path, role, target) {
  validateRawIdentifier(value, path, role, target)
  const targetName = Object.entries(targetLabels).find(([, label]) => label === target)?.[0] ?? target
  if (reservedWords[targetName]?.has(value)) {
    throw new Error(`${path}: ${role} '${value}' is reserved in ${target}`)
  }
}

function registerGeneratedName(seen, generated, source, target) {
  const key = generated.toLowerCase()
  const previous = seen.get(key)
  if (previous) throw new Error(`${source}: generated ${target} identifier '${generated}' collides with '${previous}'`)
  seen.set(key, source)
}

function banner(comment) {
  return `${comment} GENERATED from packages/contracts/protocol. DO NOT EDIT.\n${comment} Protocol ${manifest.protocolId} ${manifest.protocolVersion}.\n\n`
}

function generateOpenApi(candidateManifest) {
  const paths = {}
  for (const port of candidateManifest.ports) {
    for (const operation of port.operations.filter((item) => item.path)) {
      const response = {
        description: operation.responseDescription ?? `Successful ${operation.id} response`,
        content: {
          'application/json': {
            schema: { $ref: `../schemas/carrier-protocol.schema.json#/$defs/${operation.response}` },
          },
        },
      }
      const projection = {
        operationId: operation.id,
        ...(operation.security
          ? { security: operation.security.map((scheme) => ({ [scheme]: [] })) }
          : {}),
        ...(operation.request !== 'EmptyRequest'
          ? {
              requestBody: {
                required: true,
                content: {
                  'application/json': {
                    schema: { $ref: `../schemas/carrier-protocol.schema.json#/$defs/${operation.request}` },
                  },
                },
              },
            }
          : {}),
        responses: { 200: response },
      }
      paths[operation.path] ??= {}
      paths[operation.path][operation.httpMethod.toLowerCase()] = projection
    }
  }
  const document = {
    openapi: '3.1.1',
    info: {
      title: 'Harborline application adapter API',
      version: candidateManifest.protocolVersion,
      description: 'HTTP projection of typed HarborlineApplicationPort operations.',
    },
    paths,
    components: {
      securitySchemes: candidateManifest.securitySchemes ?? {},
    },
  }
  return `${JSON.stringify(document, null, 2)}\n`
}

function generateTypeScript(candidateManifest, definitions) {
  const lines = [banner('//').trimEnd(), '']
  lines.push(`export const HARBORLINE_PROTOCOL_ID = '${candidateManifest.protocolId}' as const`)
  lines.push(`export const HARBORLINE_PROTOCOL_VERSION = '${candidateManifest.protocolVersion}' as const`, '')
  lines.push('export const HARBORLINE_OPERATION_IDS = {')
  for (const port of candidateManifest.ports) {
    for (const op of port.operations) lines.push(`  ${constantKey(op.id)}: '${op.id}',`)
  }
  lines.push('} as const', '')
  lines.push('export const HARBORLINE_HOST_COMMANDS = {')
  for (const port of candidateManifest.ports) {
    for (const op of port.operations.filter((item) => item.hostCommand)) {
      lines.push(`  ${op.methodName}: '${op.hostCommand}',`)
    }
  }
  lines.push('} as const', '')
  lines.push('export const HARBORLINE_APPLICATION_ROUTES = {')
  for (const port of candidateManifest.ports) {
    for (const op of port.operations.filter((item) => item.path)) {
      lines.push(`  ${op.methodName}: '${op.path}',`)
    }
  }
  lines.push('} as const', '')

  for (const [name, definition] of Object.entries(definitions)) {
    lines.push(...tsDefinition(name, definition), '')
  }
  lines.push('export interface HarborlineProtocolModels {')
  for (const name of Object.keys(definitions)) lines.push(`  ${name}: ${name}`)
  lines.push('}', '')
  lines.push('export function parseHarborlineProtocolModel<K extends keyof HarborlineProtocolModels>(')
  lines.push('  model: K,')
  lines.push('  value: unknown,')
  lines.push('): HarborlineProtocolModels[K] {')
  lines.push('  switch (model) {')
  for (const name of Object.keys(definitions)) {
    lines.push(`    case '${name}': assert${name}(value, model); break`)
  }
  lines.push('    default: throw new Error(`unknown Harborline protocol model: ${String(model)}`)')
  lines.push('  }')
  lines.push('  return value as HarborlineProtocolModels[K]')
  lines.push('}', '')
  lines.push('function protocolObject(value: unknown, path: string): Record<string, unknown> {')
  lines.push("  if (typeof value !== 'object' || value === null || Array.isArray(value)) {")
  lines.push('    throw new Error(`${path}: expected object`)')
  lines.push('  }')
  lines.push('  return value as Record<string, unknown>')
  lines.push('}', '')
  lines.push('function hasOwn(object: object, key: string): boolean {')
  lines.push('  return Object.prototype.hasOwnProperty.call(object, key)')
  lines.push('}', '')
  for (const [name, definition] of Object.entries(definitions)) {
    lines.push(...tsValidator(name, definition), '')
  }
  for (const port of candidateManifest.ports) {
    lines.push(`export interface ${port.name} {`)
    for (const op of port.operations) {
      lines.push(`  ${op.methodName}(request: ${op.request}): Promise<${op.response}>`)
    }
    lines.push('}', '')
  }
  return `${lines.join('\n').trimEnd()}\n`
}

function tsValidator(name, definition) {
  const lines = [`function assert${name}(value: unknown, path: string): asserts value is ${name} {`]
  if (definition.type !== 'object') {
    lines.push(...tsValidationLines('value', 'path', definition, '  '), '}')
    return lines
  }
  lines.push('  const object = protocolObject(value, path)')
  const properties = Object.keys(definition.properties ?? {})
  if (definition.additionalProperties === false) {
    lines.push(`  const allowed = new Set<string>(${JSON.stringify(properties)})`)
    lines.push('  for (const key of Object.keys(object)) {')
    lines.push('    if (!allowed.has(key)) throw new Error(`${path}: unexpected property ${key}`)')
    lines.push('  }')
  }
  const required = new Set(definition.required ?? [])
  for (const [propertyName, property] of Object.entries(definition.properties ?? {})) {
    const access = `object[${JSON.stringify(propertyName)}]`
    const childPath = `\`${'${path}'}.${propertyName}\``
    if (required.has(propertyName)) {
      lines.push(`  if (!hasOwn(object, ${JSON.stringify(propertyName)})) throw new Error(\`${'${path}'}: missing required property ${propertyName}\`)`)
      lines.push(...tsValidationLines(access, childPath, property, '  '))
    } else {
      lines.push(`  if (hasOwn(object, ${JSON.stringify(propertyName)})) {`)
      lines.push(...tsValidationLines(access, childPath, property, '    '))
      lines.push('  }')
    }
  }
  lines.push('}')
  return lines
}

function tsValidationLines(expression, pathExpression, value, indent) {
  const nullable = isNullable(value)
  const reference = value.$ref ?? nullableReference(value)
  if (reference) {
    return nullable
      ? [`${indent}if (${expression} !== null) assert${refName(reference)}(${expression}, ${pathExpression})`]
      : [`${indent}assert${refName(reference)}(${expression}, ${pathExpression})`]
  }
  const type = Array.isArray(value.type) ? value.type.find((item) => item !== 'null') : value.type
  const lines = []
  if (nullable) lines.push(`${indent}if (${expression} !== null) {`)
  const bodyIndent = nullable ? `${indent}  ` : indent
  if (value.enum) {
    lines.push(`${bodyIndent}if (!${JSON.stringify(value.enum)}.includes(${expression} as never)) throw new Error(${pathExpression} + ': value is outside enum')`)
  } else if (type === 'array') {
    lines.push(`${bodyIndent}if (!Array.isArray(${expression})) throw new Error(${pathExpression} + ': expected array')`)
    lines.push(`${bodyIndent}for (let index = 0; index < ${expression}.length; index += 1) {`)
    lines.push(...tsValidationLines(`${expression}[index]`, `${pathExpression} + '[' + index + ']'`, value.items, `${bodyIndent}  `))
    lines.push(`${bodyIndent}}`)
  } else if (type === 'object') {
    if (typeof value.additionalProperties === 'object') {
      const mapName = `map${Math.abs(expression.split('').reduce((sum, char) => sum + char.charCodeAt(0), 0))}`
      lines.push(`${bodyIndent}const ${mapName} = protocolObject(${expression}, ${pathExpression})`)
      lines.push(`${bodyIndent}for (const [key, entry] of Object.entries(${mapName})) {`)
      lines.push(...tsValidationLines('entry', `${pathExpression} + '.' + key`, value.additionalProperties, `${bodyIndent}  `))
      lines.push(`${bodyIndent}}`)
    } else {
      lines.push(`${bodyIndent}protocolObject(${expression}, ${pathExpression})`)
    }
  } else {
    const jsType = type === 'integer' || type === 'number' ? 'number' : type
    const numericCheck = type === 'integer'
      ? ` || !Number.isInteger(${expression})`
      : type === 'number'
        ? ` || !Number.isFinite(${expression})`
        : ''
    lines.push(`${bodyIndent}if (typeof ${expression} !== '${jsType}'${numericCheck}) throw new Error(${pathExpression} + ': expected ${type}')`)
  }
  if (value.minimum !== undefined) lines.push(`${bodyIndent}if ((${expression} as number) < ${value.minimum}) throw new Error(${pathExpression} + ': below minimum ${value.minimum}')`)
  if (value.maximum !== undefined) lines.push(`${bodyIndent}if ((${expression} as number) > ${value.maximum}) throw new Error(${pathExpression} + ': above maximum ${value.maximum}')`)
  if (nullable) lines.push(`${indent}}`)
  return lines
}

function tsDefinition(name, definition) {
  if (definition.type !== 'object') return [`export type ${name} = ${tsType(definition)}`]
  if (Object.keys(definition.properties ?? {}).length === 0 && definition.additionalProperties === false) {
    return [`export type ${name} = Record<string, never>`]
  }
  const lines = [`export interface ${name} {`]
  const required = new Set(definition.required ?? [])
  for (const [propertyName, property] of Object.entries(definition.properties ?? {})) {
    lines.push(`  ${propertyName}${required.has(propertyName) ? '' : '?'}: ${tsType(property)}`)
  }
  if (definition.additionalProperties === true) lines.push('  [key: string]: unknown')
  lines.push('}')
  return lines
}

function tsType(value) {
  const nullable = isNullable(value)
  let result
  const reference = value.$ref ?? nullableReference(value)
  if (reference) result = refName(reference)
  else if (value.enum) result = value.enum.map((item) => JSON.stringify(item)).join(' | ')
  else {
    const type = Array.isArray(value.type) ? value.type.find((item) => item !== 'null') : value.type
    if (type === 'string') result = 'string'
    else if (type === 'integer' || type === 'number') result = 'number'
    else if (type === 'boolean') result = 'boolean'
    else if (type === 'array') {
      const itemType = tsType(value.items)
      result = `${tsTypeIsUnion(value.items) ? `(${itemType})` : itemType}[]`
    }
    else if (type === 'object' && typeof value.additionalProperties === 'object') {
      result = `Record<string, ${tsType(value.additionalProperties)}>`
    } else if (type === 'object') result = 'Record<string, unknown>'
    else throw new Error(`unsupported TypeScript type ${JSON.stringify(value)}`)
  }
  return nullable ? `${result} | null` : result
}

function tsTypeIsUnion(value) {
  return value.enum?.length > 1 || isNullable(value)
}

function generateCSharp(candidateManifest, definitions) {
  const lines = [banner('//').trimEnd(), '#nullable enable', '', 'using System.Text.Json;', 'using System.Text.Json.Serialization;', '', `namespace ${csharpNamespace};`, '']
  lines.push('public static class HarborlineProtocol')
  lines.push('{')
  lines.push(`    public const string Id = "${candidateManifest.protocolId}";`)
  lines.push(`    public const string Version = "${candidateManifest.protocolVersion}";`)
  for (const port of candidateManifest.ports) {
    for (const op of port.operations) lines.push(`    public const string ${pascal(constantKey(op.id).toLowerCase())} = "${op.id}";`)
  }
  lines.push('}', '')
  lines.push('public static class HarborlineHostCommands', '{')
  for (const port of candidateManifest.ports) {
    for (const op of port.operations.filter((item) => item.hostCommand)) {
      lines.push(`    public const string ${pascal(op.methodName)} = "${op.hostCommand}";`)
    }
  }
  lines.push('}', '')
  lines.push('public static class HarborlineApplicationRoutes', '{')
  for (const port of candidateManifest.ports) {
    for (const op of port.operations.filter((item) => item.path)) {
      lines.push(`    public const string ${pascal(op.methodName)} = "${op.path}";`)
    }
  }
  lines.push('}', '')

  for (const [name, definition] of Object.entries(definitions)) {
    for (const [propertyName, property] of Object.entries(definition.properties ?? {})) {
      const enumValue = enumValueForProperty(property)
      if (enumValue) lines.push(...csEnum(name, propertyName, enumValue), '')
    }
    lines.push(...csDefinition(name, definition), '')
  }
  for (const port of candidateManifest.ports) {
    lines.push(`public interface I${port.name}`, '{')
    for (const op of port.operations) {
      lines.push(`    Task<${op.response}> ${pascal(op.methodName)}Async(${op.request} request, CancellationToken cancellationToken = default);`)
    }
    lines.push('}', '')
  }
  return `${lines.join('\n').trimEnd()}\n`
}

function csDefinition(name, definition) {
  if (definition.type !== 'object') return [`public sealed record ${name}(${csType(definition)} Value);`]
  const lines = []
  lines.push(`[JsonConverter(typeof(${name}JsonConverter))]`)
  lines.push(`public sealed record ${name}`, '{')
  const required = new Set(definition.required ?? [])
  for (const [propertyName, property] of Object.entries(definition.properties ?? {})) {
    const requiredMember = required.has(propertyName)
    const type = csType(property, !requiredMember, name, propertyName)
    const bounds = !property.enum && (property.minimum !== undefined || property.maximum !== undefined)
    const field = `_${propertyName}`
    if (bounds) lines.push(`    private ${type} ${field};`)
    lines.push(`    [JsonPropertyName("${propertyName}")]`)
    if (!requiredMember) lines.push('    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]')
    if (bounds) {
      lines.push(`    public ${requiredMember ? 'required ' : ''}${type} ${pascal(propertyName)}`)
      lines.push('    {')
      lines.push(`        get => ${field};`)
      lines.push('        init')
      lines.push('        {')
      const checks = []
      if (property.minimum !== undefined) checks.push(`value < ${csNumber(property.minimum, property.type)}`)
      if (property.maximum !== undefined) checks.push(`value > ${csNumber(property.maximum, property.type)}`)
      lines.push(`            if (${checks.join(' || ')}) throw new ArgumentOutOfRangeException(nameof(${pascal(propertyName)}));`)
      lines.push(`            ${field} = value;`)
      lines.push('        }')
      lines.push('    }')
    } else {
      lines.push(`    public ${requiredMember ? 'required ' : ''}${type} ${pascal(propertyName)} { get; init; }`)
    }
  }
  if (definition.additionalProperties === true) {
    lines.push('    [JsonExtensionData]')
    lines.push('    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }')
  }
  lines.push('}')
  lines.push('', ...csWireDefinition(name, definition), '', ...csConverter(name, definition))
  return lines
}

function csWireDefinition(name, definition) {
  const lines = []
  if (definition.additionalProperties === false) {
    lines.push('[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]')
  }
  lines.push(`internal sealed record ${name}Wire`, '{')
  const required = new Set(definition.required ?? [])
  for (const [propertyName, property] of Object.entries(definition.properties ?? {})) {
    const requiredMember = required.has(propertyName)
    const type = csType(property, !requiredMember, name, propertyName)
    lines.push(`    [JsonPropertyName("${propertyName}")]`)
    if (!requiredMember) lines.push('    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]')
    lines.push(`    public ${requiredMember ? 'required ' : ''}${type} ${pascal(propertyName)} { get; init; }`)
  }
  if (definition.additionalProperties === true) {
    lines.push('    [JsonExtensionData]')
    lines.push('    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }')
  }
  lines.push('}')
  return lines
}

function csConverter(name, definition) {
  const properties = Object.entries(definition.properties ?? {})
  const required = new Set(definition.required ?? [])
  const lines = [`internal sealed class ${name}JsonConverter : JsonConverter<${name}>`, '{']
  lines.push(`    public override ${name} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)`)
  lines.push('    {')
  lines.push('        using var document = JsonDocument.ParseValue(ref reader);')
  lines.push('        var value = document.RootElement;')
  lines.push(`        Validate${name}(value);`)
  if (properties.length === 0 && definition.additionalProperties !== true) {
    lines.push(`        _ = JsonSerializer.Deserialize<${name}Wire>(value.GetRawText(), options) ?? throw new JsonException("${name}: expected object");`)
  } else {
    lines.push('        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };')
    lines.push(`        var wire = JsonSerializer.Deserialize<${name}Wire>(value.GetRawText(), wireOptions) ?? throw new JsonException("${name}: expected object");`)
  }
  lines.push(`        return new ${name}`)
  lines.push('        {')
  for (const [propertyName] of properties) {
    lines.push(`            ${pascal(propertyName)} = wire.${pascal(propertyName)},`)
  }
  if (definition.additionalProperties === true) lines.push('            AdditionalProperties = wire.AdditionalProperties,')
  lines.push('        };')
  lines.push('    }')
  lines.push(`    public override void Write(Utf8JsonWriter writer, ${name} value, JsonSerializerOptions options)`)
  lines.push('    {')
  lines.push(`        JsonSerializer.Serialize(writer, new ${name}Wire`)
  lines.push('        {')
  for (const [propertyName] of properties) {
    lines.push(`            ${pascal(propertyName)} = value.${pascal(propertyName)},`)
  }
  if (definition.additionalProperties === true) lines.push('            AdditionalProperties = value.AdditionalProperties,')
  lines.push('        }, options);')
  lines.push('    }')
  lines.push('')
  lines.push(`    private static void Validate${name}(JsonElement value)`)
  lines.push('    {')
  lines.push(`        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("${name}: expected object");`)
  lines.push('        foreach (var property in value.EnumerateObject())')
  lines.push('        {')
  lines.push('            switch (property.Name)')
  lines.push('            {')
  for (const [propertyName, property] of properties) {
    lines.push(`                case ${JSON.stringify(propertyName)}:`)
    if (!isNullable(property)) {
      lines.push(`                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("${name}.${propertyName}: value must not be null");`)
    }
    lines.push('                    break;')
  }
  lines.push('                default:')
  if (definition.additionalProperties !== true) {
    for (const [propertyName] of properties) {
      lines.push(`                    if (string.Equals(property.Name, ${JSON.stringify(propertyName)}, StringComparison.OrdinalIgnoreCase)) throw new JsonException("${name}.${propertyName}: property name must match exact wire casing");`)
    }
    lines.push(`                    throw new JsonException($"${name}: unexpected property {property.Name}");`)
  }
  if (definition.additionalProperties === true) lines.push('                    break;')
  lines.push('            }')
  lines.push('        }')
  for (const [propertyName] of properties) {
    if (required.has(propertyName)) {
      lines.push(`        if (!value.TryGetProperty(${JSON.stringify(propertyName)}, out _)) throw new JsonException("${name}: missing required property ${propertyName}");`)
    }
  }
  lines.push('    }')
  lines.push('}')
  return lines
}

function csEnum(owner, propertyName, value) {
  const name = `${owner}${pascal(propertyName)}`
  const stringEnum = value.enum.every((item) => typeof item === 'string')
  const lines = []
  const integerEnum = value.enum.every((item) => Number.isInteger(item))
  lines.push(`[JsonConverter(typeof(${name}JsonConverter))]`)
  lines.push(`public enum ${name}${integerEnum ? ' : long' : ''}`, '{')
  for (const item of value.enum) {
    lines.push(`    ${enumMember(item)} = ${stringEnum ? value.enum.indexOf(item) : item},`)
  }
  lines.push('}')
  {
    lines.push('', `public sealed class ${name}JsonConverter : JsonConverter<${name}>`, '{')
    if (stringEnum) {
    lines.push(`    public override ${name} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch`, '    {')
    for (const item of value.enum) lines.push(`        ${JSON.stringify(item)} => ${name}.${enumMember(item)},`)
    lines.push(`        _ => throw new JsonException("invalid ${name}"),`, '    };')
    lines.push(`    public override void Write(Utf8JsonWriter writer, ${name} value, JsonSerializerOptions options) => writer.WriteStringValue(value switch`, '    {')
    for (const item of value.enum) lines.push(`        ${name}.${enumMember(item)} => ${JSON.stringify(item)},`)
    lines.push(`        _ => throw new JsonException("invalid ${name}"),`, '    });', '}')
    } else {
      lines.push(`    public override ${name} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetInt64() switch`, '    {')
      for (const item of value.enum) lines.push(`        ${item}L => ${name}.${enumMember(item)},`)
      lines.push(`        _ => throw new JsonException("invalid ${name}"),`, '    };')
      lines.push(`    public override void Write(Utf8JsonWriter writer, ${name} value, JsonSerializerOptions options) => writer.WriteNumberValue(value switch`, '    {')
      for (const item of value.enum) lines.push(`        ${name}.${enumMember(item)} => ${item}L,`)
      lines.push(`        _ => throw new JsonException("invalid ${name}"),`, '    });', '}')
    }
  }
  return lines
}

function csType(value, optional = false, owner = '', propertyName = '') {
  const schemaNullable = isNullable(value)
  let result
  const reference = value.$ref ?? nullableReference(value)
  if (reference) result = refName(reference)
  else if (value.enum) result = `${owner}${pascal(propertyName)}`
  else {
    const type = Array.isArray(value.type) ? value.type.find((item) => item !== 'null') : value.type
    if (type === 'string' || value.enum?.every((item) => typeof item === 'string')) result = 'string'
    else if (type === 'integer') result = 'long'
    else if (type === 'number') result = 'double'
    else if (type === 'boolean') result = 'bool'
    else if (type === 'array') result = `IReadOnlyList<${csType(value.items, false, owner, propertyName)}>`
    else if (type === 'object' && value.additionalProperties === true && propertyName === 'extensions') {
      result = 'IReadOnlyDictionary<string, JsonElement>'
    } else if (type === 'object' && typeof value.additionalProperties === 'object') {
      result = `IReadOnlyDictionary<string, ${csType(value.additionalProperties)}>`
    } else if (type === 'object') result = 'JsonElement'
    else throw new Error(`unsupported C# type ${JSON.stringify(value)}`)
  }
  if ((schemaNullable || optional) && !result.endsWith('?')) result += '?'
  return result
}

function csNumber(value, type) {
  return type === 'number' ? `${value}d` : `${value}L`
}

function generateRust(candidateManifest, definitions) {
  const lines = [banner('//').trimEnd(), 'use serde::{Deserialize, Deserializer, Serialize, Serializer};', 'use serde_json::Value;', 'use std::collections::BTreeMap;', '']
  lines.push(`pub const HARBORLINE_PROTOCOL_ID: &str = "${candidateManifest.protocolId}";`)
  lines.push(`pub const HARBORLINE_PROTOCOL_VERSION: &str = "${candidateManifest.protocolVersion}";`, '')
  lines.push('pub mod operation_ids {')
  for (const port of candidateManifest.ports) {
    for (const op of port.operations) lines.push(`    pub const ${constantKey(op.id)}: &str = "${op.id}";`)
  }
  lines.push('}', '', 'pub mod host_commands {')
  for (const port of candidateManifest.ports) {
    for (const op of port.operations.filter((item) => item.hostCommand)) {
      lines.push(`    pub const ${constantKey(op.methodName)}: &str = "${op.hostCommand}";`)
    }
  }
  lines.push('}', '', 'pub mod application_routes {')
  for (const port of candidateManifest.ports) {
    for (const op of port.operations.filter((item) => item.path)) {
      lines.push(`    pub const ${constantKey(op.methodName)}: &str = "${op.path}";`)
    }
  }
  lines.push('}', '')
  for (const [name, definition] of Object.entries(definitions)) {
    for (const [propertyName, property] of Object.entries(definition.properties ?? {})) {
      const enumValue = enumValueForProperty(property)
      if (enumValue) lines.push(...rustEnum(name, propertyName, enumValue), '')
    }
    lines.push(...rustDefinition(name, definition), '')
  }
  for (const port of candidateManifest.ports) {
    lines.push('#[allow(async_fn_in_trait)]')
    lines.push(`pub trait ${port.name} {`)
    lines.push('    type Error;')
    for (const op of port.operations) {
      lines.push(`    async fn ${snake(op.methodName)}(&self, request: ${op.request}) -> Result<${op.response}, Self::Error>;`)
    }
    lines.push('}', '')
  }
  return `${lines.join('\n').trimEnd()}\n`
}

function rustDefinition(name, definition) {
  const lines = ['#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]']
  if (definition.additionalProperties === false) lines.push('#[serde(deny_unknown_fields)]')
  lines.push(`pub struct ${name} {`)
  const required = new Set(definition.required ?? [])
  for (const [propertyName, property] of Object.entries(definition.properties ?? {})) {
    const field = snake(propertyName)
    const requiredMember = required.has(propertyName)
    const serdeOptions = [`rename = "${propertyName}"`]
    if (!requiredMember) serdeOptions.push('default', 'skip_serializing_if = "Option::is_none"')
    if (needsRustDeserializer(property, requiredMember)) {
      serdeOptions.push(`deserialize_with = "deserialize_${snake(name)}_${field}"`)
    }
    if (needsRustSerializer(property)) {
      serdeOptions.push(`serialize_with = "serialize_${snake(name)}_${field}"`)
    }
    lines.push(`    #[serde(${serdeOptions.join(', ')})]`)
    lines.push(`    pub ${field}: ${rustType(property, !required.has(propertyName), name, propertyName)},`)
  }
  if (definition.additionalProperties === true) {
    lines.push('    #[serde(flatten)]')
    lines.push('    pub additional_properties: BTreeMap<String, Value>,')
  }
  lines.push('}')
  for (const [propertyName, property] of Object.entries(definition.properties ?? {})) {
    if (needsRustDeserializer(property, required.has(propertyName))) {
      lines.push('', ...rustDeserializer(name, propertyName, property, required.has(propertyName)))
    }
    if (needsRustSerializer(property)) {
      lines.push('', ...rustSerializer(name, propertyName, property, required.has(propertyName)))
    }
  }
  return lines
}

function needsRustSerializer(property) {
  return !property.enum && (property.minimum !== undefined || property.maximum !== undefined)
}

function needsRustDeserializer(property, required) {
  return !property.enum && (property.minimum !== undefined
    || property.maximum !== undefined)
    || required && isNullable(property)
    || !required && !isNullable(property)
}

function rustDeserializer(name, propertyName, property, required) {
  const type = rustType(property, !required, name, propertyName)
  const nonNullProperty = nonNullSchema(property)
  const baseType = rustType(nonNullProperty, false, name, propertyName)
  const nullable = isNullable(property)
  const usesOption = !required || nullable
  const functionName = `deserialize_${snake(name)}_${snake(propertyName)}`
  const lines = [`fn ${functionName}<'de, D>(deserializer: D) -> Result<${type}, D::Error>`, 'where', "    D: Deserializer<'de>,", '{']
  if (usesOption) {
    lines.push(`    let value = Option::<${baseType}>::deserialize(deserializer)?;`)
    if (!nullable) lines.push('    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }')
    if (property.minimum !== undefined || property.maximum !== undefined) {
      lines.push('    if let Some(value) = value {')
      lines.push(`        ${rustRangeCheck('value', property, '        ')}`)
      lines.push('    }')
    }
  } else {
    lines.push(`    let value = ${baseType}::deserialize(deserializer)?;`)
    if (property.minimum !== undefined || property.maximum !== undefined) {
      lines.push(`    ${rustRangeCheck('value', property, '    ')}`)
    }
  }
  lines.push('    Ok(value)', '}')
  return lines
}

function rustSerializer(name, propertyName, property, required) {
  const type = rustType(property, !required, name, propertyName)
  const nullable = isNullable(property)
  const usesOption = !required || nullable
  const functionName = `serialize_${snake(name)}_${snake(propertyName)}`
  const lines = [`fn ${functionName}<S>(value: &${type}, serializer: S) -> Result<S::Ok, S::Error>`, 'where', '    S: Serializer,', '{']
  if (usesOption) {
    lines.push('    if let Some(value) = value {')
    lines.push(`        ${rustSerializeRangeCheck('value', property)}`)
    lines.push('    }')
  } else {
    lines.push(`    ${rustSerializeRangeCheck('value', property)}`)
  }
  lines.push('    value.serialize(serializer)', '}')
  return lines
}

function rustSerializeRangeCheck(expression, property) {
  const checks = []
  if (property.minimum !== undefined) checks.push(`*${expression} < ${rustNumeric(property.minimum, property)}`)
  if (property.maximum !== undefined) checks.push(`*${expression} > ${rustNumeric(property.maximum, property)}`)
  return `if ${checks.join(' || ')} { return Err(serde::ser::Error::custom("value outside schema range")); }`
}

function nonNullSchema(value) {
  if (Array.isArray(value.type)) return { ...value, type: value.type.find((item) => item !== 'null') }
  const reference = nullableReference(value)
  if (reference) return { $ref: reference }
  return value
}

function rustEnum(owner, propertyName, value) {
  const name = `${owner}${pascal(propertyName)}`
  const stringEnum = value.enum.every((item) => typeof item === 'string')
  const derives = stringEnum
    ? 'Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize'
    : 'Debug, Clone, Copy, PartialEq, Eq'
  const lines = [`#[derive(${derives})]`]
  if (!stringEnum) lines.push(`#[repr(${value.type === 'integer' ? 'i64' : 'i64'})]`)
  lines.push(`pub enum ${name} {`)
  for (const item of value.enum) {
    if (stringEnum) lines.push(`    #[serde(rename = ${JSON.stringify(item)})]`)
    lines.push(`    ${enumMember(item)}${stringEnum ? '' : ` = ${item}`},`)
  }
  lines.push('}')
  if (!stringEnum) {
    lines.push('', `impl Serialize for ${name} {`)
    lines.push('    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error> where S: serde::Serializer {')
    lines.push('        serializer.serialize_i64(*self as i64)', '    }', '}')
    lines.push('', `impl<'de> Deserialize<'de> for ${name} {`)
    lines.push("    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error> where D: Deserializer<'de> {")
    lines.push('        match i64::deserialize(deserializer)? {')
    for (const item of value.enum) lines.push(`            ${item} => Ok(Self::${enumMember(item)}),`)
    lines.push('            _ => Err(serde::de::Error::custom("value is outside enum")),', '        }', '    }', '}')
  }
  return lines
}

function rustType(value, optional = false, owner = '', propertyName = '') {
  const schemaNullable = isNullable(value)
  let result
  const reference = value.$ref ?? nullableReference(value)
  if (reference) result = refName(reference)
  else if (value.enum) result = `${owner}${pascal(propertyName)}`
  else {
    const type = Array.isArray(value.type) ? value.type.find((item) => item !== 'null') : value.type
    if (type === 'string' || value.enum?.every((item) => typeof item === 'string')) result = 'String'
    else if (type === 'integer') result = 'i64'
    else if (type === 'number') result = 'f64'
    else if (type === 'boolean') result = 'bool'
    else if (type === 'array') result = `Vec<${rustType(value.items, false, owner, propertyName)}>`
    else if (type === 'object' && value.additionalProperties === true && propertyName === 'extensions') {
      result = 'BTreeMap<String, Value>'
    } else if (type === 'object' && typeof value.additionalProperties === 'object') {
      result = `BTreeMap<String, ${rustType(value.additionalProperties)}>`
    } else if (type === 'object') result = 'Value'
    else throw new Error(`unsupported Rust type ${JSON.stringify(value)}`)
  }
  return schemaNullable || optional ? `Option<${result}>` : result
}

function rustRangeCheck(expression, property, indent) {
  const checks = []
  if (property.minimum !== undefined) checks.push(`${expression} < ${rustNumeric(property.minimum, property)}`)
  if (property.maximum !== undefined) checks.push(`${expression} > ${rustNumeric(property.maximum, property)}`)
  return `if ${checks.join(' || ')} { return Err(serde::de::Error::custom("value outside schema range")); }`
}

function rustNumeric(value, property) {
  const type = Array.isArray(property.type)
    ? property.type.find((item) => item !== 'null')
    : property.type
  return type === 'number' ? `${value}.0` : String(value)
}

function isNullable(value) {
  return Array.isArray(value.type) && value.type.includes('null') || nullableReference(value) !== null
}

function nullableReference(value) {
  if (value.anyOf === undefined) return null
  if (!Array.isArray(value.anyOf) || value.anyOf.length !== 2) {
    throw new Error(`unsupported anyOf shape ${JSON.stringify(value)}`)
  }
  const reference = value.anyOf.find((alternative) => alternative && typeof alternative === 'object' && alternative.$ref)
  const nullSchema = value.anyOf.find((alternative) => alternative && typeof alternative === 'object' && alternative.type === 'null')
  if (!reference || !nullSchema || Object.keys(nullSchema).length !== 1) {
    throw new Error(`only reference-or-null anyOf is supported: ${JSON.stringify(value)}`)
  }
  return reference.$ref
}

function refName(ref) {
  return ref.slice(ref.lastIndexOf('/') + 1)
}

function pascal(value) {
  return value.split(/[^A-Za-z0-9]+/).filter(Boolean).map((part) => part[0].toUpperCase() + part.slice(1)).join('')
}

function snake(value) {
  return value.replace(/([a-z0-9])([A-Z])/g, '$1_$2').replace(/[^A-Za-z0-9]+/g, '_').toLowerCase()
}

function constantKey(value) {
  return snake(value).toUpperCase()
}

function enumMember(value) {
  const member = pascal(String(value).replace(/^-/, 'negative-').replace(/\./g, '-point-'))
  return /^\d/.test(member) ? `Value${member}` : member
}

function option(name, fallback) {
  const index = process.argv.indexOf(name)
  return index === -1 ? fallback : process.argv[index + 1] ?? (() => { throw new Error(`${name} requires a value`) })()
}
