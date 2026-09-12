#!/usr/bin/env node
// Projects the tier-boundary ArchTests' TRX failures into the single SARIF shape
// consumed by the quality gate.  The tests already own edge discovery and rule
// semantics; this adapter deliberately only carries their evidence across.
import {readFileSync, writeFileSync} from 'node:fs'
import {fileURLToPath} from 'node:url'
import path from 'node:path'
import {normalizeSarifFile} from './normalize-roslyn-sarif.mjs'

const slash = value => value.replaceAll('\\', '/')
const attribute = (text, name) => new RegExp(`${name}="([^"]*)"`).exec(text)?.[1]
const decode = value => value.replaceAll('&quot;', '"').replaceAll('&apos;', "'")
  .replaceAll('&lt;', '<').replaceAll('&gt;', '>').replaceAll('&amp;', '&')

const edgeFrom = message => {
  const match = /(?:^|\n)\s*([^\s]+\.csproj) -> ([^\s]+\.csproj) \(([^\s]+) -> ([^\s]+)\)/m.exec(message)
  return match && {source: match[1], target: match[2], sourceTier: match[3], targetTier: match[4]}
}

const referenceLine = (repoRoot, edge) => {
  if (!edge) return {uri: path.join(repoRoot, 'apps/local-node-host/tests/ArchTests/ApiTierDependencyArchTests.cs'), line: 1}
  const source = path.join(repoRoot, edge.source)
  try {
    const lines = readFileSync(source, 'utf8').split(/\r?\n/)
    const target = path.resolve(repoRoot, edge.target)
    const line = lines.findIndex(value => {
      const include = /<ProjectReference\b[^>]*\bInclude="([^"]+)"/.exec(value)?.[1]
      return include && path.resolve(path.dirname(source), include) === target
    })
    return {uri: source, line: line < 0 ? 1 : line + 1}
  } catch {
    return {uri: source, line: 1}
  }
}

// xUnit theories nest child results in <InnerResults>; a child's </UnitTestResult> would end the parent's
// match before the parent's own <Output>. Peel the innermost nests first so every remaining element is flat.
const withoutInnerResults = trx => {
  let text = trx, previous
  do { previous = text; text = text.replace(/<InnerResults>(?:(?!<InnerResults>)[\s\S])*?<\/InnerResults>/g, '') } while (text !== previous)
  return text
}

export const failuresFromTrx = rawTrx => {
  const trx = withoutInnerResults(rawTrx)
  const failures = []
  // Passed results are self-closing (<UnitTestResult ... />); a pattern that only knows the open/close
  // form lets a passed tag swallow the failed element that follows it, and the TRX order is not stable.
  for (const match of trx.matchAll(/<UnitTestResult\b([^>]*?)(?:\/>|>([\s\S]*?)<\/UnitTestResult>)/g)) {
    const testName = decode(attribute(match[1], 'testName') ?? '')
    // TRX testName is the display name ("R2: foundation references no blocks projects"), not the class.
    if (attribute(match[1], 'outcome') !== 'Failed' || !/^R\d+: /.test(testName)) continue
    const body = match[2] ?? ''
    const cdata = /<Message><!\[CDATA\[([\s\S]*?)\]\]><\/Message>/.exec(body)?.[1]
    const plain = /<Message>([\s\S]*?)<\/Message>/.exec(body)?.[1]
    failures.push({testName, message: decode(cdata ?? plain ?? '')})
  }
  return failures
}

export const sarifFromFailures = (failures, repoRoot) => ({
  version: '2.1.0',
  runs: [{
    tool: {driver: {name: 'arch', rules: [{id: 'HLQ.ARCH.1000', name: 'forbidden-dependency-edge'}]}},
    invocations: [{executionSuccessful: true}],
    results: failures.map(failure => {
      const edge = edgeFrom(failure.message)
      const location = referenceLine(repoRoot, edge)
      return {
        ruleId: 'HLQ.ARCH.1000', level: 'error',
        message: {text: `${failure.testName}: ${failure.message.trim()}`},
        locations: [{physicalLocation: {artifactLocation: {uri: slash(location.uri)}, region: {startLine: location.line}}}],
      }
    }),
  }],
})

export const writeArchSarif = (trxFile, outputFile, repoRoot) => {
  writeFileSync(outputFile, JSON.stringify(sarifFromFailures(failuresFromTrx(readFileSync(trxFile, 'utf8')), repoRoot)) + '\n')
  normalizeSarifFile(outputFile, repoRoot, {engine: 'arch', project: 'api-tier-dependency'})
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [flag, repoRoot, trxFile, outputFile] = process.argv.slice(2)
  if (flag !== '--repo-root' || !repoRoot || !trxFile || !outputFile || process.argv.length !== 6) {
    throw new Error('usage: arch-sarif.mjs --repo-root <path> <trx-file> <output-file>')
  }
  writeArchSarif(trxFile, outputFile, repoRoot)
}
