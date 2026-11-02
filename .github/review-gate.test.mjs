// Tests for review-gate.sh's "review by risk" rules. Each case runs the real script in DRY_RUN mode against a stub
// `gh` that serves one PR's record from fixture files, then checks the status it would set.
// Run: node --test .github/review-gate.test.mjs
import {test} from 'node:test'
import assert from 'node:assert/strict'
import {mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, chmodSync} from 'node:fs'
import {tmpdir} from 'node:os'
import {join} from 'node:path'
import {spawnSync} from 'node:child_process'

const script = new URL('./review-gate.sh', import.meta.url).pathname

// Serves repos/... reads from $FIX, one file per endpoint; a missing file reads as an empty listing. A path that
// matches $FAIL fails, like a GitHub API error.
const stub = `#!/usr/bin/env bash
jq_q=''; path=''
while [ $# -gt 0 ]; do
  case $1 in
    api|--paginate) ;;
    --jq) jq_q=$2; shift ;;
    -X|-f|-F) shift ;;
    *) [ -z "$path" ] && path=$1 ;;
  esac; shift
done
p=\${path%%\\?*}
if [ -n "\${FAIL:-}" ] && [[ $p == *$FAIL* ]]; then echo "stub gh: failing $p" >&2; exit 1; fi
case $p in
  repos/*/pulls/*/files) f=files.json ;;
  repos/*/pulls/*/commits) f=commits.json ;;
  repos/*/pulls/*/reviews) f=reviews.json ;;
  repos/*/pulls/*) f=pr.json ;;
  repos/*/pulls) f=pulls.json ;;
  repos/*/issues/*/events) f=events.json ;;
  repos/*/issues/*/comments) f=comments.json ;;
  repos/*/commits/*/status) f=status.json ;;
  repos/*/commits/*/check-suites) f=suites.json ;;
  *) echo "stub gh: unknown $path" >&2; exit 1 ;;
esac
if [ -f "$FIX/$f" ]; then d=$(cat "$FIX/$f")
else case $f in status.json) d='{"statuses":[]}';; suites.json) d='{"check_suites":[]}';; *) d='[]';; esac; fi
if [ -n "$jq_q" ]; then jq -r "$jq_q" <<<"$d"; else printf '%s\\n' "$d"; fi
`

const dependabotMessage = (...types) => 'Bump the minor-and-patch group\n\n---\nupdated-dependencies:\n' +
  types.map((t, i) => `- dependency-name: Dep${i}\n  dependency-type: direct:production\n  update-type: ${t}\n`).join('') +
  '...\n\nSigned-off-by: dependabot[bot] <support@github.com>'

// A Dependabot commit as GitHub returns it: authored by dependabot[bot], committed and signed by GitHub (web-flow).
const dependabotCommit = (message = dependabotMessage('version-update:semver-patch'), overrides = {}) => ({
  author: {login: overrides.author ?? 'dependabot[bot]'},
  committer: {login: overrides.committer ?? 'web-flow'},
  commit: {message, verification: {verified: overrides.verified ?? true}},
})

