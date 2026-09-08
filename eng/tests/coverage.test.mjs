import assert from 'node:assert/strict'
import {execFileSync} from 'node:child_process'
import {copyFileSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {test} from 'node:test'
import {addCoverageLabel, coverageSummary} from '../coverage.mjs'

const root = path.resolve(import.meta.dirname, '../..')
const coverage = `<coverage line-rate="0"><sources><source>/unmapped/source</source></sources><packages><package name="p"><classes>
  <class name="One" filename="src/One.cs"><lines><line number="4" hits="0"/><line number="8" hits="2"/></lines></class>
  <class name="OneAgain" filename="src/One.cs"><lines><line number="4" hits="1"/></lines></class>
  <class name="Missing" filename="not-tracked/Missing.cs"><lines><line number="2" hits="0"/></lines></class>
</classes></package></packages></coverage>`

test('label post-step is idempotent', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'coverage-label-'))
  try {
    const file = path.join(dir, 'report.xml')
    writeFileSync(file, coverage)
    addCoverageLabel(file, 'unit-tests')
    addCoverageLabel(file, 'unit-tests')
    assert.match(readFileSync(file, 'utf8'), /^<coverage\b[^>]*\blabel="unit-tests"/)
    assert.equal((readFileSync(file, 'utf8').match(/\blabel="unit-tests"/g) ?? []).length, 1)
  } finally { rmSync(dir, {recursive: true, force: true}) }
})

test('Cobertura union keeps an unmapped class path and takes maximum line hits', () => {
  const summary = coverageSummary(coverage)
  assert.deepEqual(summary, {
    coveredLines: 2,
    validLines: 3,
    paths: ['not-tracked/Missing.cs', 'src/One.cs'],
  })
})

test('only the landing gate enables the host and contracts coverage route', () => {
  const runner = readFileSync(path.join(root, 'eng', 'run-exact-clone.mjs'), 'utf8')
  assert.match(runner, /coverageEnabled\(\)/)
  assert.match(runner, /--collect:XPlat Code Coverage/)
  assert.match(runner, /DataCollectionRunSettings\.DataCollectors\.DataCollector\.Configuration\.Format=cobertura/)
  assert.match(runner, /copyCoberturaReport/)
  assert.match(runner, /\['run', 'test:coverage', '--'/)
  const landing = readFileSync(path.join(root, 'eng', 'land.sh'), 'utf8')
  assert.equal((landing.match(/HARBORLINE_GATE_COVERAGE=1 bash eng\/verify\.sh/g) ?? []).length, 2)
  const contracts = JSON.parse(readFileSync(path.join(root, 'packages', 'contracts', 'package.json'), 'utf8'))
  assert.equal(contracts.devDependencies['@vitest/coverage-v8'], '5.0.0')
  assert.match(contracts.scripts['test:coverage'], /--coverage\.reporter=cobertura/)
})

test('receipt records both coverage entries when flagged and none otherwise', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'coverage-receipt-'))
  try {
    mkdirSync(path.join(dir, 'eng'), {recursive: true})
    for (const file of ['coverage.mjs', 'host-baseline.mjs', 'verify-receipt.mjs']) {
      copyFileSync(path.join(root, 'eng', file), path.join(dir, 'eng', file))
    }
    writeFileSync(path.join(dir, '.gitignore'), 'artifacts/\n')
    const run = (args, env = {}) => execFileSync(args[0], args.slice(1), {cwd: dir, env: {...process.env, ...env}, encoding: 'utf8'})
    run(['git', 'init', '-q'])
    run(['git', 'add', '.'])
    run(['git', '-c', 'user.name=Coverage Test', '-c', 'user.email=coverage@example.invalid', 'commit', '--no-verify', '-qm', 'fixture'])
    const steps = [...readFileSync(path.join(dir, 'eng', 'verify-receipt.mjs'), 'utf8')
      .match(/export const requiredStepIds = \[([^\]]+)\]/)[1].matchAll(/'([^']+)'/g)].map(match => match[1])
    const record = env => run([process.execPath, 'eng/verify-receipt.mjs', '--record', ...steps, '--host-baseline', 'eng/baselines/host-test-baseline.json'], env)

    record()
    let receipt = JSON.parse(readFileSync(path.join(dir, '.git', 'harborline-api-verify-receipt.json'), 'utf8'))
    assert.equal(receipt.coverage, 'none')

    mkdirSync(path.join(dir, 'artifacts', 'quality'), {recursive: true})
    writeFileSync(path.join(dir, 'artifacts', 'quality', 'host.cobertura.xml'), coverage)
    writeFileSync(path.join(dir, 'artifacts', 'quality', 'contracts.cobertura.xml'), coverage.replaceAll('src/One.cs', 'src/Two.ts'))
    record({HARBORLINE_GATE_COVERAGE: '1'})
    receipt = JSON.parse(readFileSync(path.join(dir, '.git', 'harborline-api-verify-receipt.json'), 'utf8'))
    assert.deepEqual(receipt.coverage, {
      host: {path: 'artifacts/quality/host.cobertura.xml', coveredLines: 2, validLines: 3},
      contracts: {path: 'artifacts/quality/contracts.cobertura.xml', coveredLines: 2, validLines: 3},
    })
  } finally { rmSync(dir, {recursive: true, force: true}) }
})
