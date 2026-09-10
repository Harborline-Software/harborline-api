// 339 s4 landing defect (2026-09-09): the artifact walk fed cqg the raw per-run coverlet files under
// artifacts/quality/coverage/ (30 MB each) beside the merged report and tripped its 16 MB input cap.
// Only the merged top-level reports are quality inputs.
import assert from 'node:assert/strict'
import {mkdtempSync, mkdirSync, writeFileSync, rmSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import test from 'node:test'
import {qualityArtifacts} from '../quality-step.mjs'

test('only top-level cobertura reports are quality inputs; raw per-run coverlet output is not', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'quality-artifacts-'))
  try {
    const quality = path.join(root, 'artifacts', 'quality')
    mkdirSync(path.join(quality, 'coverage', 'host', 'run-1'), {recursive: true})
    mkdirSync(path.join(quality, 'roslyn'), {recursive: true})
    writeFileSync(path.join(quality, 'host.cobertura.xml'), '<coverage/>')
    writeFileSync(path.join(quality, 'contracts.cobertura.xml'), '<coverage/>')
    writeFileSync(path.join(quality, 'coverage', 'host', 'run-1', 'coverage.cobertura.xml'), '<coverage/>')
    writeFileSync(path.join(quality, 'roslyn', 'A.sarif'), '{}')
    const found = qualityArtifacts(root)
    assert.deepEqual(found.cobertura.map(f => path.relative(quality, f)).sort(), ['contracts.cobertura.xml', 'host.cobertura.xml'])
    assert.deepEqual(found.sarif.map(f => path.relative(quality, f)), [path.join('roslyn', 'A.sarif')])
  } finally {
    rmSync(root, {recursive: true, force: true})
  }
})
