import {test} from 'node:test'
import assert from 'node:assert/strict'
import {spawnSync} from 'node:child_process'
import {mkdtempSync, writeFileSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {fileURLToPath} from 'node:url'
import {receiptProblem, acceptanceMessage, DEFAULT_MAX_AGE_HOURS} from '../receipt-accept.mjs'
import {REPOSITORY, SCHEMA_VERSION, requiredStepIds} from '../verify-receipt.mjs'

// Proof for eng/receipt-accept.mjs (ticket 350 slice 1): the four cases that decide whether an hour
// of Windows gating is skipped. Each refusal is asserted by its reason, not merely by "not accepted",
// because eng/land.sh prints that reason and a wrong one sends the next lane hunting the wrong thing.
const here = path.dirname(fileURLToPath(import.meta.url))
const script = path.join(here, '..', 'receipt-accept.mjs')
const TREE = 'a'.repeat(40)
const OTHER = 'b'.repeat(40)
const good = (over = {}) => ({
  schemaVersion: SCHEMA_VERSION,
  repository: REPOSITORY,
  hostBaseline: 'eng/baselines/host-test-baseline.macos.json',
  baseHead: 'c'.repeat(40),
  testedTree: TREE,
  steps: [...requiredStepIds],
  host: 'macbook-pro-73',
  recordedAt: new Date().toISOString(),
  ...over,
})

test('a good receipt is accepted and names tree, host and time', () => {
  const receipt = good()
  assert.equal(receiptProblem({receipt, testedTree: TREE}), null)
  assert.equal(acceptanceMessage(receipt),
    `land: accepting receipt for tree ${TREE} from macbook-pro-73 at ${receipt.recordedAt}; skipping the gate`)
})

test('a receipt for a different tree is refused', () => {
  const problem = receiptProblem({receipt: good(), testedTree: OTHER})
  assert.match(problem, /attests to tree aaaaaaaaaaaa, not bbbbbbbbbbbb/)
})

test('an expired receipt is refused, and the same receipt passes under a wider limit', () => {
  const receipt = good({recordedAt: new Date(Date.now() - 25 * 3600000).toISOString()})
  assert.match(receiptProblem({receipt, testedTree: TREE}), /25\.0h old, older than the 24h limit/)
  assert.equal(DEFAULT_MAX_AGE_HOURS, 24)
  assert.equal(receiptProblem({receipt, testedTree: TREE, maxAgeHours: 48}), null)
})

test('a receipt missing any single required step is refused, naming that step', () => {
  for (const step of requiredStepIds) {
    const receipt = good({steps: requiredStepIds.filter(id => id !== step)})
    assert.equal(receiptProblem({receipt, testedTree: TREE}), `the receipt does not cover: ${step}`)
  }
  assert.match(receiptProblem({receipt: good({steps: undefined}), testedTree: TREE}), /does not cover: /)
})

test('a stale schema, a foreign repository and an unreadable time are each refused', () => {
  assert.match(receiptProblem({receipt: good({schemaVersion: SCHEMA_VERSION + 1}), testedTree: TREE}), /schemaVersion/)
  assert.match(receiptProblem({receipt: good({repository: 'harborline-platform'}), testedTree: TREE}), /not harborline-api/)
  assert.match(receiptProblem({receipt: good({recordedAt: 'whenever'}), testedTree: TREE}), /no readable recordedAt/)
})

test('the CLI exits 0 and prints the land line only for the accepted case', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'receipt-accept-'))
  const run = (receipt, tree = TREE, env = {}) => {
    const file = path.join(dir, 'receipt.json')
    writeFileSync(file, JSON.stringify(receipt))
    const r = spawnSync(process.execPath, [script, tree, file], {encoding: 'utf8', env: {...process.env, ...env}})
    return {status: r.status, out: (r.stdout + r.stderr).trim()}
  }
  const accepted = run(good())
  assert.equal(accepted.status, 0, accepted.out)
  assert.match(accepted.out, /^land: accepting receipt for tree a{40} from macbook-pro-73 at .+; skipping the gate$/)

  const wrongTree = run(good(), OTHER)
  assert.equal(wrongTree.status, 1)
  assert.match(wrongTree.out, /^land: the receipt attests to tree/)

  const expired = run(good({recordedAt: new Date(Date.now() - 3 * 3600000).toISOString()}), TREE, {HARBORLINE_RECEIPT_MAX_AGE_HOURS: '2'})
  assert.equal(expired.status, 1)
  assert.match(expired.out, /older than the 2h limit/)

  const missing = run(good({steps: requiredStepIds.slice(1)}))
  assert.equal(missing.status, 1)
  assert.match(missing.out, new RegExp(`does not cover: ${requiredStepIds[0]}$`))
  rmSync(dir, {recursive: true, force: true})
})
