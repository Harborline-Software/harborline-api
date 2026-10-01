import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import {assertNoCacheAccess} from './cache-isolation.mjs'
test('runner none permits execution', () => assert.doesNotThrow(() => assertNoCacheAccess('none')))
test('missing/read/write/write-only capabilities refuse execution', () => {
  for (const mode of [undefined, '', 'read', 'write', 'write-only']) assert.throws(() => assertNoCacheAccess(mode), /refusing execution/)
})
test('mutation workflow disables cache capability and guards both jobs', () => {
  const workflow = readFileSync(new URL('../workflows/mutation.yml', import.meta.url), 'utf8')
  assert.match(workflow, /^cache-mode: none$/m)
  assert.equal((workflow.match(/run: node \.github\/workloads\/cache-isolation\.mjs/g) || []).length, 2)
  assert.ok(workflow.indexOf('Verify cache capability isolation') < workflow.indexOf('id: platform_pin'))
})
