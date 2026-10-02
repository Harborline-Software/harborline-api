import {test} from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
test('enabled Windows default uses direct execution; non-Windows keeps the lock', () => {
  const source=readFileSync(new URL('../workflows/mutation.yml',import.meta.url),'utf8').replaceAll('\r\n','\n')
  const step=source.slice(source.indexOf('      - name: Reserved full mutation and isolated feed'), source.indexOf('      - name: Mutation report'))
  assert.ok(step.includes('"$RUNNER_OS" == "Windows"'))
  const windows=step.slice(step.indexOf('"$RUNNER_OS" == "Windows"'),step.indexOf('python .github/workloads/host-workload-lock.py'))
  assert.ok(windows.includes('source .github/workloads/full-mutation.sh'))
  assert.ok(!windows.includes('host-workload-lock.py'))
  assert.match(source,/if: .*runner\.os != 'Windows'\n        with:\n          python-version: '3\.14'/)
})
