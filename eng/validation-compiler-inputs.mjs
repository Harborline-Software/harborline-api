// Observe actual Csc command-line output, not an approximation from project XML.
// This does not authenticate candidate-generated files or cover arbitrary MSBuild tasks.
import {readFileSync, existsSync, realpathSync} from 'node:fs'
import path from 'node:path'
import {digest} from './validation-reuse.mjs'

const fileSwitches = new Set(['reference', 'r', 'analyzer', 'additionalfile', 'analyzerconfig',
  'embed', 'resource', 'linkresource', 'keyfile', 'ruleset', 'sourcelink', 'win32res', 'win32icon',
  'win32manifest', 'appconfig', 'addmodule'])
const valueSwitches = new Set(['noconfig', 'nostdlib', 'nologo', 'target', 't', 'out', 'refout', 'doc', 'pdb',
  'debug', 'optimize', 'o', 'deterministic', 'publicsign', 'delaysign', 'platform', 'langversion', 'nullable',
  'define', 'd', 'warn', 'w', 'nowarn', 'warnaserror', 'warnaserror-', 'warnaserror+', 'unsafe', 'checked',
  'fullpaths', 'utf8output', 'highentropyva', 'subsystemversion', 'filealign', 'errorendlocation',
  'errorreport', 'errorlog', 'reportanalyzer', 'generatedfilesout', 'pathmap', 'checksumalgorithm',
  'features', 'instrument', 'preferreduilang', 'moduleassemblyname', 'main', 'runtimemetadataversion'])
const unquote = value => value.startsWith('"') && value.endsWith('"') ? value.slice(1, -1) : value

