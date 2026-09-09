#!/usr/bin/env node
// Decide whether a fetched tree receipt (ticket 350) lets eng/land.sh skip its own gate.
//
// Kept free of git on purpose: eng/land.sh does the plumbing (fetch the ref, cat-file the blob) and
// hands the JSON text here, so eng/tests/receipt-accept.test.mjs can enumerate the refusals without
// a repository. The constants are IMPORTED from eng/verify-receipt.mjs rather than restated: a step
// added there must narrow what a landing accepts, and a copy would drift.
//
//   node eng/receipt-accept.mjs <tested-tree> <receipt.json>   # exit 0 accepts and prints the reason
import {readFileSync} from 'node:fs'
import {REPOSITORY, SCHEMA_VERSION, requiredStepIds} from './verify-receipt.mjs'

export const DEFAULT_MAX_AGE_HOURS = 24

// NOTE (350): hostBaseline is deliberately NOT checked here. Ticket 324 makes a macOS receipt
// slice-only for the HEAD-tree receipt the pre-push hook reads; ticket 350 is the user-approved
// exception for the MERGE-tree receipt, which is the whole point of the mac gating the tree that
// actually lands. verify-receipt.mjs's own refusal is untouched.
export function receiptProblem({receipt, testedTree, now = Date.now(), maxAgeHours = DEFAULT_MAX_AGE_HOURS}) {
  if (!receipt || typeof receipt !== 'object') return 'the receipt is not a JSON object'
  if (receipt.schemaVersion !== SCHEMA_VERSION) return `receipt schemaVersion ${receipt.schemaVersion}, expected ${SCHEMA_VERSION}`
  if (receipt.repository !== REPOSITORY) return `receipt is for ${receipt.repository}, not ${REPOSITORY}`
  if (receipt.testedTree !== testedTree) return `the receipt attests to tree ${String(receipt.testedTree).slice(0, 12)}, not ${String(testedTree).slice(0, 12)}`
  const missing = requiredStepIds.filter(id => !(Array.isArray(receipt.steps) ? receipt.steps : []).includes(id))
  if (missing.length > 0) return `the receipt does not cover: ${missing.join(', ')}`
  const recordedAt = Date.parse(receipt.recordedAt ?? '')
  if (!Number.isFinite(recordedAt)) return 'the receipt has no readable recordedAt'
  const ageHours = (now - recordedAt) / 3600000
  if (ageHours > maxAgeHours) return `the receipt is ${ageHours.toFixed(1)}h old, older than the ${maxAgeHours}h limit`
  if (ageHours < -1) return `the receipt is dated ${Math.abs(ageHours).toFixed(1)}h in the future`
  return null
}

export const acceptanceMessage = receipt =>
  `land: accepting receipt for tree ${receipt.testedTree} from ${receipt.host ?? 'unknown-host'} at ${receipt.recordedAt}; skipping the gate`

if (import.meta.url === `file://${process.argv[1]}` || process.argv[1]?.endsWith('receipt-accept.mjs')) {
  const [testedTree, file] = process.argv.slice(2)
  if (!testedTree || !file) { console.error('usage: receipt-accept.mjs <tested-tree> <receipt.json>'); process.exit(2) }
  const hours = Number(process.env.HARBORLINE_RECEIPT_MAX_AGE_HOURS || DEFAULT_MAX_AGE_HOURS)
  let receipt = null
  try { receipt = JSON.parse(readFileSync(file, 'utf8')) } catch (error) {
    console.log(`land: the receipt is not readable JSON (${error.message})`); process.exit(1)
  }
  const problem = receiptProblem({receipt, testedTree, maxAgeHours: Number.isFinite(hours) ? hours : DEFAULT_MAX_AGE_HOURS})
  if (problem) { console.log(`land: ${problem}`); process.exit(1) }
  console.log(acceptanceMessage(receipt))
}
