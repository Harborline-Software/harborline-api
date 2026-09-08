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
  writeFileSync(file, xml.replace(/<source>[^<]*<\/source>/g, `<source>${sourceRoot}</source>`))
}

export function postProcessCoverage(file, {label, sourceRoot}) {
  setCoverageSourceRoot(file, sourceRoot)
  addCoverageLabel(file, label)
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
  const reports = walk(resultsDirectory).filter(file => /\.cobertura\.xml$/i.test(file))
  if (reports.length !== 1) throw new Error(`expected one Cobertura report under ${resultsDirectory}, found ${reports.length}`)
  mkdirSync(path.dirname(target), {recursive: true})
  copyFileSync(reports[0], target)
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
