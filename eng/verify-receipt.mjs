#!/usr/bin/env node
// Local-verification receipt for harborline-api. Modelled on harborline-platform's
// tooling/verify-phase4-receipt.mjs, for the same reason and with the same shape.
//
// Why this exists: GitHub Actions is switched off in this repository (see the ACTIONS_ENABLED
// block at the top of .github/workflows/packages.yml — the org is on the free plan, private-repo
// minutes ran out on 2026-08-24, and the workflows are a duplicate of checks that can run here).
// With CI off, nothing was left that verified anything. `eng/verify.sh` is the replacement, and
// this file is what stops it from being optional.
//
// The receipt lives inside .git/, NOT in the tree. That is deliberate and load-bearing: a receipt
// that is a tracked file changes the tree it attests to, so it could never match itself.
//
// Keyed to HEAD rather than the index, because the expensive step (eng/run-exact-clone.mjs) clones
// HEAD and refuses a dirty tree. The hook is therefore pre-push, which is also what CI triggered on.
//
//   node eng/verify-receipt.mjs --record   # written by eng/verify.sh on success
//   node eng/verify-receipt.mjs            # verify; the pre-push hook calls this
import {execFileSync} from 'node:child_process'
import {existsSync, readFileSync, writeFileSync} from 'node:fs'
import path from 'node:path'
import {baselineArgument, receiptBaselineProblem} from './host-baseline.mjs'
import {receiptCoverage} from './coverage.mjs'
import {receiptCheckForPushRefs} from './pre-push-receipt.mjs'

export const REPOSITORY = 'harborline-api'
export const SCHEMA_VERSION = 1

// The steps eng/verify.sh must have run and passed. A receipt missing any of these is refused, so
// commenting a step out of verify.sh does not silently narrow what the hook accepts — the two
// files have to move together.
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

// Ticket 350: eng/receipt-accept.mjs imports REPOSITORY, SCHEMA_VERSION and requiredStepIds from here so a
// landing cannot accept a receipt this file would refuse. Importing must therefore not run git or verify
// anything, so everything below is the entry-point body and nothing else.
if (process.argv[1] && process.argv[1].replaceAll('\\', '/').endsWith('eng/verify-receipt.mjs')) {
  if (process.argv.includes('--pre-push')) {
    const decision = receiptCheckForPushRefs(readFileSync(0, 'utf8'))
    if (!decision.verify) {
      if (decision.skipped) console.log(`${REPOSITORY}: receipt check was skipped for a feature branch`)
      process.exit(0)
    }
  }

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

const refuse = (why) => {
  console.error(`\n  ${REPOSITORY}: refusing to push — ${why}`)
  console.error('\n  GitHub Actions is off in this repository, so this receipt is the only thing that')
  console.error('  verifies the commits you are pushing. Run:\n')
  console.error('      bash eng/verify.sh\n')
  console.error('  and push again. To push anyway (and own that nothing checked it): git push --no-verify\n')
  process.exit(1)
}

if (!existsSync(receiptPath)) refuse('no verification receipt exists')

let receipt
try {
  receipt = JSON.parse(readFileSync(receiptPath, 'utf8'))
} catch {
  refuse('the verification receipt is not readable JSON')
}

if (receipt.schemaVersion !== SCHEMA_VERSION) refuse(`receipt schemaVersion ${receipt.schemaVersion}, expected ${SCHEMA_VERSION}`)
if (receipt.repository !== REPOSITORY) refuse(`receipt is for ${receipt.repository}, not ${REPOSITORY}`)
// Default verification is landing-safe; --slice is explicit and cannot override --landing.
const baselineProblem = receiptBaselineProblem(receipt, process.argv.includes('--slice') && !process.argv.includes('--landing'))
if (baselineProblem) refuse(baselineProblem)
if (receipt.testedTree !== tree) {
  refuse(`the receipt attests to tree ${String(receipt.testedTree).slice(0, 12)}, but HEAD's tree is ${tree.slice(0, 12)}`)
}
if (receipt.baseHead !== head) {
  refuse(`the receipt attests to commit ${String(receipt.baseHead).slice(0, 12)}, but HEAD is ${head.slice(0, 12)}`)
}

const missing = requiredStepIds.filter(id => !(receipt.steps ?? []).map(stepId).includes(id))
if (missing.length > 0) refuse(`the receipt does not cover: ${missing.join(', ')}`)

console.log(`${REPOSITORY}: verification receipt matches HEAD ${head.slice(0, 12)} — ${receipt.steps.length} steps`)
}
