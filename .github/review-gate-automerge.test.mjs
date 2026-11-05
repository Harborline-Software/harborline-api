// Tests for the merge-queue and auto-merge halves of review-gate: review-gate-queue.sh (carries a PR's result to its
// merge group's commit), review-gate-arm.sh (decides whether a passed PR may merge on its own) and the outputs
// review-gate.sh hands the auto-merge job. Each case runs the real script against a stub `gh` that serves fixture
// files and logs every call, then checks what the script posted or ran.
// Run: node --test .github/review-gate-automerge.test.mjs
import {test} from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, chmodSync, existsSync} from 'node:fs'
import {tmpdir} from 'node:os'
import {join} from 'node:path'
import {spawnSync} from 'node:child_process'

const here = new URL('.', import.meta.url).pathname
const HEAD = 'a'.repeat(40)
const GROUP = 'c'.repeat(40)
const REF = `refs/heads/gh-readonly-queue/main/pr-7-${HEAD}`

// Serves GETs from $FIX (one file per endpoint), logs every call to $LOG, and accepts status posts and merges.
const stub = `#!/usr/bin/env bash
echo "$*" >>"$LOG"
if [ "$1" = pr ]; then exit 0; fi
jq_q=''; path=''; method=GET
shift
while [ $# -gt 0 ]; do
  case $1 in
    --paginate) ;;
    --jq) jq_q=$2; shift ;;
    -X) method=$2; shift ;;
    -f|-F|-H) shift ;;
    *) [ -z "$path" ] && path=$1 ;;
  esac; shift
done
p=\${path%%\\?*}
if [ -n "\${FAIL:-}" ] && [[ $p == *$FAIL* ]]; then echo "stub gh: failing $p" >&2; exit 1; fi
if [ "$method" = POST ]; then echo '{}'; exit 0; fi
case $p in
  repos/*/pulls/*/files) f=files.json ;;
  repos/*/pulls/*/reviews) f=reviews.json ;;
  repos/*/pulls/*) f=pr.json ;;
  repos/*/pulls) f=pulls.json ;;
  repos/*/commits/*/statuses) f=statuses.json ;;
  repos/*/commits/*/status) f=status.json ;;
  repos/*/commits/*/check-suites) f=suites.json ;;
  repos/*/compare/*) f=compare.json ;;
  repos/*/issues/*) f=empty.json ;;
  *) echo "stub gh: unknown $path" >&2; exit 1 ;;
esac
if [ -f "$FIX/$f" ]; then d=$(cat "$FIX/$f")
else case $f in status.json) d='{"statuses":[]}';; suites.json) d='{"check_suites":[]}';; *) d='[]';; esac; fi
if [ -n "$jq_q" ]; then jq -r "$jq_q" <<<"$d"; else printf '%s\\n' "$d"; fi
`

const prRecord = (o = {}) => ({
  number: 7, state: 'open', draft: false, user: {login: 'ctwoodwa'}, labels: [], auto_merge: null,
  head: {sha: HEAD, repo: {full_name: 'o/r'}}, base: {ref: 'main', sha: 'b1', repo: {default_branch: 'main'}},
  changed_files: 1, commits: 1, ...o,
})
const gateStatus = (state = 'success', creator = 'github-actions[bot]', description = 'Passed by Codex') =>
  ({context: 'review-gate', state, creator: {login: creator}, description})

function run(script, {args = [], env = {}, fixtures = {}, fail} = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'review-gate-am-'))
  try {
    const bin = join(dir, 'bin'); const fix = join(dir, 'fix'); mkdirSync(bin); mkdirSync(fix)
    writeFileSync(join(bin, 'gh'), stub); chmodSync(join(bin, 'gh'), 0o755)
    for (const [name, value] of Object.entries(fixtures)) writeFileSync(join(fix, name), JSON.stringify(value))
    const log = join(dir, 'log'); const out = join(dir, 'out'); writeFileSync(log, ''); writeFileSync(out, '')
    const r = spawnSync('bash', [join(here, script), ...args], {encoding: 'utf8', env: {
      ...process.env, PATH: `${bin}:${process.env.PATH}`, FIX: fix, LOG: log, FAIL: fail ?? '',
      GITHUB_REPOSITORY: 'o/r', GITHUB_OUTPUT: out, GITHUB_STEP_SUMMARY: '', DRY_RUN: '', ...env}})
    const calls = readFileSync(log, 'utf8').split('\n').filter(Boolean)
    const outputs = Object.fromEntries(readFileSync(out, 'utf8').split('\n').filter(Boolean).map(l => l.split('=')))
    return {code: r.status, out: r.stdout + r.stderr, calls, outputs}
  } finally { rmSync(dir, {recursive: true, force: true}) }
}

