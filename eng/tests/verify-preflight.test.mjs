import assert from 'node:assert/strict'
import test from 'node:test'
import {mkdtempSync, mkdirSync, writeFileSync, rmSync, existsSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {verificationPlan, verifyPreflight} from '../verify-preflight.mjs'
import {beginQualityProduction, invalidateQualityProduction, recordQualityProduction, requireQualityProduction} from '../quality-production.mjs'
import {qualityArtifacts} from '../quality-step.mjs'

for (const lane of ['all', 'shared', 'host']) for (const flag of [undefined, '1']) {
  test(`${lane}, quality=${flag}: producer/consumer policy`, () => {
    const plan = verificationPlan({HARBORLINE_VERIFY_LANE: lane, HARBORLINE_GATE_QUALITY: flag})
    assert.equal(plan.quality, lane === 'all' || (lane === 'host' && flag === '1'))
    assert.equal(plan.tools.includes('cargo'), lane === 'all')
  })
}
test('unset lane is a full quality run; invalid lanes fail closed', () => {
  assert.equal(verificationPlan({}).quality, true)
  assert.throws(() => verificationPlan({HARBORLINE_VERIFY_LANE: 'typo'}), /must be/)
})
test('production startup removes every old SARIF input and its manifest, including stray nested reports', () => {
  const {apiRoot} = fixture()
  try {
    const directory = path.join(apiRoot, 'artifacts/quality')
    mkdirSync(path.join(directory, 'stray'), {recursive: true})
    const files = ['old.sarif', 'stray/old.sarif.json', 'old.sarif.raw', 'stray/old.sarif.json.raw'].map(file => path.join(directory, file))
    for (const file of files) writeFileSync(file, '{}')
    writeFileSync(path.join(directory, 'host.cobertura.xml'), '<coverage/>')
    recordQualityProduction(apiRoot, {head: 'old-head', run: 'old-run', files})
    const oldArtifacts = qualityArtifacts(apiRoot)
    beginQualityProduction(apiRoot, [...oldArtifacts.sarif, ...oldArtifacts.rawSarif])
    assert.deepEqual(qualityArtifacts(apiRoot).sarif, [])
    assert.deepEqual(qualityArtifacts(apiRoot).rawSarif, [])
    assert.equal(existsSync(path.join(directory, 'production.json')), false)
    assert.equal(existsSync(path.join(directory, 'host.cobertura.xml')), true)
  } finally { rmSync(apiRoot, {recursive: true, force: true}) }
})
function fixture() {
  const apiRoot = mkdtempSync(path.join(tmpdir(), 'api preflight space-'))
  mkdirSync(path.join(apiRoot, 'eng/baselines'), {recursive: true})
  mkdirSync(path.join(apiRoot, 'artifacts/quality'), {recursive: true})
  mkdirSync(path.join(apiRoot, '.feed'))
  writeFileSync(path.join(apiRoot, '.feed/packed-version.props'), '<Project/>')
  writeFileSync(path.join(apiRoot, 'global.json'), JSON.stringify({sdk: {version: '11.0.100-test', rollForward: 'disable'}}))
  writeFileSync(path.join(apiRoot, 'eng/platform-pin.json'), JSON.stringify({schemaVersion: 1, commit: 'a'.repeat(40)}))
  writeFileSync(path.join(apiRoot, 'eng/quality-pin.json'), JSON.stringify({schemaVersion: 1, repository: 'owner/quality', commit: 'b'.repeat(40)}))
  for (const file of ['eng/quality-policy.yaml', 'eng/baselines/quality-baseline.json']) writeFileSync(path.join(apiRoot, file), '{}')
  return {apiRoot, env: {}, version: tool => tool === 'dotnet' ? '11.0.100-test' : tool === 'node' ? 'v24.1.0' : '1.0',
    qualityCheckout: () => ({quality: 'fixture'}), controlPolicy: () => ({policyDefaults: 'fixture'}), identity: () => {}}
}
test('preflight refuses missing binaries, wrong SDK/Node, missing policies and unusable pinned checkouts', () => {
  const options = fixture()
  try {
    assert.equal(verifyPreflight(options).quality, true)
    assert.throws(() => verifyPreflight({...options, version: tool => { if (tool === 'pnpm') throw new Error('not found'); return options.version(tool) }}), /pnpm unavailable/)
    assert.throws(() => verifyPreflight({...options, version: tool => tool === 'dotnet' ? '10.0.100' : options.version(tool)}), /dotnet expected/)
    assert.throws(() => verifyPreflight({...options, version: tool => tool === 'node' ? 'v20.0.0' : options.version(tool)}), /node requires/)
    assert.throws(() => verifyPreflight({...options, qualityCheckout: () => ({reason: 'wrong pinned commit'})}), /QUALITY_REPO wrong pinned commit/)
    assert.throws(() => verifyPreflight({...options, controlPolicy: () => ({reason: 'missing policy'})}), /CONTROL_REPO missing policy/)
    rmSync(path.join(options.apiRoot, 'eng/quality-policy.yaml'))
    assert.throws(() => verifyPreflight(options), /missing eng\/quality-policy/)
    assert.equal(verifyPreflight({...options, env: {HARBORLINE_VERIFY_LANE: 'host'}}).quality, false)
    assert.equal(verifyPreflight({...options, env: {HARBORLINE_VERIFY_LANE: 'shared', HARBORLINE_GATE_QUALITY: '1'}}).quality, false)
    rmSync(path.join(options.apiRoot, '.feed/packed-version.props'))
    assert.throws(() => verifyPreflight({...options, env: {HARBORLINE_VERIFY_LANE: 'shared'}}), /missing \.feed\/packed-version.props/)
    assert.equal(verifyPreflight({...options, env: {HARBORLINE_VERIFY_LANE: 'host'}}).quality, false)
  } finally { rmSync(options.apiRoot, {recursive: true, force: true}) }
})
test('quality consumption refuses missing, previous-run, previous-head and changed analyzer outputs', () => {
  const {apiRoot} = fixture()
  const file = path.join(apiRoot, 'artifacts/quality/analyzer.sarif')
  const production = {head: 'head-a', run: 'run-a', files: [file]}
  try {
    writeFileSync(file, '{}')
    assert.throws(() => requireQualityProduction(apiRoot, production), /missing fresh/)
    assert.throws(() => recordQualityProduction(apiRoot, {...production, files: []}), /no SARIF/)
    recordQualityProduction(apiRoot, production)
    requireQualityProduction(apiRoot, production)
    assert.throws(() => requireQualityProduction(apiRoot, {...production, run: undefined}), /missing verification HEAD\/run/)
    const extra = path.join(apiRoot, 'artifacts/quality/unexpected.sarif')
    writeFileSync(extra, '{}')
    assert.throws(() => requireQualityProduction(apiRoot, {...production, files: [file, extra]}), /outputs changed/)
    rmSync(extra)
    assert.throws(() => requireQualityProduction(apiRoot, {...production, head: 'head-b'}), /stale/)
    assert.throws(() => requireQualityProduction(apiRoot, {...production, run: 'run-b'}), /stale/)
    assert.throws(() => requireQualityProduction(apiRoot, {...production, files: []}), /outputs changed/)
    writeFileSync(file, '{"changed":true}')
    assert.throws(() => requireQualityProduction(apiRoot, production), /outputs changed/)
    invalidateQualityProduction(apiRoot)
    assert.throws(() => requireQualityProduction(apiRoot, production), /missing fresh/)
  } finally { rmSync(apiRoot, {recursive: true, force: true}) }
})

test('raw compiler bytes participate in exact-head production provenance', () => {
  const {apiRoot} = fixture()
  const normalized = path.join(apiRoot, 'artifacts/quality/compiler.sarif')
  const raw = `${normalized}.raw`
  const production = {head: 'source-head', run: 'producer-run', files: [normalized, raw]}
  try {
    writeFileSync(normalized, '{"normalized":true}')
    writeFileSync(raw, 'original compiler bytes')
    recordQualityProduction(apiRoot, production)
    requireQualityProduction(apiRoot, production)
    writeFileSync(raw, 'replaced compiler bytes')
    assert.throws(() => requireQualityProduction(apiRoot, production), /outputs changed/)
  } finally { rmSync(apiRoot, {recursive: true, force: true}) }
})
test('all/shared require the generated local feed; either selective host produces its own', () => {
  const options = fixture()
  try {
    rmSync(path.join(options.apiRoot, '.feed/packed-version.props'))
    for (const lane of ['all', 'shared']) for (const flag of [undefined, '1']) {
      assert.throws(() => verifyPreflight({...options, env: {HARBORLINE_VERIFY_LANE: lane, HARBORLINE_GATE_QUALITY: flag}}), /missing \.feed\/packed-version.props/)
    }
    for (const flag of [undefined, '1']) {
      assert.equal(verifyPreflight({...options, env: {HARBORLINE_VERIFY_LANE: 'host', HARBORLINE_GATE_QUALITY: flag}}).quality, flag === '1')
    }
  } finally { rmSync(options.apiRoot, {recursive: true, force: true}) }
})
