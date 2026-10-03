// Reviewed source exceptions are evidence, not a baseline or an analyzer switch.
import {createHash} from 'node:crypto'
import {existsSync, readFileSync, realpathSync} from 'node:fs'
import path from 'node:path'

const fail = reason => { throw new Error(`reviewed analyzer suppression: ${reason}`) }
const text = value => typeof value === 'string' && value.trim().length > 0
export const scopeDigest = source => createHash('sha256').update(source.replaceAll('\r\n', '\n')).digest('hex')
const localFile = (root, relative) => {
  if (!text(relative) || relative.includes('\\') || path.posix.isAbsolute(relative)
    || relative.split('/').some(part => !part || part === '.' || part === '..') || relative.includes(':')) fail('evidence/source path must be repository-relative')
  const resolved = realpathSync(path.join(root, relative))
  const inside = path.relative(realpathSync(root), resolved)
  if (inside.startsWith('..') || path.isAbsolute(inside)) fail('evidence/source escapes repository')
  return resolved
}
const requireFields = (object, fields, label) => {
  if (!object || fields.some(field => !text(object[field]))) fail(`${label} is missing required justification`)
}
const reference = (root, item) => {
  requireFields(item, ['path', 'anchor'], 'evidence reference')
  if (!readFileSync(localFile(root, item.path), 'utf8').includes(item.anchor)) fail('evidence anchor does not exist')
}
// These two deliberately finite forms cover reviewed local initializers and
// handlers. More C# forms need their own reviewed parser support, not a wildcard.
const tokens = body => body.replace(/@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|\/\*[\s\S]*?\*\/|\/\/[^\n]*/g, ' ')
const oneCatch = body => {
  const prefix = /^catch\s*\([^)]*\)\s*\{/.exec(body)
  if (!prefix) return false
  let depth = 1
  for (let index = prefix[0].length; index < body.length; index++) {
    if (body[index] === '{') depth++
    if (body[index] === '}' && --depth === 0) return body.slice(index + 1).trim() === ''
  }
  return false
}

export function readReviewedSuppressions(root) {
  const manifest = path.join(root, 'eng/reviewed-analyzer-suppressions.json')
  if (!existsSync(manifest)) return []
  const policy = JSON.parse(readFileSync(manifest, 'utf8'))
  if (policy.schemaVersion !== 1 || !Array.isArray(policy.exceptions)) fail('unsupported manifest schema')
  const identifiers = new Set()
  return policy.exceptions.map(entry => {
    requireFields(entry, ['id', 'rule', 'path', 'scopeSha256'], 'exception')
    if (!/^[a-z][a-z0-9-]+$/.test(entry.id) || identifiers.has(entry.id)) fail('invalid or duplicate exception id')
    identifiers.add(entry.id)
    if (!/^CA\d{4}$/.test(entry.rule) || !/^[a-f0-9]{64}$/.test(entry.scopeSha256)) fail('exact analyzer rule and scope digest are required')
    if (!['statement', 'catch'].includes(entry.scopeKind)) fail('unsupported source scope')
    requireFields(entry.justification, ['protectedBehavior', 'whyComplianceChangesBehavior', 'protectedRisk', 'applicability', 'remainingControls', 'repeatedExceptionReview'], 'exception')
    if (!Array.isArray(entry.alternatives) || !entry.alternatives.length) fail('alternatives must distinguish execution from analysis')
    for (const alternative of entry.alternatives) {
      if (!['analysis', 'executed'].includes(alternative.kind)) fail('unsupported alternative evidence kind')
      requireFields(alternative, ['option', 'reason'], 'alternative')
      if (!Array.isArray(alternative.evidence) || !alternative.evidence.length) fail('alternative evidence is required')
      alternative.evidence.forEach(item => reference(root, item))
      if (alternative.kind === 'executed') {
        requireFields(alternative.execution, ['command', 'sourceHead', 'observedResult'], 'executed alternative')
        if (!/^[a-f0-9]{40}$/.test(alternative.execution.sourceHead)
          || !['failed', 'changed-behavior', 'expected-rejection'].includes(alternative.execution.outcome)) fail('executed alternative needs exact source and observed outcome')
        reference(root, alternative.execution.report)
      }
    }
    if (!Array.isArray(entry.tests) || !entry.tests.length) fail('regression test references are required')
    entry.tests.forEach(item => reference(root, item))
    if (entry.review?.status !== 'reviewed' || !text(entry.review?.reviewedBy)) fail('exception review is pending or missing')
    reference(root, entry.review)
    const lines = readFileSync(localFile(root, entry.path), 'utf8').replaceAll('\r\n', '\n').split('\n')
    const disable = `#pragma warning disable ${entry.rule} // reviewed-suppression: ${entry.id}`
    const restore = `#pragma warning restore ${entry.rule}`
    const starts = lines.flatMap((line, index) => line.trim() === disable ? [index] : [])
    if (starts.length !== 1) fail('reviewed directive must identify one exact rule and source scope')
    const start = starts[0]
    const end = lines.findIndex((line, index) => index > start && line.trim() === restore)
    if (end < 0 || end - start > 32) fail('missing restore or broad source scope')
    const body = lines.slice(start + 1, end).join('\n').trim()
    const code = tokens(body).trim()
    if (/^\s*#pragma/m.test(code) || /\b(?:namespace|class|struct|interface|record)\s+\w/.test(code)) fail('nested or declaration-wide suppression is unsupported')
    if (entry.scopeKind === 'statement' && (!/^var\s+\w+\s*=/.test(code) || !code.endsWith(';') || (code.match(/;/g) ?? []).length !== 1 || /[{}]/.test(code))) fail('statement scope must contain one local initializer')
    if (entry.scopeKind === 'catch' && !oneCatch(code)) fail('catch scope must contain one handler')
    if (scopeDigest(lines.slice(start, end + 1).join('\n')) !== entry.scopeSha256) fail('reviewed source scope changed')
    return {...entry, startLine: start + 2, endLine: end}
  })
}

export function applyReviewedSuppressions(result, entries) {
  const physical = result.locations?.[0]?.physicalLocation
  const endLine = physical?.region?.endLine ?? physical?.region?.startLine
  const entry = entries.find(item => item.rule === result.ruleId && item.path === physical?.artifactLocation?.uri
    && physical.region.startLine >= item.startLine && Number.isInteger(endLine)
    && endLine >= physical.region.startLine && endLine <= item.endLine)
  const suppressions = result.suppressions ?? []
  if (!Array.isArray(suppressions)) fail('malformed SARIF suppressions')
  if (suppressions.some(item => item.status === 'accepted') && !entry) fail('accepted suppression has no reviewed source evidence')
  if (!entry) return
  const inSource = suppressions.filter(item => item.kind === 'inSource')
  if (inSource.length !== 1 || suppressions.some(item => item.kind !== 'inSource')) fail('reviewed exception requires one compiler source suppression')
  result.suppressions = [{...inSource[0], status: 'accepted', justification: `${entry.id}: ${entry.justification.protectedBehavior}`}]
  result.properties = {...result.properties, 'harborline/reviewed-suppression': entry.id}
}
