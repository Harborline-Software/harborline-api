// CI load (owner, 2026-09-29): PR runs and the merge queue share one hosted-runner pool, so a PR run
// that a newer push has superseded is cancelled, and a PR labelled `stacked` skips its heavy PR jobs.
// Neither may touch a merge_group, push, schedule or workflow_dispatch run: the merge group still runs
// the full gate before anything lands, which is what makes a skipped-as-green PR context acceptable.
import assert from 'node:assert/strict'
import {readdirSync, readFileSync} from 'node:fs'
import path from 'node:path'
import test from 'node:test'

const dir = path.resolve(import.meta.dirname, '../../.github/workflows')
const read = file => readFileSync(path.join(dir, file), 'utf8').replaceAll('\r\n', '\n')
const guard = (text, job) => text.split(`\n  ${job}:\n`)[1].split(/\n    runs-on:/)[0]
const STACKED = "!contains(github.event.pull_request.labels.*.name, 'stacked')"
// Each property may use dot or quoted index access; inspecting only the last
// segment misses valid mixed context paths such as github.event['pull_request'].
const propertyAccess = key => String.raw`(?:\s*\.\s*${key}\b|\s*\[\s*(?:'${key}'|"${key}")\s*\])`
const PR_LABELS = new RegExp(String.raw`\bgithub${['event', 'pull_request', 'labels'].map(propertyAccess).join('')}`)
const assertPrLabelReads = (text, file) => assert.doesNotMatch(
  text.replaceAll(STACKED, ''), PR_LABELS,
  `${file}: reads PR labels outside the negated stacked guard`,
)

// The jobs a pull request runs that cost a runner. sbom and dependency-review stay: both are cheap.
const heavy = {
  'verify.yml': ['verify-shared', 'verify-perf', 'verify-perf-hosted', 'verify-macos', 'verify-linux', 'verify-windows', 'verify-windows-hosted', 'stryker'],
  'packages.yml': ['protocol-lane-conformance', 'operator-cli-headless', 'pack-consume'],
  'strykerjs.yml': ['strykerjs'],
}
const aggregators = {'verify.yml': ['verify']}

test('every pull_request workflow cancels its superseded PR run and only that', () => {
  for (const file of readdirSync(dir).filter(name => /\.ya?ml$/.test(name))) {
    const text = read(file)
    if (!/^ {2}pull_request:/m.test(text)) continue
    const block = text.match(/^concurrency:\n((?: {2}.*\n)+)/m)?.[1]
    assert.ok(block, `${file}: no workflow-level concurrency`)
    assert.match(block, /group: .*github\.event\.pull_request\.number/, `${file}: the group is not keyed on the PR`)
    assert.match(block, /cancel-in-progress: \$\{\{ github\.event_name == 'pull_request' \}\}/, `${file}: cancels more than PR runs`)
  }
})

test('a stacked PR skips its heavy PR jobs and its aggregator; a draft skips the heavy jobs', () => {
  for (const [file, jobs] of Object.entries(heavy)) {
    const text = read(file)
    for (const job of jobs) {
      assert.ok(guard(text, job).includes(STACKED), `${file} ${job}: no stacked skip`)
      assert.ok(guard(text, job).includes('github.event.pull_request.draft == false'), `${file} ${job}: no draft guard`)
    }
  }
  for (const [file, jobs] of Object.entries(aggregators)) {
    for (const job of jobs) assert.ok(guard(read(file), job).endsWith(`\n    if: always() && ${STACKED}`), `${file} ${job}: not always() plus the stacked skip`)
  }
})

// T-1001: stryker needed verify-windows, the winbox lane that since the 2026-09-29 ruling runs on a PR only
// for Dependabot, so on every other PR stryker was skipped with it and Stryker.NET feedback never ran.
test('stryker needs the hosted Windows lane, which an ordinary PR runs, and keeps its PR guards', () => {
  const text = read('verify.yml')
  const needs = guard(text, 'stryker').match(/^ {4}needs: \[(.*)\]$/m)?.[1].split(/,\s*/)
  assert.deepEqual(needs, ['verify-windows-hosted'], 'verify.yml stryker: does not need verify-windows-hosted')
  for (const lane of needs) assert.ok(!guard(text, lane).includes("github.actor == 'dependabot[bot]'"), `verify.yml stryker: needs ${lane}, which an ordinary PR skips`)
  assert.ok(guard(text, 'stryker').includes('github.event.pull_request.head.repo.full_name == github.repository'), 'verify.yml stryker: no fork guard')
})

test('PR labels are only read in negated stacked guards; runner outputs are independent', () => {
  // A merge_group, push, schedule or dispatch payload carries no pull_request labels: contains() is
  // false there, so the negation is true and the label cannot skip (or enable) anything outside a PR.
  for (const file of readdirSync(dir).filter(name => /\.ya?ml$/.test(name))) {
    const text = read(file)
    assertPrLabelReads(text, file)
  }
})

// Oracle: the owner contract above constrains PR labels, not runner-routing outputs.
test('PR-label contract accepts runner outputs and rejects unsafe PR-label reads', () => {
  const routing = 'labels: ${{ steps.route.outputs.labels }}\nruns-on: ${{ fromJSON(needs.workload-route.outputs.labels) }}'
  assert.doesNotThrow(() => assertPrLabelReads(routing, 'runner routing'))
  assert.doesNotThrow(() => assertPrLabelReads(`${routing}\nif: ${STACKED}`, 'stacked guard plus routing'))
  for (const unsafe of [
    "contains(github.event.pull_request.labels.*.name, 'stacked')",
    'github.event.pull_request.labels[0].name',
    "github.event.pull_request['labels'][0].name",
    'github . event . pull_request . labels[0].name',
    `${STACKED} || contains(github.event.pull_request.labels.*.name, 'run-heavy')`,
  ]) assert.throws(() => assertPrLabelReads(unsafe, 'unsafe fixture'), /reads PR labels outside/)
})

// Independently written GitHub index-access fixtures: each context segment can
// use bracket access, including mixed forms. None is an approved stacked guard.
for (const prLabels of [
  "github['event'].pull_request.labels",
  "github.event['pull_request'].labels",
  "github.event.pull_request['labels']",
  "github['event']['pull_request'].labels",
  "github['event'].pull_request['labels']",
  "github.event['pull_request']['labels']",
  "github['event']['pull_request']['labels']",
  "github [ 'event' ] . pull_request [ 'labels' ]",
]) test(`PR-label contract rejects index access: ${prLabels}`, () => {
  const unsafe = `contains(${prLabels}.*.name, 'run-heavy')`
  assert.throws(() => assertPrLabelReads(unsafe, 'index fixture'), /reads PR labels outside/)
  assert.throws(() => assertPrLabelReads(`${STACKED} || ${unsafe}`, 'guard plus index fixture'), /reads PR labels outside/)
})
