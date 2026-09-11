#!/usr/bin/env node
// Migrates a committed quality baseline to the distinct v2 identity. A normal Release
// quality run supplies this data going forward; this bridge preserves the existing
// location identity while adding the MSBuild project identity deterministically.
import {createHash} from 'node:crypto'
import {execFileSync} from 'node:child_process'
import {readFileSync, writeFileSync} from 'node:fs'
import path from 'node:path'
import {distinctBaseline} from './quality-step.mjs'

const root = process.cwd()
const file = process.argv[2] ?? path.join(root, 'eng', 'baselines', 'quality-baseline.json')
const safeRoot = root.replaceAll('\\', '/')
const projectFiles = execFileSync('git', ['-c', `safe.directory=${safeRoot}`, '-C', root, 'ls-files', '*.csproj'], {encoding: 'utf8'}).trim()
  .split(/\r?\n/).filter(Boolean)
const projectFor = source => {
  const candidates = projectFiles.filter(project => source.startsWith(`${path.posix.dirname(project)}/`))
  const longest = Math.max(...candidates.map(project => path.posix.dirname(project).length))
  const matches = candidates.filter(project => path.posix.dirname(project).length === longest)
  if (matches.length !== 1) throw new Error(`cannot map ${source} to exactly one project`)
  return path.basename(matches[0], '.csproj')
}
const projectFingerprint = project => 'sha256:' + createHash('sha256').update(project).digest('hex')
const v2Fingerprint = (location, project) => 'sha256:' + createHash('sha256').update(JSON.stringify([location, project])).digest('hex')
const baseline = JSON.parse(readFileSync(file, 'utf8'))
baseline.findings = baseline.findings.map(finding => {
  const partial = JSON.parse(finding.enginePartial)
  if (typeof partial['harborline/primary-location/v1'] !== 'string') throw new Error(`missing v1 location identity for ${finding.path}`)
  partial['harborline/project/v1'] = projectFingerprint(projectFor(finding.path))
  partial['harborline/primary-location/v2'] = v2Fingerprint(partial['harborline/primary-location/v1'], partial['harborline/project/v1'])
  return {...finding, enginePartial: JSON.stringify(Object.fromEntries(Object.entries(partial).sort(([left], [right]) => left.localeCompare(right))))}
})
writeFileSync(file, JSON.stringify(distinctBaseline(baseline)) + '\n')
