#!/usr/bin/env node
// Runs the pinned Harborline Quality checkout and persists its evidence beside the verification receipt.
import {execFileSync} from 'node:child_process'
import {existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'

const rootArgument = process.argv.slice(2)
const root = path.resolve(rootArgument[0] === '--root' ? rootArgument[1] : process.cwd())
const git = (...args) => execFileSync('git', ['-C', root, ...args], {encoding: 'utf8'}).trim()
const refuse = reason => { console.error(`quality: HARBORLINE_QUALITY_REPO ${reason}; refusing to skip the quality gate.`); process.exit(1) }

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

const walk = directory => existsSync(directory) ? readdirSync(directory, {withFileTypes: true}).flatMap(entry => {
  const file = path.join(directory, entry.name)
  return entry.isDirectory() ? walk(file) : entry.isFile() ? [file] : []
}) : []

export function qualityArtifacts(apiRoot = root) {
  const files = walk(path.join(apiRoot, 'artifacts', 'quality')).sort()
  return {
    sarif: files.filter(file => /\.sarif(?:\.json)?$/i.test(file)),
    cobertura: files.filter(file => /cobertura.*\.xml$/i.test(path.basename(file))),
  }
}

export function receiptDirectory(apiRoot = root) {
  const common = execFileSync('git', ['-C', apiRoot, 'rev-parse', '--git-common-dir'], {encoding: 'utf8'}).trim()
  return path.resolve(apiRoot, common)
}

export function runQualityStep({apiRoot = root, env = process.env} = {}) {
  const pin = readQualityPin(path.join(apiRoot, 'eng', 'quality-pin.json'))
  const checkout = resolveQualityCheckout({apiRoot, pin, env})
  if (!checkout.quality) refuse(checkout.reason)
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
      '--policy-defaults', path.join(env.HARBORLINE_CONTROL_REPO ?? path.join(path.dirname(git('rev-parse', '--path-format=absolute', '--git-common-dir')), '..', 'harborline-control'), 'policy', 'quality-defaults.yaml'),
      '--policy', path.join(apiRoot, 'eng', 'quality-policy.yaml'), '--repo-root', apiRoot, '--out', decision]
    execFileSync(process.execPath, args, {cwd: checkout.quality, env, stdio: 'inherit'})
    const record = JSON.parse(readFileSync(decision, 'utf8'))
    if (!/^sha256:[a-f0-9]{64}$/.test(record.decisionId) || !/^sha256:[a-f0-9]{64}$/.test(record.policyDigest)) throw new Error('quality decision is missing its decision or policy digest')
    console.log(`quality: recorded ${record.decisionId} with policy ${record.policyDigest}`)
  } finally {
    rmSync(scratch, {recursive: true, force: true})
  }
}

if (import.meta.main) {
  try { runQualityStep() } catch (error) { console.error(`quality: ${error.message}`); process.exitCode = 1 }
}
