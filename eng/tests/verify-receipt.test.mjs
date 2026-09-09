import assert from 'node:assert/strict'
import {copyFileSync, mkdirSync, mkdtempSync, rmSync} from 'node:fs'
import {spawnSync} from 'node:child_process'
import {tmpdir} from 'node:os'
import path from 'node:path'
import {test} from 'node:test'
import {fileURLToPath} from 'node:url'
import {receiptCheckForPushRefs} from '../pre-push-receipt.mjs'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '..', '..')
const zero = '0'.repeat(40)
const sha = '1'.repeat(40)

function withoutReceipt(input) {
  const directory = mkdtempSync(path.join(tmpdir(), 'verify-receipt-pre-push-'))
  try {
    mkdirSync(path.join(directory, 'eng'))
    for (const file of ['verify-receipt.mjs', 'host-baseline.mjs', 'pre-push-receipt.mjs', 'coverage.mjs']) {
      copyFileSync(path.join(root, 'eng', file), path.join(directory, 'eng', file))
    }
    const git = args => spawnSync('git', args, {cwd: directory, encoding: 'utf8'})
    for (const args of [['init', '-q'], ['add', '.'], ['-c', 'user.name=Receipt Test', '-c', 'user.email=receipt@example.invalid', 'commit', '--no-verify', '-qm', 'fixture']]) {
      const result = git(args)
      assert.equal(result.status, 0, result.stdout + result.stderr)
    }
    return spawnSync(process.execPath, ['eng/verify-receipt.mjs', '--pre-push'], {
      cwd: directory,
      encoding: 'utf8',
      input,
    })
  } finally { rmSync(directory, {recursive: true, force: true}) }
}

test('pre-push: a feature ref without a receipt passes', () => {
  const input = `refs/heads/feat/x ${sha} refs/heads/feat/x ${zero}\n`
  assert.deepEqual(receiptCheckForPushRefs(input), {verify: false, skipped: true})
  const result = withoutReceipt(input)
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.equal(result.stdout, 'harborline-api: receipt check was skipped for a feature branch\n')
})

test('pre-push: main without a receipt is refused with the existing message', () => {
  const input = `refs/heads/main ${sha} refs/heads/main ${zero}\n`
  assert.deepEqual(receiptCheckForPushRefs(input), {verify: true, skipped: false})
  const result = withoutReceipt(input)
  assert.equal(result.status, 1, result.stdout + result.stderr)
  assert.match(result.stderr, /harborline-api: refusing to push — no verification receipt exists/)
})

test('pre-push: deleting a branch passes without a receipt', () => {
  const input = `(delete) ${zero} refs/heads/feat/x ${sha}\n`
  assert.deepEqual(receiptCheckForPushRefs(input), {verify: false, skipped: false})
  const result = withoutReceipt(input)
  assert.equal(result.status, 0, result.stdout + result.stderr)
  assert.equal(result.stdout, '')
})
