#!/usr/bin/env node
import { spawnSync } from 'node:child_process'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { resolve } from 'node:path'

const root = resolve(import.meta.dirname, '..')
const output = resolve(root, '.stryker')
const enforce = process.argv.includes('--enforce')
const started = Date.now()
const result = spawnSync('dotnet', [
  'tool', 'run', 'dotnet-stryker', '--',
  '--config-file', '../../stryker-config.json',
  '--since:origin/main', '--break-at', '80', '--output', '../../.stryker',
], { cwd: resolve(root, 'apps/local-node-host'), encoding: 'utf8' })

process.stdout.write(result.stdout ?? '')
process.stderr.write(result.stderr ?? '')

const reports = walk(output).filter(file => file.endsWith('mutation-report.md'))
  .filter(file => statSync(file).mtimeMs >= started - 2000)
  .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)
const table = reports.length ? survivingTable(readFileSync(reports[0], 'utf8')) : null

if (table?.zero) console.log('No mutants found for changes since origin/main.')
else if (table) console.log(table.markdown)
else console.log('| File | Surviving mutants |\n| --- | ---: |\n| report unavailable | — |')

if (!enforce) console.warn(`WARN mutation-diff is advisory${result.status ? ` (Stryker exited ${result.status})` : ''}.`)
if (enforce && !table?.zero && result.status) process.exit(result.status ?? 1)

function walk(directory) {
  try {
    return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
      const path = resolve(directory, entry.name)
      return entry.isDirectory() ? walk(path) : path
    })
  } catch { return [] }
}

export function survivingTable(markdown) {
  const lines = markdown.split(/\r?\n/)
  const header = lines.findIndex(line => /^File\s*\|.*Survived/.test(line))
  if (header < 0) return null
  const columns = lines[header].split('|').map(value => value.trim()).filter(Boolean)
  const survived = columns.indexOf('Survived')
  const total = columns.indexOf('Total Mutants')
  const rows = lines.slice(header + 2).filter(line => line.includes('|')).map(line =>
    line.split('|').map(value => value.trim()).filter(Boolean))
  if (!rows.length || rows.every(row => Number(row[total]) === 0)) return { zero: true }
  return {
    zero: false,
    markdown: ['| File | Surviving mutants |', '| --- | ---: |',
      ...rows.map(row => `| ${row[0]} | ${row[survived]} |`)].join('\n'),
  }
}

// ponytail: not wired into eng/verify.sh yet. Stryker 4.16 (Buildalyzer) reports the host test
// project's design-time build as Succeeded:false while a manual design-time msbuild succeeds and
// every other project analyses fine; the host project is then never mutated. Ticket 236 row 5
// tracks the analysis fix (sqlite-vec.targets import is the first suspect). Run by hand until then.
