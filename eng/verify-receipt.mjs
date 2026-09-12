#!/usr/bin/env node
// Local-verification receipt for harborline-api. Modelled on harborline-platform's
// tooling/verify-phase4-receipt.mjs, for the same reason and with the same shape.
//
// The receipt lives inside .git/, NOT in the tree. That is deliberate and load-bearing: a receipt
// that is a tracked file changes the tree it attests to, so it could never match itself.
//
// Keyed to HEAD rather than the index, because the expensive step (eng/run-exact-clone.mjs) clones
// HEAD and refuses a dirty tree.
//
//   node eng/verify-receipt.mjs --record   # written by eng/verify.sh on success
import {execFileSync} from 'node:child_process'
import {existsSync, readFileSync, writeFileSync} from 'node:fs'
import path from 'node:path'
import {baselineArgument} from './host-baseline.mjs'
import {receiptCoverage} from './coverage.mjs'

export const REPOSITORY = 'harborline-api'
export const SCHEMA_VERSION = 1

// The steps eng/verify.sh must have run and passed. A receipt missing any of these is refused, so
// commenting a step out of verify.sh does not silently narrow the evidence it records.
export const requiredStepIds = [
  'boundaries',
  'identity-r3',
  'codegen-check',
  'codegen-guard-suite',
  'contracts-typescript',
  'contracts-csharp',
  'localfirst-csharp',
  'rule-engine-conformance',
  'contracts-rust',
  'operator-cli-headless',
  'install-artefact',
  'exact-clone',
  'quality',
  'quality-baseline',
  'packages',
]

// Importing must not run git or record anything, so everything below is the entry-point body.
if (process.argv[1] && process.argv[1].replaceAll('\\', '/').endsWith('eng/verify-receipt.mjs')) {
const root = execFileSync('git', ['rev-parse', '--show-toplevel'], {encoding: 'utf8'}).trim()
const git = (...args) => execFileSync('git', ['-C', root, ...args], {encoding: 'utf8'}).trim()
const receiptPath = path.resolve(root, git('rev-parse', '--git-common-dir'), 'harborline-api-verify-receipt.json')
const qualityDecisionPath = path.resolve(path.dirname(receiptPath), 'harborline-api-quality-decision.json')
const stepId = step => typeof step === 'string' ? step : step?.id
const qualityEntry = () => {
  if (!existsSync(qualityDecisionPath)) throw new Error('quality decision is absent beside the receipt')
  const decision = JSON.parse(readFileSync(qualityDecisionPath, 'utf8'))
  if (!/^sha256:[a-f0-9]{64}$/.test(decision.decisionId) || !/^sha256:[a-f0-9]{64}$/.test(decision.policyDigest)) {
    throw new Error('quality decision is missing its decision or policy digest')
  }
  return {id: 'quality', decisionDigest: decision.decisionId, policyDigest: decision.policyDigest}
}

const head = git('rev-parse', 'HEAD')
const tree = git('rev-parse', 'HEAD^{tree}')

if (process.argv.includes('--record')) {
  const recordArgs = process.argv.slice(2)
  const hostBaseline = baselineArgument(recordArgs)
  // The receipt attests to HEAD's TREE, but eng/verify.sh runs against the WORKING tree. On a dirty
  // tree those are different things, and the receipt would vouch for code the run never saw. Refuse,
  // rather than record a claim that is quietly false.
  const dirty = git('status', '--porcelain')
  if (dirty) {
    console.error('refusing to record a receipt from a dirty working tree - commit first, then verify.')
    console.error('The receipt attests to HEAD; uncommitted changes were not what the run tested:')
    console.error(dirty.split(String.fromCharCode(10)).slice(0, 10).map(line => '  ' + line).join(String.fromCharCode(10)))
    process.exit(1)
  }
  const passed = recordArgs.slice(recordArgs.indexOf('--record') + 1).filter(id => !id.startsWith('-'))
  const missing = requiredStepIds.filter(id => !passed.includes(id))
  if (missing.length > 0) {
    console.error(`refusing to record a receipt missing: ${missing.join(', ')}`)
    process.exit(1)
  }
  writeFileSync(receiptPath, JSON.stringify({
    schemaVersion: SCHEMA_VERSION,
    repository: REPOSITORY,
    hostBaseline,
    coverage: receiptCoverage(root),
    baseHead: head,
    testedTree: tree,
    steps: passed.map(id => id === 'quality' ? qualityEntry() : id),
    recordedAt: new Date().toISOString(),
  }, null, 2) + '\n')
  console.log(`recorded verification receipt for ${head.slice(0, 12)} (tree ${tree.slice(0, 12)})`)
  process.exit(0)
}

}