function runGate({user = 'ctwoodwa', files, commits, comments, status, fail}) {
  const dir = mkdtempSync(join(tmpdir(), 'review-gate-'))
  try {
    const bin = join(dir, 'bin'); const fix = join(dir, 'fix'); mkdirSync(bin); mkdirSync(fix)
    writeFileSync(join(bin, 'gh'), stub); chmodSync(join(bin, 'gh'), 0o755)
    const pr = {state: 'open', draft: false, head: {sha: 'h1', repo: {full_name: 'o/r'}}, base: {ref: 'main'},
                user: {login: user}, changed_files: files.length}
    writeFileSync(join(fix, 'pr.json'), JSON.stringify(pr))
    writeFileSync(join(fix, 'pulls.json'), JSON.stringify([{number: 1, head: {sha: 'h1'}}]))
    writeFileSync(join(fix, 'files.json'), JSON.stringify(files.map(filename => ({filename}))))
    if (commits) writeFileSync(join(fix, 'commits.json'), JSON.stringify(commits))
    if (comments) writeFileSync(join(fix, 'comments.json'), JSON.stringify(comments))
    if (status) writeFileSync(join(fix, 'status.json'), JSON.stringify({statuses: status}))
    // Fixed lists, so the cases don't depend on what this repo's own lists hold today.
    writeFileSync(join(dir, 'risky'), 'Directory.Packages.props\npackage.json\nscripts/release/\n')
    writeFileSync(join(dir, 'exempt'), '# docs that nothing builds or gates on\nSUPPORT.md\ndocs/guide/\n!docs/guide/evidence.md\n')
    const r = spawnSync('bash', [script, '1'], {encoding: 'utf8', env: {
      ...process.env, PATH: `${bin}:${process.env.PATH}`, FIX: fix, FAIL: fail ?? '',
      GITHUB_REPOSITORY: 'o/r', DRY_RUN: '1', NO_GRAPHQL: '1',
      RISKY_LIST: join(dir, 'risky'), EXEMPT_LIST: join(dir, 'exempt')}})
    const m = /^PR 1 head h1: (\w+): (.*) \(claude=/m.exec(r.stdout)
    return {code: r.status, state: m?.[1], desc: m?.[2], out: r.stdout + r.stderr}
  } finally { rmSync(dir, {recursive: true, force: true}) }
}

const NEEDS_REVIEW = 'Waiting for one clean review from Claude, Codex or CodeRabbit'

test('a change only to exempt docs needs no review', () => {
  const g = runGate({files: ['SUPPORT.md', 'docs/guide/setup.md']})
  assert.equal(g.state, 'success', g.out)
  assert.equal(g.desc, 'No review needed: every change is on the review-exempt list')
})

test('one file off the exempt list, or one taken back out with !, means a review', () => {
  assert.equal(runGate({files: ['docs/guide/setup.md', 'src/App.cs']}).desc, NEEDS_REVIEW)
  assert.equal(runGate({files: ['docs/guide/evidence.md']}).desc, NEEDS_REVIEW)
})

test('the exemption never applies to a risky path', () => {
  for (const f of ['.github/review-exempt-paths', '.github/workflows/verify.yml', 'scripts/release/sign.sh']) {
    const g = runGate({files: ['SUPPORT.md', f]})
    assert.equal(g.state, 'pending', `${f}: ${g.out}`)
    assert.equal(g.desc, NEEDS_REVIEW, f)
  }
})

test('a blocker still blocks an exempt change', () => {
  const g = runGate({files: ['SUPPORT.md'],
    status: [{context: 'claude-review', state: 'failure', updated_at: '2026-11-02T00:00:00Z', description: 'Claude review: BLOCK'}]})
  assert.equal(g.state, 'failure', g.out)
  assert.match(g.desc, /^Blocked: /)
})

test('a Dependabot patch or minor update of a file that is not risky needs no review', () => {
  const g = runGate({user: 'dependabot[bot]', files: ['src/App.csproj'],
    commits: [dependabotCommit(dependabotMessage('version-update:semver-patch', 'version-update:semver-minor'))]})
  assert.equal(g.state, 'success', g.out)
  assert.equal(g.desc, 'No review needed: a Dependabot patch or minor update')
})

test('a Dependabot bump of a risky path needs a review and the security review, even with Codex security clean', () => {
  for (const f of ['.github/workflows/verify.yml', 'Directory.Packages.props', 'package.json']) {
    const g = runGate({user: 'dependabot[bot]', files: [f], commits: [dependabotCommit()]})
    assert.equal(g.desc, NEEDS_REVIEW, `${f}: ${g.out}`)
  }
  const clean = '<!-- codex-review-state:v1 &#123;"code_review":&#123;"commit_sha":null,"status":null&#125;,' +
    '"security_review":&#123;"commit_sha":"h1","has_any_findings":false,"has_unresolved_findings":false,"status":"completed"&#125;&#125; -->'
  const g = runGate({user: 'dependabot[bot]', files: ['.github/workflows/verify.yml'], commits: [dependabotCommit()],
    comments: [{user: {login: 'chatgpt-codex-connector[bot]'}, body: clean}]})
  assert.equal(g.state, 'pending', g.out)
  assert.equal(g.desc, NEEDS_REVIEW)
})

test('a commit someone else pushed under Dependabot\'s name means a review', () => {
  // Signed with the pusher's own verified key: Dependabot is still the author, but GitHub is not the committer.
  const g = runGate({user: 'dependabot[bot]', files: ['src/App.csproj'],
    commits: [dependabotCommit(), dependabotCommit(undefined, {committer: 'someone'})]})
  assert.equal(g.desc, NEEDS_REVIEW, g.out)
})

test('an unverified, human-written, major or untyped Dependabot update means a review', () => {
  const cases = {
    unverified: [dependabotCommit(undefined, {verified: false})],
    'extra human commit': [dependabotCommit(), {author: {login: 'ctwoodwa'}, committer: {login: 'ctwoodwa'},
                                                commit: {message: 'fix', verification: {verified: true}}}],
    major: [dependabotCommit(dependabotMessage('version-update:semver-patch', 'version-update:semver-major'))],
    'no update-type': [dependabotCommit('Bump x (security)')],
  }
  for (const [name, commits] of Object.entries(cases)) {
    assert.equal(runGate({user: 'dependabot[bot]', files: ['src/App.csproj'], commits}).desc, NEEDS_REVIEW, name)
  }
})

test('a PR not opened by Dependabot gets no Dependabot exemption', () => {
  assert.equal(runGate({files: ['src/App.csproj'], commits: [dependabotCommit()]}).desc, NEEDS_REVIEW)
})

test('a failed commit listing stops the gate instead of reading as empty', () => {
  const g = runGate({user: 'dependabot[bot]', files: ['src/App.csproj'], commits: [dependabotCommit()], fail: 'pulls/1/commits'})
  assert.notEqual(g.code, 0, g.out)
  assert.equal(g.state, undefined)
  assert.match(g.out, /could not list the PR's commits/)
})

test('nothing on this repo\'s exempt list is risky', () => {
  const read = name => readFileSync(new URL(name, import.meta.url), 'utf8').split('\n')
    .map(l => l.replace(/#.*/, '').trim()).filter(Boolean)
  const exempt = read('./review-exempt-paths').filter(l => !l.startsWith('!'))
  const risky = read('./review-risky-paths')
  const pattern = new RegExp(/^RISKY=\$\{RISKY_PATHS:-'([^']+)'\}/m.exec(readFileSync(script, 'utf8'))[1])
  for (const e of exempt) {
    assert.ok(!pattern.test(e), `${e} matches the risky pattern`)
    for (const r of risky) assert.ok(!(e === r || (r.endsWith('/') && e.startsWith(r)) || (e.endsWith('/') && r.startsWith(e))),
      `${e} overlaps risky ${r}`)
  }
})
