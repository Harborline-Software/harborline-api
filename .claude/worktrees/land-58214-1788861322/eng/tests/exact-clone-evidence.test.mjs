// node --test eng/tests/exact-clone-evidence.test.mjs
// Enumerates evidenceTarget over {record} x {PASS, FAIL}: a FAIL without --record must never resolve into the
// tracked tree (round-1 blocker on ticket 247), --record must keep the committed path, and a PASS without
// --record writes nothing.
import test from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import {execFileSync} from 'node:child_process'
import {evidenceTarget, FAIL_EVIDENCE_RELATIVE} from '../exact-clone-evidence.mjs'

const apiRoot = path.resolve(import.meta.dirname, '..', '..')
const evidencePath = path.join(apiRoot, 'docs/evidence/exact-clone.json')

test('--record keeps the committed evidence path for PASS and FAIL', () => {
  for (const status of ['PASS', 'FAIL']) {
    assert.equal(evidenceTarget({record: true, status, apiRoot, evidencePath}), evidencePath)
  }
})

test('FAIL without --record goes under .claude/land-evidence, outside the tracked tree', () => {
  const target = evidenceTarget({record: false, status: 'FAIL', apiRoot, evidencePath})
  assert.equal(target, path.join(apiRoot, FAIL_EVIDENCE_RELATIVE))
  assert.notEqual(target, evidencePath)
  // git must ignore it: a red gate can never dirty the checkout it ran in
  const rc = (() => { try { execFileSync('git', ['check-ignore', '-q', FAIL_EVIDENCE_RELATIVE], {cwd: apiRoot, stdio: 'ignore'}); return 0 } catch (e) { return e.status } })()
  assert.equal(rc, 0, `${FAIL_EVIDENCE_RELATIVE} is not gitignored`)
})

test('PASS without --record writes nothing', () => {
  assert.equal(evidenceTarget({record: false, status: 'PASS', apiRoot, evidencePath}), null)
})

test('run-exact-clone.mjs uses evidenceTarget (no second writer path)', async () => {
  const {readFileSync} = await import('node:fs')
  const src = readFileSync(path.join(apiRoot, 'eng/run-exact-clone.mjs'), 'utf8')
  assert.match(src, /evidenceTarget\(\{record, status: report\.status, apiRoot, evidencePath\}\)/)
  assert.doesNotMatch(src, /exact-clone-fail\.json/)
})
