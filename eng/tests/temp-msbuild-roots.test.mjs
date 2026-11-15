import assert from 'node:assert/strict'
import {mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync, symlinkSync} from 'node:fs'
import {tmpdir} from 'node:os'
import path from 'node:path'
import test from 'node:test'

for (const [file, env] of [['run-exact-clone.mjs', {}], ['platform-feed-benchmark.mjs', {RUNNER_TEMP: ''}]]) {
  test(`${file} creates its MSBuild scratch root under the real temporary path`, t => {
    const parent = mkdtempSync(path.join(tmpdir(), 'msbuild-symlink-test-'))
    t.after(() => rmSync(parent, {recursive: true, force: true}))
    const actual = path.join(parent, 'actual'), link = path.join(parent, 'link')
    mkdirSync(actual)
    symlinkSync(actual, link, process.platform === 'win32' ? 'junction' : 'dir')
    const source = readFileSync(new URL(`../${file}`, import.meta.url), 'utf8')
    const declaration = source.match(/^\s*const scratch = .*$/m)?.[0]
    assert.ok(declaration)
    const scratch = new Function('mkdtempSync', 'realpathSync', 'path', 'tmpdir', 'env',
      `${declaration}; return scratch`)(mkdtempSync, realpathSync, path, () => link, env)
    assert.equal(path.dirname(scratch), realpathSync(link))
  })
}