export function readCompilerObservation({argsFile, roots, expectedCaptureSession, capturePhase = false}) {
  const problems = []
  const contextEntries = readFileSync(argsFile.replace(/\.args$/, '.context'), 'utf8')
    .replace(/^\uFEFF/, '').split(/\r?\n/).filter(Boolean).map(line => {
      const index = line.indexOf('=')
      if (index < 1) throw new Error('invalid compiler context')
      return [line.slice(0, index), line.slice(index + 1)]
    })
  const context = Object.fromEntries(contextEntries)
  const requiredContext = ['project', 'framework', 'configuration', 'skipCompilerExecution', 'designTimeBuild']
  if (contextEntries.length !== requiredContext.length || new Set(contextEntries.map(([key]) => key)).size !== contextEntries.length
    || requiredContext.some(key => !Object.hasOwn(context, key))) problems.push('missing, duplicate or unknown compiler context fields')
  if (!context.project || !context.framework || context.configuration !== 'Release') problems.push('incomplete compiler context')
  if (![undefined, '', 'false'].includes(context.skipCompilerExecution?.toLowerCase())
    || ![undefined, '', 'false'].includes(context.designTimeBuild?.toLowerCase())) problems.push('compiler execution skipped or design-time only')
  const base = path.dirname(context.project ?? argsFile)
  const approvedRoots = roots.map(root => ({...root, directory: path.resolve(root.directory)}))
    .sort((a, b) => b.directory.length - a.directory.length)
  const identify = file => {
    const absolute = path.resolve(base, unquote(file))
    for (const root of approvedRoots) {
      const relative = path.relative(root.directory, absolute)
      if (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative)) {
        if (existsSync(absolute)) {
          const physical = path.relative(realpathSync(root.directory), realpathSync(absolute))
          if (physical.startsWith(`..${path.sep}`) || physical === '..' || path.isAbsolute(physical)) {
            problems.push('compiler input symlink escapes approved root')
            return {absolute: null, identity: '<unapproved>'}
          }
        }
        return {absolute, identity: `${root.name}/${relative.replaceAll('\\', '/')}`}
      }
    }
    problems.push('compiler file outside approved roots')
    return {absolute: null, identity: '<unapproved>'}
  }
  const files = new Map()
  const capture = (file, role) => {
    const {absolute, identity} = identify(file)
    if (!absolute) return
    if (!existsSync(absolute)) { problems.push(`missing ${role} input`); return }
    const sha256 = digest(readFileSync(absolute))
    const key = `${role}:${identity}`
    if (files.has(key) && files.get(key).sha256 !== sha256) problems.push('input changed while observed')
    files.set(key, {role, identity, sha256})
  }
  const argumentsObserved = []
  const seenResponses = new Set()
  const observe = argument => {
    if (!argument || /[\r\n\0]/.test(argument)) {problems.push('invalid compiler argument'); return}
    if (argument.startsWith('@')) {
      const {absolute, identity} = identify(argument.slice(1))
      if (!absolute) return
      if (seenResponses.has(absolute)) {problems.push('recursive or duplicate response file'); return}
      seenResponses.add(absolute)
      capture(argument.slice(1), 'response')
      argumentsObserved.push(`@${identity}`)
      if (existsSync(absolute)) {
        // Full response-file tokenization is not silently approximated.
        problems.push('nested response-file arguments require reviewed tokenization')
      }
      return
    }
    const match = /^[/-]([A-Za-z][A-Za-z0-9]*[+-]?)(?::(.*))?$/.exec(argument)
    if (!match) { capture(argument, 'source'); argumentsObserved.push(identify(argument).identity); return }
    const option = match[1].toLowerCase().replace(/[+-]$/, '')
    if (fileSwitches.has(option)) {
      let file = match[2] ?? ''
      let prefix = '', suffix = ''
      if ((option === 'reference' || option === 'r') && file.includes('=')) {
        prefix = file.slice(0, file.indexOf('=') + 1)
        file = file.slice(file.indexOf('=') + 1)
      }
      if (option === 'resource' || option === 'linkresource') {
        const comma = file.indexOf(',')
        if (comma !== -1) {suffix = file.slice(comma); file = file.slice(0, comma)}
      }
      if (!file || file.includes(';')) {problems.push('ambiguous file argument'); return}
      capture(file, option)
      argumentsObserved.push(`${match[1].toLowerCase()}:${prefix}${identify(file).identity}${suffix}`)
    } else if (valueSwitches.has(option)) {
      // Preserve semantic switches. Outputs and pathmap can differ by checkout; until normalized
      // safely they cause conservative mismatches rather than false equivalence.
      argumentsObserved.push(argument)
    } else { problems.push(`unknown compiler switch ${option}`); argumentsObserved.push(argument) }
  }
  const args = readFileSync(argsFile, 'utf8').replace(/^\uFEFF/, '').split(/\r?\n/).filter(Boolean)
  if (!args.length) problems.push('empty compiler arguments')
  for (const argument of args) observe(argument)
  capture(context.project, 'project')
  if (![...files.values()].some(item => item.role === 'source')) problems.push('no compiler sources observed')
  const observedFiles = [...files.values()].sort((a, b) => `${a.role}:${a.identity}`.localeCompare(`${b.role}:${b.identity}`))
  if (!capturePhase) {
    try {
      const snapshot = JSON.parse(readFileSync(argsFile.replace(/\.args$/, '.snapshot'), 'utf8'))
      if (!/^[0-9a-f-]{36}$/.test(expectedCaptureSession ?? '') || snapshot.captureSession !== expectedCaptureSession
        || snapshot.schemaVersion !== 1 || snapshot.beforeMatches !== true
        || snapshot.argsDigest !== digest(readFileSync(argsFile))
        || snapshot.contextDigest !== digest(readFileSync(argsFile.replace(/\.args$/, '.context')))
        || JSON.stringify(snapshot.files) !== JSON.stringify(observedFiles)) problems.push('compiler execution snapshot mismatch')
    } catch { problems.push('compiler execution snapshot missing or unreadable') }
  }
  return {schemaVersion: 1, project: identify(context.project ?? argsFile).identity,
    framework: context.framework ?? null, configuration: context.configuration ?? null,
    arguments: argumentsObserved, files: observedFiles,
    completeCompilerObservation: problems.length === 0, problems: [...new Set(problems)].sort(),
    trustedProducer: false}
}