// The one status a queue run posted on the group's commit: [state, description].
function groupStatus(r) {
  const posts = r.calls.filter(c => c.includes(`statuses/${GROUP}`))
  assert.equal(posts.length, 1, r.out)
  return [/state=(\w+)/.exec(posts[0])[1], /description=(.*?) -f target_url/.exec(posts[0])[1]]
}
const queue = (fixtures, ref = REF, fail) => run('review-gate-queue.sh',
  {env: {MERGE_GROUP_SHA: GROUP, MERGE_GROUP_REF: ref}, fixtures, fail})

test('queue: a pass the gate workflow set on the PR head carries to the group commit', () => {
  const r = queue({'pr.json': prRecord(), 'statuses.json': [gateStatus()]})
  assert.equal(r.code, 0, r.out)
  assert.deepEqual(groupStatus(r), ['success', 'Carried from PR #7 at aaaaaaa: Passed by Codex'])
})

test('queue: a pass posted from a personal token never carries', () => {
  const r = queue({'pr.json': prRecord(), 'statuses.json': [gateStatus('success', 'ctwoodwa')]})
  assert.equal(groupStatus(r)[0], 'failure', r.out)
})

test('queue: only the newest review-gate status counts', () => {
  const r = queue({'pr.json': prRecord(), 'statuses.json': [gateStatus('pending'), gateStatus('success')]})
  assert.deepEqual(groupStatus(r), ['failure', "PR #7's review-gate on aaaaaaa is pending"])
})

test('queue: no review-gate on the head, a moved head or a closed PR fails the group', () => {
  assert.equal(groupStatus(queue({'pr.json': prRecord(), 'statuses.json': []}))[0], 'failure')
  assert.equal(groupStatus(queue({'pr.json': prRecord({head: {sha: 'b'.repeat(40), repo: {full_name: 'o/r'}}}),
    'statuses.json': [gateStatus()]}))[0], 'failure')
  assert.equal(groupStatus(queue({'pr.json': prRecord({state: 'closed'}), 'statuses.json': [gateStatus()]}))[0], 'failure')
})

test('queue: a ref that is not a merge queue ref fails without reading any PR', () => {
  for (const ref of ['refs/heads/main', `refs/heads/gh-readonly-queue/main/pr-7-${'a'.repeat(39)}`,
    `refs/heads/feature/pr-7-${HEAD}`]) {
    const r = queue({'pr.json': prRecord(), 'statuses.json': [gateStatus()]}, ref)
    assert.equal(groupStatus(r)[0], 'failure', ref)
    assert.ok(!r.calls.some(c => c.includes('/pulls/')), ref)
  }
})

test('queue: a base branch with slashes still parses', () => {
  const r = queue({'pr.json': prRecord(), 'statuses.json': [gateStatus()]},
    `refs/heads/gh-readonly-queue/release/2026/pr-7-${HEAD}`)
  assert.equal(groupStatus(r)[0], 'success', r.out)
})

test('queue: a failed lookup marks the group errored, never passed', () => {
  const r = queue({'pr.json': prRecord(), 'statuses.json': [gateStatus()]}, REF, '/statuses')
  assert.notEqual(r.code, 0)
  assert.equal(groupStatus(r)[0], 'error', r.out)
})

const arm = (fixtures, mode, fail) => run('review-gate-arm.sh',
  {args: ['7', HEAD], env: mode ? {MODE: mode} : {}, fixtures, fail})
const merged = r => r.calls.filter(c => c.startsWith('pr merge'))

