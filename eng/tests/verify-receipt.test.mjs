import assert from 'node:assert/strict'
import {copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {spawnSync} from 'node:child_process'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {test} from 'node:test'
import {fileURLToPath} from 'node:url'
import {gitRetry} from './fixture-git-retry.mjs'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '..', '..')
function recordReceipt() {
  const directory = mkdtempSync(path.join(tmpdir(), 'verify-receipt-record-'))
  try {
    mkdirSync(path.join(directory, 'eng'))
    for (const file of ['verify-receipt.mjs', 'host-baseline.mjs', 'coverage.mjs']) {
      copyFileSync(path.join(root, 'eng', file), path.join(directory, 'eng', file))
    }
    const git = args => gitRetry(gitArgs => spawnSync('git', gitArgs, {cwd: directory, encoding: 'utf8'}), args)
    for (const args of [['init', '-q'], ['add', '.'], ['-c', 'user.name=Receipt Test', '-c', 'user.email=receipt@example.invalid', 'commit', '--no-verify', '-qm', 'fixture']]) {
      const result = git(args)
      assert.equal(result.status, 0, result.stdout + result.stderr)
    }
    writeFileSync(path.join(directory, '.git', 'harborline-api-quality-decision.json'), JSON.stringify({
      decisionId: 'sha256:' + 'a'.repeat(64), policyDigest: 'sha256:' + 'b'.repeat(64),
    }))
    const source = readFileSync(path.join(directory, 'eng', 'verify-receipt.mjs'), 'utf8')
    const steps = [...source.match(/export const requiredStepIds = \[([^\]]+)\]/)[1].matchAll(/'([^']+)'/g)].map(match => match[1])
    const result = spawnSync(process.execPath, ['eng/verify-receipt.mjs', '--record', ...steps, '--host-baseline', 'eng/baselines/host-test-baseline.json'], {
      cwd: directory,
      encoding: 'utf8',
    })
    assert.equal(result.status, 0, result.stdout + result.stderr)
    return JSON.parse(readFileSync(path.join(directory, '.git', 'harborline-api-verify-receipt.json'), 'utf8'))
  } finally { rmSync(directory, {recursive: true, force: true}) }
}

test('records every required verification step as per-run evidence', () => {
  const receipt = recordReceipt()
  assert.equal(receipt.repository, 'harborline-api')
  assert.equal(receipt.steps.length, 17)
  assert.deepEqual(receipt.steps.map(step => typeof step === 'string' ? step : step.id), [
    'boundaries', 'dependency-ledger', 'identity-r3', 'codegen-check', 'codegen-guard-suite', 'contracts-typescript',
    'contracts-csharp', 'localfirst-csharp', 'rule-engine-conformance', 'contracts-rust',
    'operator-cli-headless', 'install-artefact', 'removal-exercise', 'exact-clone', 'quality', 'quality-baseline', 'packages',
  ])
})

test('a lane requires exactly its own steps, and the two lanes together are the whole gate', async () => {
  const {requiredStepIds, hostStepIds, stepIdsForLane} = await import('../verify-receipt.mjs')
  const quality = {HARBORLINE_GATE_QUALITY: '1'}
  assert.deepEqual(stepIdsForLane('all'), requiredStepIds)
  assert.deepEqual(stepIdsForLane('host', quality), hostStepIds)
  // No step may fall between the lanes: a gap is a step CI stops running without telling anyone.
  assert.deepEqual([...stepIdsForLane('shared'), ...stepIdsForLane('host', quality)].sort(), [...requiredStepIds].sort())
  // quality reads the SARIF exact-clone writes, so it belongs to the host that produces it.
  assert.equal(stepIdsForLane('shared').includes('exact-clone'), false)
  assert.equal(stepIdsForLane('shared').includes('quality'), false)
  assert.deepEqual(stepIdsForLane('host', {}), ['exact-clone'])
})
