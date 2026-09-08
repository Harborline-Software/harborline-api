import assert from 'node:assert/strict'
import {execFileSync} from 'node:child_process'
import {copyFileSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {test} from 'node:test'
import {addCoverageLabel, copyCoberturaReport, coverageSummary, setCoverageSourceRoot} from '../coverage.mjs'

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

test('Cobertura copy prefers the direct VSTest report and ignores Windows In staging copies', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'coverage-copy-'))
  try {
    const windows = path.join(dir, 'windows')
    const macos = path.join(dir, 'macos')
    const target = path.join(dir, 'target.xml')
    for (const results of [windows, macos]) mkdirSync(path.join(results, 'guid'), {recursive: true})
    writeFileSync(path.join(windows, 'guid', 'coverage.cobertura.xml'), coverage)
    mkdirSync(path.join(windows, 'guid', 'In', 'machine'), {recursive: true})
    writeFileSync(path.join(windows, 'guid', 'In', 'machine', 'coverage.cobertura.xml'), coverage)
    writeFileSync(path.join(macos, 'guid', 'coverage.cobertura.xml'), coverage)
    for (const resultsDirectory of [windows, macos]) {
      copyCoberturaReport({resultsDirectory, target, label: 'unit-tests', sourceRoot: '.'})
      assert.match(readFileSync(target, 'utf8'), /label="unit-tests"/)
    }
  } finally { rmSync(dir, {recursive: true, force: true}) }
})

test('Cobertura copy refuses different non-staging reports', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'coverage-copy-different-'))
  try {
    mkdirSync(path.join(dir, 'one'), {recursive: true})
    mkdirSync(path.join(dir, 'two'), {recursive: true})
    writeFileSync(path.join(dir, 'one', 'coverage.cobertura.xml'), coverage)
    writeFileSync(path.join(dir, 'two', 'coverage.cobertura.xml'), coverage.replace('hits="2"', 'hits="3"'))
    assert.throws(() => copyCoberturaReport({resultsDirectory: dir, target: path.join(dir, 'target.xml'), label: 'unit-tests', sourceRoot: '.'}),
      /different Cobertura reports/)
  } finally { rmSync(dir, {recursive: true, force: true}) }
})

test('Cobertura copy removes duplicate method-line detail without changing class coverage', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'coverage-copy-compact-'))
  try {
    const source = path.join(dir, 'coverage.cobertura.xml')
    const target = path.join(dir, 'target.xml')
    writeFileSync(source, `<!DOCTYPE coverage SYSTEM "coverage.dtd">${coverage.replace('name="One"', 'name="One&lt;Two"').replace('number="4" hits="0"', 'number="4" hits="0" branch="True"').replace('</lines></class>', '</lines><methods><method name="One"><lines><line number="4" hits="0"/></lines></method></methods></class>')}`)
    copyCoberturaReport({resultsDirectory: dir, target, label: 'unit-tests', sourceRoot: '.'})
    assert.doesNotMatch(readFileSync(target, 'utf8'), /<methods>/)
    assert.doesNotMatch(readFileSync(target, 'utf8'), /<!DOCTYPE|&/)
    assert.match(readFileSync(target, 'utf8'), /branch="true"/)
    assert.deepEqual(coverageSummary(readFileSync(target, 'utf8')), {coveredLines: 2, validLines: 3, paths: ['not-tracked/Missing.cs', 'src/One.cs']})
  } finally { rmSync(dir, {recursive: true, force: true}) }
})

test('source roots are forward-slash repository-relative paths', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'coverage-source-root-'))
  try {
    const file = path.join(dir, 'report.xml')
    writeFileSync(file, coverage)
    setCoverageSourceRoot(file, '.\\packages\\contracts')
    assert.match(readFileSync(file, 'utf8'), /<source>packages\/contracts<\/source>/)
  } finally { rmSync(dir, {recursive: true, force: true}) }
})

test('only the landing gate enables the host and contracts coverage route', () => {
  const runner = readFileSync(path.join(root, 'eng', 'run-exact-clone.mjs'), 'utf8')
  assert.match(runner, /coverageEnabled\(\)/)
  assert.match(runner, /--collect:XPlat Code Coverage/)
  assert.match(runner, /--settings', 'eng\/coverage\.runsettings'/)
  assert.match(runner, /copyCoberturaReport/)
  assert.match(runner, /\['run', 'test:coverage'\]/)
  assert.match(runner, /sourceRoot: '\.'/)
  assert.match(runner, /sourceRoot: 'packages\/contracts'/)
  const settings = readFileSync(path.join(root, 'eng', 'coverage.runsettings'), 'utf8')
  assert.match(settings, /<Format>cobertura<\/Format>/)
  assert.match(settings, /<Include>\[Harborline\.\*\]\*<\/Include>/)
  assert.match(settings, /<Exclude>\[\*\.Tests\]\*,\[\*Testing\*\]\*<\/Exclude>/)
  assert.match(settings, /<ExcludeByAttribute>GeneratedCodeAttribute,CompilerGeneratedAttribute<\/ExcludeByAttribute>/)
  assert.match(settings, /<ExcludeByFile>\*\*\/Generated\/\*\*\/\*\.cs,\*\*\/Migrations\/\*\*\/\*\.cs,\*\*\/obj\/\*\*\/\*\.cs<\/ExcludeByFile>/)
  const landing = readFileSync(path.join(root, 'eng', 'land.sh'), 'utf8')
  assert.equal((landing.match(/HARBORLINE_GATE_COVERAGE=1 bash eng\/verify\.sh/g) ?? []).length, 2)
  const contracts = JSON.parse(readFileSync(path.join(root, 'packages', 'contracts', 'package.json'), 'utf8'))
  assert.equal(contracts.devDependencies['@vitest/coverage-v8'], '5.0.0')
  assert.match(contracts.scripts['test:coverage'], /--coverage\.reporter=cobertura/)
  assert.match(contracts.scripts['test:coverage'], /--coverage\.reportsDirectory=\.\/coverage/)
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

test('the contracts coverage.reportsDirectory is gitignored so the script leaves the tree clean', () => {
  const pkg = JSON.parse(readFileSync(path.join(root, 'packages/contracts/package.json'), 'utf8'))
  const dir = pkg.scripts['test:coverage'].match(/--coverage\.reportsDirectory=(\S+)/)[1].replace(/^\.\//, '')
  execFileSync('git', ['check-ignore', '-q', `packages/contracts/${dir}/cobertura-coverage.xml`], {cwd: root})
})
