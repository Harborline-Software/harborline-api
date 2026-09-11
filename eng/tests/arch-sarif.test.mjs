import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {writeArchSarif, failuresFromTrx} from '../arch-sarif.mjs'

const trx = ({outcome = 'Failed', message = ''} = {}) => `<?xml version="1.0"?><TestRun><Results><UnitTestResult outcome="${outcome}" testName="R2: foundation references no blocks projects"><Output><ErrorInfo><Message><![CDATA[${message}]]></Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>`

test('arch adapter maps one tier-fence failure to one normalized arch result', t => {
  const root = mkdtempSync(path.join(tmpdir(), 'arch-sarif-test-'))
  t.after(() => rmSync(root, {recursive: true, force: true}))
  mkdirSync(path.join(root, 'packages/foundation-a'), {recursive: true})
  writeFileSync(path.join(root, 'packages/foundation-a/A.csproj'), '<Project>\n<ProjectReference Include="../blocks-b/B.csproj" />\n</Project>\n')
  mkdirSync(path.join(root, 'packages/blocks-b'))
  writeFileSync(path.join(root, 'packages/blocks-b/B.csproj'), '<Project />\n')
  const input = path.join(root, 'input.trx'), output = path.join(root, 'arch.sarif')
  writeFileSync(input, trx({message: 'R2 failed (1 violating edges):\npackages/foundation-a/A.csproj -> packages/blocks-b/B.csproj (foundation -> blocks)'}))
  writeArchSarif(input, output, root)
  const run = JSON.parse(readFileSync(output, 'utf8')).runs[0]
  assert.equal(run.tool.driver.name, 'arch')
  assert.equal(run.invocations[0].executionSuccessful, true)
  assert.equal(run.results.length, 1)
  assert.equal(run.results[0].ruleId, 'HLQ.ARCH.1000')
  assert.equal(run.results[0].locations[0].physicalLocation.artifactLocation.uri, 'packages/foundation-a/A.csproj')
  assert.equal(run.results[0].locations[0].physicalLocation.region.startLine, 2)
  assert.deepEqual(Object.keys(run.results[0].partialFingerprints).sort(), ['harborline/primary-location/v1', 'harborline/primary-location/v2', 'harborline/project/v1'])
})

test('arch adapter produces a successful zero-result run for a passing fence', t => {
  const root = mkdtempSync(path.join(tmpdir(), 'arch-sarif-pass-'))
  t.after(() => rmSync(root, {recursive: true, force: true}))
  const input = path.join(root, 'input.trx'), output = path.join(root, 'arch.sarif')
  writeFileSync(input, trx({outcome: 'Passed'}))
  writeArchSarif(input, output, root)
  const run = JSON.parse(readFileSync(output, 'utf8')).runs[0]
  assert.equal(run.invocations[0].executionSuccessful, true)
  assert.deepEqual(run.results, [])
})

test('a passed self-closing result before the failed one does not swallow it (TRX order is not stable)', () => {
  const failure = 'System.InvalidOperationException : R2: foundation references no blocks projects failed (1 violating edges):\n  packages/foundation-a/A.csproj -> packages/blocks-b/B.csproj (foundation -> blocks)'
  const text = `<?xml version="1.0"?><TestRun><Results><UnitTestResult outcome="Passed" testName="R1: kernel references no foundation or blocks projects" /><UnitTestResult outcome="Failed" testName="R2: foundation references no blocks projects"><Output><ErrorInfo><Message><![CDATA[${failure}]]></Message></ErrorInfo></Output></UnitTestResult><UnitTestResult outcome="Passed" testName="R3: packages reference no apps projects" /></Results></TestRun>`
  assert.equal(failuresFromTrx(text).length, 1)
})

test('a nested InnerResults child before the parent Output does not hide the parent edge message (CRLF too)', () => {
  const edge = 'R2: foundation references no blocks projects failed (1 violating edges):\r\n  packages/foundation-a/A.csproj -> packages/blocks-b/B.csproj (foundation -> blocks)'
  const text = `<?xml version="1.0"?><TestRun><Results><UnitTestResult outcome="Failed" testName="R2: foundation references no blocks projects"><InnerResults><UnitTestResult outcome="Failed" testName="inner"><Output><ErrorInfo><Message>inner</Message></ErrorInfo></Output></UnitTestResult></InnerResults><Output><ErrorInfo><Message><![CDATA[${edge}]]></Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>`
  const failures = failuresFromTrx(text)
  assert.equal(failures.length, 1)
  assert.match(failures[0].message, /A\.csproj -> packages\/blocks-b\/B\.csproj/)
})
