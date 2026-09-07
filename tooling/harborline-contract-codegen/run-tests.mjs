#!/usr/bin/env node
// Runs the codegen guard suite and fails CLOSED. Ticket 081 wave-2 finding contracts-1: this
// 468-line suite - including the only executable gate on boundary-inventory drift - ran in no
// workflow, and the drift its comments warn about accrued invisibly. This wrapper makes the
// suite binding again with two honesty rules:
//
// 1. An empty test match fails (bare `node --test` exits 0 when its glob matches nothing).
// 2. The three app-relocation failures are pinned BY NAME below: the earlier app never
//    lived in this repository (the same relocation that killed the NavigationPermissionProjection
//    scrape fence, wave-1 finding api-archtests-2), so the tests that read that app's sources
//    cannot pass here. They are expected failures, not skips: a fourth failure fails the step,
//    and so does any of these three unexpectedly passing - either change means the relocation
//    disposition (control ticket 084) must be revisited, not ignored.
import {spawnSync} from 'node:child_process'
import {dirname, resolve} from 'node:path'
import {fileURLToPath} from 'node:url'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
// Ticket 224 made Harborline App command discovery checkout-independent through a checked-in fixture that
// is verified against the real generate_handler! list whenever that producer is present. The whole
// inventory gate therefore runs normally now; there are no expected failures left.
const expectedRelocationFailures = new Set()
const run = spawnSync(process.execPath, ['--test', '--test-reporter', 'tap', 'tooling/harborline-contract-codegen/tests/*.test.mjs'], {
  cwd: repoRoot,
  encoding: 'utf8',
  maxBuffer: 64 * 1024 * 1024,
})
const stdout = run.stdout ?? ''
const counts = Object.fromEntries(['tests', 'pass', 'fail', 'cancelled', 'skipped', 'todo']
  .map(key => [key, Number(new RegExp(`^# ${key} (\\d+)$`, 'm').exec(stdout)?.[1] ?? NaN)]))
const failedNames = [...stdout.matchAll(/^not ok \d+ - (.+?)\s*$/gm)].map(match => match[1])
const unexpectedFailures = failedNames.filter(name => !expectedRelocationFailures.has(name))
const missingExpectedFailures = [...expectedRelocationFailures].filter(name => !failedNames.includes(name))
const passed = counts.tests > 0
  && counts.cancelled === 0 && counts.skipped === 0
  && unexpectedFailures.length === 0
  && missingExpectedFailures.length === 0
  && counts.fail === expectedRelocationFailures.size
  && counts.pass === counts.tests - expectedRelocationFailures.size
process.stdout.write(`${JSON.stringify({
  schemaVersion: 1,
  status: passed ? 'PASS' : 'FAIL',
  counts,
  expectedRelocationFailures: [...expectedRelocationFailures],
  unexpectedFailures,
  missingExpectedFailures,
}, null, 2)}\n`)
if (!passed) process.stderr.write(`${stdout.split('\n').slice(-60).join('\n')}\n${run.stderr ?? ''}\n`)
process.exitCode = passed ? 0 : 1
