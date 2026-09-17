import assert from 'node:assert/strict'
import {mkdtempSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import test from 'node:test'
import {compareFindings, loadBaseline} from '../quality-baseline-compare.mjs'

const finding = (fingerprint, ruleId, file, line, project = 'project-a') => ({fingerprint, ruleId, path: file, line, project, symbol: null, snippetHash: null})
const insertedAtStart = 'diff --git a/src/Existing.cs b/src/Existing.cs\n--- a/src/Existing.cs\n+++ b/src/Existing.cs\n@@ -1,1 +1,11 @@\n+// one\n+// two\n+// three\n+// four\n+// five\n+// six\n+// seven\n+// eight\n+// nine\n+// ten\n namespace Example;\n'

test('a line moved by an insertion is neither new nor resolved', () => {
  const result = compareFindings([finding('head-50', 'CA1000', 'src/Existing.cs', 50)], [finding('base-40', 'CA1000', 'src/Existing.cs', 40)], insertedAtStart)
  assert.deepEqual(result.newFindings, [])
  assert.deepEqual(result.resolved, [])
})

test('a genuinely new finding remains new with its rule and path', () => {
  const result = compareFindings([finding('kept', 'CA1000', 'src/Existing.cs', 50), finding('new', 'CA2000', 'src/New.cs', 8)], [finding('base', 'CA1000', 'src/Existing.cs', 40)], insertedAtStart)
  assert.deepEqual(result.newFindings.map(row => [row.ruleId, row.path]), [['CA2000', 'src/New.cs']])
})

test('two findings sharing a rule and file keep their individual identity when one moves', () => {
  const result = compareFindings([finding('head-50', 'CA1000', 'src/Existing.cs', 50), finding('unchanged-100', 'CA1000', 'src/Existing.cs', 100)], [finding('base-40', 'CA1000', 'src/Existing.cs', 40), finding('unchanged-100', 'CA1000', 'src/Existing.cs', 100)], insertedAtStart)
  assert.deepEqual(result.newFindings, [])
  assert.deepEqual(result.resolved, [])
})

test('an unavailable base artifact falls back to the committed baseline and says so', () => {
  const directory = mkdtempSync(path.join(tmpdir(), 'quality-baseline-fallback-'))
  try {
    const committed = path.join(directory, 'committed.json')
    writeFileSync(committed, JSON.stringify({findings: [finding('committed', 'CA1000', 'src/Existing.cs', 40)]}))
    const result = loadBaseline(path.join(directory, 'missing-artifact.json'), committed)
    assert.equal(result.source, 'committed fallback')
    assert.equal(result.findings.length, 1)
  } finally {
    rmSync(directory, {recursive: true, force: true})
  }
})

const rewroteTheWholeFile = 'diff --git a/src/Existing.cs b/src/Existing.cs\n--- a/src/Existing.cs\n+++ b/src/Existing.cs\n@@ -1,200 +1,240 @@\n-// old\n+// new\n'

test('a rewritten neighbourhood does not turn its own untouched findings into new ones', () => {
  // The 121 shape: fingerprint, anchor and line all move because the surrounding code was rewritten,
  // and movedLine refuses a line inside the hunk. The diagnostics themselves never changed.
  const head = [finding('head-a', 'CA1873', 'src/Existing.cs', 128), finding('head-b', 'CA1873', 'src/Existing.cs', 204)]
  const base = [finding('base-a', 'CA1873', 'src/Existing.cs', 96), finding('base-b', 'CA1873', 'src/Existing.cs', 150)]
  const result = compareFindings(head, base, rewroteTheWholeFile)
  assert.deepEqual(result.newFindings, [])
  assert.deepEqual(result.resolved, [])
  assert.deepEqual(result.matches.map(match => match.method), ['identity', 'identity'])
})

test('an added diagnostic of a rule already in the file is still new', () => {
  const head = [finding('head-a', 'CA1873', 'src/Existing.cs', 128), finding('head-b', 'CA1873', 'src/Existing.cs', 204)]
  const base = [finding('base-a', 'CA1873', 'src/Existing.cs', 96)]
  const result = compareFindings(head, base, rewroteTheWholeFile)
  assert.equal(result.newFindings.length, 1)
  assert.deepEqual(result.resolved, [])
})
