import {createHash} from 'node:crypto'
import {copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync} from 'node:fs'
import path from 'node:path'

export const coverageEnabled = (env = process.env) => env.HARBORLINE_GATE_COVERAGE === '1'

export const qualityCoveragePaths = (root) => ({
  host: path.join(root, 'artifacts', 'quality', 'host.cobertura.xml'),
  contracts: path.join(root, 'artifacts', 'quality', 'contracts.cobertura.xml'),
})

const attribute = (attributes, name) => {
  const match = new RegExp(`\\b${name}\\s*=\\s*(["'])(.*?)\\1`).exec(attributes)
  return match?.[2]
}

export function addCoverageLabel(file, label) {
  const xml = readFileSync(file, 'utf8')
  const root = /<coverage\b([^>]*)>/.exec(xml)
  if (!root) throw new Error(`Cobertura report has no coverage root: ${file}`)
  const attributes = root[1].replace(/\s+label\s*=\s*(["']).*?\1/g, '')
  const labeled = `<coverage${attributes} label="${label}">`
  writeFileSync(file, xml.replace(root[0], labeled))
}

export function setCoverageSourceRoot(file, sourceRoot) {
  const xml = readFileSync(file, 'utf8')
  if (!/<sources>/.test(xml)) throw new Error(`Cobertura report has no sources: ${file}`)
  const relativeRoot = sourceRoot.replaceAll('\\', '/').replace(/^\.\//, '').replace(/\/+$/, '') || '.'
  writeFileSync(file, xml.replace(/<source>[^<]*<\/source>/g, `<source>${relativeRoot}</source>`))
}

export function postProcessCoverage(file, {label, sourceRoot}) {
  setCoverageSourceRoot(file, sourceRoot)
  addCoverageLabel(file, label)
}

export function compactCobertura(file) {
  const xml = readFileSync(file, 'utf8')
  writeFileSync(file, xml
    .replace(/<!DOCTYPE[\s\S]*?>/gi, '')
    .replace(/&(?:amp|lt|gt|quot|apos);/g, '')
    .replace(/\bbranch="(True|False)"/g, (_, value) => `branch="${value.toLowerCase()}"`)
    .replace(/<methods>[\s\S]*?<\/methods>/g, ''))
}

export function coverageSummary(xml) {
  const lines = new Map()
  for (const match of xml.matchAll(/<class\b([^>]*)>([\s\S]*?)<\/class>/g)) {
    const filename = attribute(match[1], 'filename')
    if (!filename) throw new Error('Cobertura class has no filename')
    for (const line of match[2].matchAll(/<line\b([^>]*)\/?\s*>/g)) {
      const number = attribute(line[1], 'number'), hits = attribute(line[1], 'hits')
      if (!/^\d+$/.test(number ?? '') || !/^\d+$/.test(hits ?? '')) throw new Error(`invalid Cobertura line in ${filename}`)
      const key = `${filename}:${number}`
      lines.set(key, {filename, hits: Math.max(Number(hits), lines.get(key)?.hits ?? 0)})
    }
  }
  const rows = [...lines.values()]
  return {
    coveredLines: rows.filter(line => line.hits > 0).length,
    validLines: rows.length,
    paths: [...new Set(rows.map(line => line.filename))].sort(),
  }
}

export function coverageSummaryFromFile(file) {
  return coverageSummary(readFileSync(file, 'utf8'))
}

const walk = directory => readdirSync(directory, {withFileTypes: true}).flatMap(entry => {
  const file = path.join(directory, entry.name)
  return entry.isDirectory() ? walk(file) : entry.isFile() ? [file] : []
})

export function copyCoberturaReport({resultsDirectory, target, label, sourceRoot}) {
  const reports = walk(resultsDirectory)
    .filter(file => /^(coverage\.cobertura|cobertura-coverage)\.xml$/i.test(path.basename(file)))
    .filter(file => !path.relative(resultsDirectory, file).split(path.sep).some(part => part.toLowerCase() === 'in'))
    .sort((left, right) => left.localeCompare(right))
  const direct = reports.filter(file => path.relative(resultsDirectory, file).split(path.sep).length === 2)
  const candidates = direct.length ? direct : reports
  if (!candidates.length) throw new Error(`expected a Cobertura report under ${resultsDirectory}, found 0`)
  const digests = new Set(candidates.map(file => createHash('sha256').update(readFileSync(file)).digest('hex')))
  if (digests.size !== 1) throw new Error(`different Cobertura reports under ${resultsDirectory}`)
  mkdirSync(path.dirname(target), {recursive: true})
  copyFileSync(candidates[0], target)
  compactCobertura(target)
  postProcessCoverage(target, {label, sourceRoot})
  return target
}

export function receiptCoverage(root, env = process.env) {
  if (!coverageEnabled(env)) return 'none'
  const paths = qualityCoveragePaths(root)
  const entry = (file, relative) => {
    if (!existsSync(file)) throw new Error(`coverage artifact is absent: ${relative}`)
    const {coveredLines, validLines} = coverageSummaryFromFile(file)
    return {path: relative, coveredLines, validLines}
  }
  return {
    host: entry(paths.host, 'artifacts/quality/host.cobertura.xml'),
    contracts: entry(paths.contracts, 'artifacts/quality/contracts.cobertura.xml'),
  }
}
