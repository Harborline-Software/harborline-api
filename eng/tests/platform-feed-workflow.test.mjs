import test from 'node:test'
import assert from 'node:assert/strict'
import {spawnSync} from 'node:child_process'
import {existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync} from 'node:fs'
import path from 'node:path'
import {tmpdir} from 'node:os'

const root = path.resolve(import.meta.dirname, '../..')
const workflow = readFileSync(path.join(root, '.github/workflows/packages.yml'), 'utf8').replaceAll('\r\n', '\n')
const action = readFileSync(path.join(root, '.github/actions/platform-feed/action.yml'), 'utf8').replaceAll('\r\n', '\n')
function job(name, text = workflow) {
  const match = new RegExp(`^  ${name}:\\n([\\s\\S]*?)(?=^  [a-z][a-z-]*:|$(?![\\s\\S]))`, 'm').exec(text)
  assert.ok(match, `job ${name} exists`)
  return match[0]
}
function condition(block) {
  const expression = /    if: >-\n([\s\S]*?)(?=^    \w)/m.exec(block)?.[1]
  assert.ok(expression, 'job has an explicit event guard')
  return expression.trim()
}
function admitted(expression, {event, draft = false, stacked = false, publish = false, enabled = 'true', ref = 'refs/heads/experiment'}) {
  const context = {event_name: event, event: {pull_request: {draft, labels: [{name: stacked ? 'stacked' : 'other'}]}}, ref}
  const translated = expression.replaceAll('github.event.pull_request.labels.*.name', 'labels')
  const labels = stacked ? ['stacked'] : []
  return Function('github', 'inputs', 'vars', 'contains', 'startsWith', 'labels', `return (${translated})`)(
    context, {publish}, {ACTIONS_ENABLED: enabled}, (values, item) => values.includes(item), (value, prefix) => value.startsWith(prefix), labels)
}

test('producer admission matches pack consumer for drafts, stacked PRs, queue, tags and dispatches', () => {
  const cases = [
    [{event: 'pull_request'}, true], [{event: 'pull_request', draft: true}, false],
    [{event: 'pull_request', stacked: true}, false], [{event: 'merge_group'}, true],
    [{event: 'push', ref: 'refs/tags/v1-preview.1'}, true], [{event: 'workflow_dispatch'}, true],
    [{event: 'workflow_dispatch', publish: true}, true],
  ]
  for (const [context, expected] of cases) for (const name of ['platform-feed-producer', 'pack-consume']) {
    assert.equal(admitted(condition(job(name)), context), expected, `${name}: ${JSON.stringify(context)}`)
  }
  for (const name of ['protocol-lane-conformance', 'operator-cli-headless']) {
    assert.equal(admitted(condition(job(name)), {event: 'push'}), false)
    assert.equal(admitted(condition(job(name)), {event: 'merge_group'}), true)
  }
})
test('only the three same-run Ubuntu consumers depend on the producer and select its exact outputs', () => {
  for (const name of ['protocol-lane-conformance', 'operator-cli-headless', 'pack-consume']) {
    const block = job(name)
    assert.match(block, /needs: \[platform-feed-producer\]/)
    assert.match(block, /runs-on: ubuntu-latest/)
    for (const field of ['artifact-id', 'artifact-digest']) {
      assert.ok(block.includes(`${field}: \${{ github.event_name != 'push' && inputs.publish != true && needs.platform-feed-producer.outputs.${field} || '' }}`))
    }
  }
  assert.match(job('platform-feed-producer'), /archive: false/)
  assert.match(job('platform-feed-producer'), /overwrite: false/)
  assert.match(job('platform-feed-producer'), /ARTIFACT_DIGEST.*BUNDLE_DIGEST/)
  const verify = readFileSync(path.join(root, '.github/workflows/verify.yml'), 'utf8')
  assert.doesNotMatch(verify, /needs: \[platform-feed-producer\]/)
})
test('publication-enabled dispatch and tag paths never select reuse, and trial dispatch cannot publish', () => {
  const producer = job('platform-feed-producer')
  const steps = producer.slice(producer.indexOf('    steps:'))
  const stepCount = (steps.match(/^      - /gm) ?? []).length
  assert.equal((steps.match(/if: github.event_name != 'push' && inputs.publish != true/g) ?? []).length, stepCount)
  const publish = condition(job('publish'))
  assert.equal(admitted(publish, {event: 'workflow_dispatch', publish: false}), false)
  assert.equal(admitted(publish, {event: 'workflow_dispatch', publish: true}), true)
  assert.equal(admitted(publish, {event: 'push', ref: 'refs/tags/v1-preview.1'}), true)
  assert.equal(admitted(publish, {event: 'pull_request'}), false)
  assert.match(job('publish'), /needs: \[pack-consume\]/)
})
test('same-run downloader uses exact ID, fails digest mismatch, and does not use other runs or credentials', () => {
  const download = /    - name: Download the exact same-run dependency artifact\n([\s\S]*?)(?=    - name:)/.exec(action)[1]
  assert.match(download, /artifact-ids: \$\{\{ inputs.artifact-id \}\}/)
  assert.match(download, /digest-mismatch: error/)
  assert.doesNotMatch(download, /\b(name|pattern|run-id|github-token|repository):/)
  assert.match(action, /restore .*FEED_ARTIFACT_DIGEST/)
  assert.match(action, /Reject failed downloads that left any bytes/)
  assert.match(action, /Validate a freshly rebuilt dependency feed after unavailable download/)
})

const bash = process.platform === 'win32' ? 'C:/Program Files/Git/bin/bash.exe' : 'bash'
test('download fallback accepts only zero transferred bytes and visibly rejects partial or corrupt downloads', {skip: process.platform === 'win32' && !existsSync(bash)}, t => {
  const script = /    - name: Reject failed downloads that left any bytes\n[\s\S]*?      run: \|\n([\s\S]*?)(?=    - name:)/.exec(action)[1]
    .replace(/^        /gm, '')
  const directory = mkdtempSync(path.join(tmpdir(), 'feed-fallback-test-'))
  t.after(() => { assert.equal(path.dirname(directory), tmpdir()); rmSync(directory, {recursive: true, force: true}) })
  mkdirSync(path.join(directory, '.feed-transfer'))
  const run = () => spawnSync(bash, ['-c', script], {cwd: directory, encoding: 'utf8'})
  const absent = run()
  assert.equal(absent.status, 0, absent.stderr)
  assert.match(absent.stdout, /unavailable with no bytes; rebuilding/)
  writeFileSync(path.join(directory, '.feed-transfer', 'platform-feed-bundle.json'), 'tampered')
  const corrupt = run()
  assert.equal(corrupt.status, 1, corrupt.stderr)
  assert.match(corrupt.stdout, /refusing possible corruption/)
})
