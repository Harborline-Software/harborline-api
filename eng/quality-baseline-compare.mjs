#!/usr/bin/env node
// Compares quality finding sets without treating a line-only move as a new diagnostic.
import {existsSync, readFileSync} from 'node:fs'
import path from 'node:path'
import {fileURLToPath} from 'node:url'

const emptySnippet = 'sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'
const document = file => JSON.parse(readFileSync(file, 'utf8')).findings ?? []
const partial = finding => { try { return JSON.parse(finding.enginePartial ?? '{}') } catch { return {} } }
const project = finding => finding.project ?? partial(finding)['harborline/project/v1'] ?? ''
const line = finding => finding.line ?? finding.startLine ?? finding.primaryLocation?.startLine ?? null
const anchor = finding => {
  if (typeof finding.snippetHash === 'string' && finding.snippetHash && finding.snippetHash !== emptySnippet) return `snippet:${finding.snippetHash}`
  if (typeof finding.symbol === 'string' && finding.symbol) return `symbol:${finding.symbol}`
  return null
}
const identity = finding => [finding.ruleId, finding.path, project(finding)].join('\u0000')

// Maps an unchanged base line through unified-diff hunk offsets. A finding inside a replacement
// hunk has no safe identity; only lines before/after a hunk acquire its precise offset.
export const parseMovedLines = diff => {
  const files = new Map(); let current = null
  for (const row of diff.split(/\r?\n/)) {
    if (row.startsWith('+++ ')) {
      const raw = row.slice(4).split('\t')[0]
      current = raw === '/dev/null' ? null : raw.replace(/^b\//, '')
      continue
    }
    const hunk = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/.exec(row)
    if (hunk && current) {
      const rows = files.get(current) ?? []
      rows.push({oldStart: Number(hunk[1]), oldCount: Number(hunk[2] ?? 1), newStart: Number(hunk[3]), newCount: Number(hunk[4] ?? 1)})
      files.set(current, rows)
    }
  }
  return files
}

const movedLine = (hunks, baseLine) => {
  let offset = 0
  for (const hunk of hunks ?? []) {
    if (baseLine < hunk.oldStart) return baseLine + offset
    if (baseLine < hunk.oldStart + hunk.oldCount) return null
    offset += hunk.newCount - hunk.oldCount
  }
  return baseLine + offset
}

export const compareFindings = (head, base, diff = '') => {
  const used = new Set(), matches = [], moved = parseMovedLines(diff)
  const remaining = predicate => base.findIndex((row, index) => !used.has(index) && predicate(row))
  for (const finding of head) {
    let index = remaining(row => row.fingerprint === finding.fingerprint), method = 'fingerprint'
    if (index < 0 && anchor(finding)) { const key = `${identity(finding)}\u0000${anchor(finding)}`; index = remaining(row => `${identity(row)}\u0000${anchor(row)}` === key); method = 'anchor' }
    if (index < 0 && line(finding) !== null) {
      index = remaining(row => identity(row) === identity(finding) && line(row) !== null && movedLine(moved.get(finding.path), line(row)) === line(finding))
      method = 'moved-line'
    }
    if (index >= 0) { used.add(index); matches.push({head: finding, base: base[index], method}) }
  }
  const matched = new Set(matches.map(match => match.head))
  return {newFindings: head.filter(row => !matched.has(row)), resolved: base.filter((_, index) => !used.has(index)), matches}
}

export const loadBaseline = (artifact, committed) => existsSync(artifact)
  ? {findings: document(artifact), source: 'merge-base artifact'}
  : {findings: document(committed), source: 'committed fallback'}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [candidate, baseline, diff = ''] = process.argv.slice(2)
  if (!candidate || !baseline) throw new Error('usage: quality-baseline-compare.mjs <candidate> <baseline> [diff]')
  const result = compareFindings(document(candidate), document(baseline), diff && existsSync(diff) ? readFileSync(diff, 'utf8') : '')
  console.log(JSON.stringify({new: result.newFindings.length, resolved: result.resolved.length, findings: result.newFindings}))
}
