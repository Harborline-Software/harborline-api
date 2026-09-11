#!/usr/bin/env node
// Runs the pinned Harborline Quality checkout and persists its evidence beside the verification receipt.
import {execFileSync} from 'node:child_process'
import {existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {fileURLToPath} from 'node:url'

const cliArguments = process.argv.slice(2)
const optionValue = option => {
  const index = cliArguments.indexOf(option)
  return index === -1 ? undefined : cliArguments[index + 1]
}
const root = path.resolve(optionValue('--root') ?? process.cwd())
const baselineOutput = optionValue('--write-baseline')
const git = (...args) => execFileSync('git', ['-C', root, ...args], {encoding: 'utf8'}).trim()
const refuse = (variable, reason) => { console.error(`quality: ${variable} ${reason}; refusing to skip the quality gate.`); process.exit(1) }

export function readQualityPin(file = path.join(root, 'eng', 'quality-pin.json')) {
  const pin = JSON.parse(readFileSync(file, 'utf8'))
  if (pin.schemaVersion !== 1 || typeof pin.repository !== 'string' || !/^[a-f0-9]{40}$/.test(pin.commit)) {
    throw new Error('quality pin must name a repository and a 40-hex commit')
  }
  return pin
}

export function resolveQualityCheckout({apiRoot = root, pin = readQualityPin(), env = process.env} = {}) {
  const common = execFileSync('git', ['-C', apiRoot, 'rev-parse', '--path-format=absolute', '--git-common-dir'], {encoding: 'utf8'}).trim()
  const mainRoot = path.dirname(common)
  const candidate = path.resolve(apiRoot, env.HARBORLINE_QUALITY_REPO ?? path.join(mainRoot, '..', 'harborline-quality'))
  if (!existsSync(candidate)) return {reason: `does not name an existing checkout (${candidate})`}
  try {
    const checkoutGit = (...args) => execFileSync('git', ['-C', candidate, ...args], {encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']}).trim()
    if (checkoutGit('rev-parse', 'HEAD') !== pin.commit) return {reason: `does not match pinned commit ${pin.commit}`}
    if (checkoutGit('status', '--porcelain', '--untracked-files=normal')) return {reason: 'must name a clean pinned checkout'}
    if (!existsSync(path.join(candidate, 'bin', 'cqg.mjs'))) return {reason: 'does not contain bin/cqg.mjs'}
  } catch {
    return {reason: `does not name a usable pinned checkout (${candidate})`}
  }
  return {quality: candidate}
}

export function resolveControlPolicy({apiRoot = root, env = process.env} = {}) {
  const common = execFileSync('git', ['-C', apiRoot, 'rev-parse', '--path-format=absolute', '--git-common-dir'], {encoding: 'utf8'}).trim()
  const candidate = path.resolve(apiRoot, env.HARBORLINE_CONTROL_REPO ?? path.join(path.dirname(common), '..', 'harborline-control'))
  const policyDefaults = path.join(candidate, 'policy', 'quality-defaults.yaml')
  if (!existsSync(policyDefaults)) return {reason: `does not contain the expected file (${policyDefaults})`}
  return {policyDefaults}
}

const walk = directory => existsSync(directory) ? readdirSync(directory, {withFileTypes: true}).flatMap(entry => {
  const file = path.join(directory, entry.name)
  return entry.isDirectory() ? walk(file) : entry.isFile() ? [file] : []
}) : []

export function qualityArtifacts(apiRoot = root) {
  const files = walk(path.join(apiRoot, 'artifacts', 'quality')).sort()
  return {
    sarif: files.filter(file => /\.sarif(?:\.json)?$/i.test(file)),
    // Only the merged reports eng/coverage.mjs writes at the top level; the raw per-run coverlet files under
    // coverage/ are the same data unmerged (30 MB each on the Windows landing, over cqg's 16 MB input cap).
    cobertura: files.filter(file => /cobertura.*\.xml$/i.test(path.basename(file)) && path.dirname(file) === path.join(apiRoot, 'artifacts', 'quality')),
  }
}

export function receiptDirectory(apiRoot = root) {
  const common = execFileSync('git', ['-C', apiRoot, 'rev-parse', '--git-common-dir'], {encoding: 'utf8'}).trim()
  return path.resolve(apiRoot, common)
}

export function runQualityStep({apiRoot = root, env = process.env} = {}) {
  const pin = readQualityPin(path.join(apiRoot, 'eng', 'quality-pin.json'))
  const checkout = resolveQualityCheckout({apiRoot, pin, env})
  if (!checkout.quality) refuse('HARBORLINE_QUALITY_REPO', checkout.reason)
  const control = resolveControlPolicy({apiRoot, env})
  if (!control.policyDefaults) refuse('HARBORLINE_CONTROL_REPO', control.reason)
  const artifacts = qualityArtifacts(apiRoot)
  const scratch = mkdtempSync(path.join(tmpdir(), 'harborline-api-quality-'))
  const receipt = receiptDirectory(apiRoot)
  const diff = path.join(scratch, 'base-to-head.diff')
  const decision = path.join(receipt, 'harborline-api-quality-decision.json')
  try {
    const base = git('merge-base', 'origin/main', 'HEAD')
    writeFileSync(diff, execFileSync('git', ['-C', apiRoot, 'diff', `${base}..HEAD`], {encoding: 'utf8'}))
    const args = ['bin/cqg.mjs', 'analyze', ...artifacts.sarif.flatMap(file => ['--sarif', file]), ...artifacts.cobertura.flatMap(file => ['--cobertura', file]),
      '--diff', diff, '--baseline', path.join(apiRoot, 'eng', 'baselines', 'quality-baseline.json'),
      '--policy-defaults', control.policyDefaults,
      '--policy', path.join(apiRoot, 'eng', 'quality-policy.yaml'), '--repo-root', apiRoot, '--out', decision]
    if (baselineOutput) args.push('--write-baseline', path.resolve(apiRoot, baselineOutput))
    execFileSync(process.execPath, args, {cwd: checkout.quality, env, stdio: 'inherit'})
    const record = JSON.parse(readFileSync(decision, 'utf8'))
    if (!/^sha256:[a-f0-9]{64}$/.test(record.decisionId) || !/^sha256:[a-f0-9]{64}$/.test(record.policyDigest)) throw new Error('quality decision is missing its decision or policy digest')
    if (baselineOutput) {
      const candidate = path.resolve(apiRoot, baselineOutput)
      const failedEngines = new Map((record.reasons ?? [])
        .filter(reason => reason?.kind === 'analyzer-error')
        .map(reason => [typeof reason.engine === 'string' ? reason.engine : 'unknown', typeof reason.message === 'string' ? reason.message : 'analyzer-error']))
      const requiredEngines = record.resolvedPolicy?.analysis?.requireSuccessfulEngines ?? []
      const engines = [...new Set([...requiredEngines, ...failedEngines.keys()])].sort().map(engine => ({
        engine,
        status: failedEngines.has(engine) ? 'analyzer-error' : 'ok',
        detail: failedEngines.get(engine) ?? '',
      }))
      const baseline = JSON.parse(readFileSync(candidate, 'utf8'))
      baseline.engines = engines
      writeFileSync(candidate, JSON.stringify(baseline) + '\n')
    }
    console.log(`quality: recorded ${record.decisionId} with policy ${record.policyDigest}`)
    return record
  } finally {
    rmSync(scratch, {recursive: true, force: true})
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { runQualityStep() } catch (error) { console.error(`quality: ${error.message}`); process.exitCode = 1 }
}