test('arm: watch-only by default reports "would arm" and merges nothing', () => {
  const r = arm({'pr.json': prRecord(), 'statuses.json': [gateStatus()]})
  assert.equal(r.code, 0, r.out)
  assert.match(r.out, /would arm auto-merge for PR #7 at aaaaaaa/)
  assert.deepEqual(merged(r), [])
})

test('arm: arm mode turns on auto-merge for exactly the head that passed', () => {
  const r = arm({'pr.json': prRecord(), 'statuses.json': [gateStatus()]}, 'arm')
  assert.equal(r.code, 0, r.out)
  assert.deepEqual(merged(r), [`pr merge 7 --repo o/r --auto --squash --match-head-commit ${HEAD}`])
})

test('arm: an unknown mode fails before reading anything', () => {
  const r = arm({'pr.json': prRecord(), 'statuses.json': [gateStatus()]}, 'yes')
  assert.notEqual(r.code, 0)
  assert.deepEqual(r.calls, [])
})

test('arm: each disqualifier keeps the PR manual, even in arm mode', () => {
  const cases = {
    'not open': [prRecord({state: 'closed'})],
    'a draft': [prRecord({draft: true})],
    'the head moved': [prRecord({head: {sha: 'b'.repeat(40), repo: {full_name: 'o/r'}}})],
    'from a fork': [prRecord({head: {sha: HEAD, repo: {full_name: 'x/r'}}})],
    'not into the default branch': [prRecord({base: {ref: 'next', sha: 'b1', repo: {default_branch: 'main'}}})],
    'a Dependabot PR': [prRecord({user: {login: 'dependabot[bot]'}})],
    'labelled stacked': [prRecord({labels: [{name: 'stacked'}]})],
    'labelled no-auto-merge': [prRecord({labels: [{name: 'docs'}, {name: 'no-auto-merge'}]})],
    'auto-merge is already on': [prRecord({auto_merge: {merge_method: 'squash'}})],
    'no review-gate on this head': [prRecord(), []],
    'not set by the gate workflow': [prRecord(), [gateStatus('success', 'ctwoodwa')]],
    'review-gate on this head is pending': [prRecord(), [gateStatus('pending'), gateStatus('success')]],
  }
  for (const [why, [pr, statuses = [gateStatus()]]] of Object.entries(cases)) {
    const r = arm({'pr.json': pr, 'statuses.json': statuses}, 'arm')
    assert.equal(r.code, 0, `${why}: ${r.out}`)
    assert.match(r.out, new RegExp(`stays manual: .*${why}`), why)
    assert.deepEqual(merged(r), [], why)
  }
})

test('arm: a failed lookup stops the script without merging', () => {
  const r = arm({'pr.json': prRecord(), 'statuses.json': [gateStatus()]}, 'arm', '/statuses')
  assert.notEqual(r.code, 0)
  assert.deepEqual(merged(r), [])
})

test('gate: review-gate.sh hands its verdict to the auto-merge job only once the status is posted', () => {
  const dir = mkdtempSync(join(tmpdir(), 'review-gate-lists-'))
  try {
    writeFileSync(join(dir, 'risky'), 'scripts/release/\n')
    writeFileSync(join(dir, 'exempt'), 'SUPPORT.md\n')
    const lists = {RISKY_LIST: join(dir, 'risky'), EXEMPT_LIST: join(dir, 'exempt'), NO_GRAPHQL: '1'}
    const fixtures = files => ({'pr.json': prRecord({changed_files: files.length}),
      'pulls.json': [{number: 7, head: {sha: HEAD}}],
      'files.json': files.map(filename => ({filename})),
      'compare.json': {files: files.map(filename => ({filename})), commits: [], total_commits: 1}})
    const exempt = run('review-gate.sh', {args: ['7'], env: lists, fixtures: fixtures(['SUPPORT.md'])})
    assert.equal(exempt.code, 0, exempt.out)
    assert.deepEqual(exempt.outputs, {state: 'success', risky: 'false', head: HEAD})
    const risky = run('review-gate.sh', {args: ['7'], env: lists, fixtures: fixtures(['scripts/release/sign.sh'])})
    assert.deepEqual(risky.outputs, {state: 'pending', risky: 'true', head: HEAD})
    const dry = run('review-gate.sh', {args: ['7'], env: {...lists, DRY_RUN: '1'}, fixtures: fixtures(['SUPPORT.md'])})
    assert.deepEqual(dry.outputs, {}, 'a dry run hands nothing on')
    assert.ok(existsSync(join(here, 'review-gate.sh')))
  } finally { rmSync(dir, {recursive: true, force: true}) }
})
