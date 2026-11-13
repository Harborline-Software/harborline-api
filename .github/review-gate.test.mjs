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
    -X|-f|-F|-H) shift ;;
    *) [ -z "$path" ] && path=$1 ;;
  esac; shift
done
p=\${path%%\\?*}
if [ -n "\${FAIL:-}" ] && [[ $p == *$FAIL* ]]; then echo "stub gh: failing $p" >&2; exit 1; fi
case $p in
  repos/*/contents/.github/*)
    f=base-\${p##*/}; [ -f "$FIX/$f" ] || { echo "stub gh: HTTP 404 $p" >&2; exit 1; }
    cat "$FIX/$f"; exit 0 ;;
  repos/*/rules/branches/*) f=rules.json ;;
  repos/*/compare/*) f=compare.json ;;
  repos/*/pulls/*/files) f=files.json ;;
  repos/*/pulls/*/commits) f=commits.json ;;
  repos/*/pulls/*/reviews) f=reviews.json ;;
  repos/*/pulls/*) f=pr.json ;;
  repos/*/pulls) f=pulls.json ;;
  repos/*/issues/*/events) f=events.json ;;
  repos/*/issues/*/comments) f=comments.json ;;
  repos/*/commits/*/statuses) f=statuses.json ;;
  repos/*/commits/*/check-suites) f=suites.json ;;
  *) echo "stub gh: unknown $path" >&2; exit 1 ;;
esac
if [ -f "$FIX/$f" ]; then d=$(cat "$FIX/$f")
else case $f in suites.json) d='{"check_suites":[]}';; *) d='[]';; esac; fi
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

// base is the PR's base branch (the default branch is main). rules is that branch's rulesets, and baseLists the
// lists it carries ({risky, exempt}), served through the contents API. The env lists stand for main's checkout.
// snapshotFiles and totalCommits are what GitHub's comparison of the PR's base and head commits holds; by default
// it agrees with the PR's own listings.
function runGate({user = 'ctwoodwa', files, commits, commitCount = commits?.length ?? 0, comments, reviews, status, fail,
                  base = 'main', rules, baseLists = {}, snapshotFiles = files, totalCommits = commitCount}) {
  const dir = mkdtempSync(join(tmpdir(), 'review-gate-'))
  try {
    const bin = join(dir, 'bin'); const fix = join(dir, 'fix'); mkdirSync(bin); mkdirSync(fix)
    writeFileSync(join(bin, 'gh'), stub); chmodSync(join(bin, 'gh'), 0o755)
    const pr = {state: 'open', draft: false, head: {sha: 'h1', repo: {full_name: 'o/r'}},
                base: {ref: base, sha: 'b1', repo: {default_branch: 'main'}},
                user: {login: user}, changed_files: files.length, commits: commitCount}
    writeFileSync(join(fix, 'pr.json'), JSON.stringify(pr))
    writeFileSync(join(fix, 'pulls.json'), JSON.stringify([{number: 1, head: {sha: 'h1'}}]))
    writeFileSync(join(fix, 'files.json'), JSON.stringify(files.map(filename => ({filename}))))
    writeFileSync(join(fix, 'compare.json'), JSON.stringify({files: snapshotFiles.map(filename => ({filename})),
      commits: commits ?? [], total_commits: totalCommits}))
    if (comments) writeFileSync(join(fix, 'comments.json'), JSON.stringify(comments))
    if (reviews) writeFileSync(join(fix, 'reviews.json'), JSON.stringify(reviews))
    // Statuses as the listing returns them, newest first; each was set by the workflows unless a case says otherwise.
    if (status) writeFileSync(join(fix, 'statuses.json'),
      JSON.stringify(status.map(s => ({creator: {login: 'github-actions[bot]'}, ...s}))))
    if (rules) writeFileSync(join(fix, 'rules.json'), JSON.stringify(rules))
    if (baseLists.risky != null) writeFileSync(join(fix, 'base-review-risky-paths'), baseLists.risky)
    if (baseLists.exempt != null) writeFileSync(join(fix, 'base-review-exempt-paths'), baseLists.exempt)
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

test('an agent instruction file inside an exempt folder, at any depth or casing, means a review', () => {
  for (const f of ['docs/guide/AGENTS.md', 'docs/guide/deep/er/agents.md', 'docs/guide/AGENTS.override.md',
    'docs/guide/x/CLAUDE.md', 'docs/guide/CLAUDE.local.md']) {
    assert.equal(runGate({files: ['SUPPORT.md', f]}).desc, NEEDS_REVIEW, f)
  }
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

// Codex's summary comment, with each review's verdict on head h1. A review given as null didn't run.
const codexState = ({code = null, security = null}) => {
  const review = v => v == null ? '&#123;"commit_sha":null,"status":null&#125;'
    : `&#123;"commit_sha":"h1","has_any_findings":${v === 'dirty'},"has_unresolved_findings":${v === 'dirty'},"status":"completed"&#125;`
  return [{user: {login: 'chatgpt-codex-connector[bot]'},
    body: `<!-- codex-review-state:v1 &#123;"code_review":${review(code)},"security_review":${review(security)}&#125; -->`}]
}
const claudeSays = (verdict, base = 'main') => [{context: 'claude-review', state: verdict === 'PASS' ? 'success' : 'failure',
  updated_at: '2026-11-02T00:00:00Z', description: `Claude review: ${verdict} (shadow, base ${base})`}]

// ADR-0159: a Claude BLOCK holds the PR; an ESCALATE makes it risky, so it passes once Codex's security review is clean
// and one reviewer passed it.
test('a Claude BLOCK blocks even when Codex passed both reviews', () => {
  const g = runGate({files: ['src/App.cs'], status: claudeSays('BLOCK'), comments: codexState({code: 'clean', security: 'clean'})})
  assert.equal(g.state, 'failure', g.out)
  assert.equal(g.desc, 'Blocked: Claude review: BLOCK (shadow, base main)')
})

test('a Claude ESCALATE with no other review waits for one clean review', () => {
  const g = runGate({files: ['src/App.cs'], status: claudeSays('ESCALATE')})
  assert.equal(g.state, 'pending', g.out)
  assert.equal(g.desc, NEEDS_REVIEW)
  assert.match(g.out, /risky=true/)
})

test('a Claude ESCALATE passes once Codex passes both its code and security reviews', () => {
  const g = runGate({files: ['src/App.cs'], status: claudeSays('ESCALATE'), comments: codexState({code: 'clean', security: 'clean'})})
  assert.equal(g.state, 'success', g.out)
  assert.equal(g.desc, 'Passed by Codex; Claude escalated, Codex security review clean')
  // risky=true is what keeps the auto-merge job from arming an escalated PR.
  assert.match(g.out, /risky=true/)
})

test('a Claude ESCALATE with a clean pass but no clean Codex security review waits for that review', () => {
  // CodeRabbit's approval on this head is the clean pass; Codex's security review hasn't run.
  const approved = [{user: {login: 'coderabbitai[bot]'}, commit_id: 'h1', state: 'APPROVED', submitted_at: '2026-11-02T00:00:00Z'}]
  const g = runGate({files: ['src/App.cs'], status: claudeSays('ESCALATE'), reviews: approved})
  assert.equal(g.state, 'pending', g.out)
  assert.equal(g.desc, 'Passed by CodeRabbit; a risky change also needs a clean Codex security review')
  // With it clean, the approval carries the PR.
  const h = runGate({files: ['src/App.cs'], status: claudeSays('ESCALATE'), reviews: approved,
    comments: codexState({security: 'clean'})})
  assert.equal(h.state, 'success', h.out)
  assert.equal(h.desc, 'Passed by CodeRabbit; Claude escalated, Codex security review clean')
  assert.match(h.out, /risky=true/)
})

test('a Claude ESCALATE with open Codex security findings blocks', () => {
  const g = runGate({files: ['src/App.cs'], status: claudeSays('ESCALATE'), comments: codexState({code: 'clean', security: 'dirty'})})
  assert.equal(g.state, 'failure', g.out)
  assert.equal(g.desc, 'Blocked: Codex security review has open findings')
})

// Claude review on Platform #492: an ESCALATE run that can't read the earlier verdict posts HELD rather than nothing.
test('a Claude HELD blocks even when Codex passed both reviews', () => {
  const g = runGate({files: ['src/App.cs'], status: claudeSays('HELD'), comments: codexState({code: 'clean', security: 'clean'})})
  assert.equal(g.state, 'failure', g.out)
  assert.equal(g.desc, 'Blocked: Claude review: HELD (shadow, base main)')
})

// Fable risky-change pass on Platform #492: the description carries the base branch, so the verdict is read by position.
test('a Claude BLOCK into a branch whose name contains ESCALATE still blocks', () => {
  const base = {base: 'release/ESCALATE-fix', rules: [{type: 'pull_request'}], baseLists: {risky: 'scripts/release/\n'}}
  const g = runGate({...base, files: ['src/App.cs'], status: claudeSays('BLOCK', 'release/ESCALATE-fix'),
    comments: codexState({code: 'clean', security: 'clean'})})
  assert.equal(g.state, 'failure', g.out)
  assert.equal(g.desc, 'Blocked: Claude review: BLOCK (shadow, base release/ESCALATE-fix)')
  assert.match(g.out, /risky=false/)
  // A PASS into that branch is a pass, not an escalation.
  const p = runGate({...base, files: ['src/App.cs'], status: claudeSays('PASS', 'release/ESCALATE-fix')})
  assert.equal(p.state, 'success', p.out)
  assert.equal(p.desc, 'Passed by Claude')
  assert.match(p.out, /risky=false/)
})

test('a Claude verdict set by anyone but the review workflow blocks', () => {
  for (const verdict of ['ESCALATE', 'PASS']) {
    const g = runGate({files: ['src/App.cs'], comments: codexState({code: 'clean', security: 'clean'}),
      status: claudeSays(verdict).map(s => ({...s, creator: {login: 'ctwoodwa'}}))})
    assert.equal(g.state, 'failure', `${verdict}: ${g.out}`)
    assert.equal(g.desc, 'Blocked: claude-review was not set by the review workflow', verdict)
  }
  // Only the newest claude-review counts: a workflow BLOCK set after a forged PASS still blocks as a BLOCK.
  const later = runGate({files: ['src/App.cs'], status: [...claudeSays('BLOCK'),
    ...claudeSays('PASS').map(s => ({...s, creator: {login: 'ctwoodwa'}}))]})
  assert.equal(later.desc, 'Blocked: Claude review: BLOCK (shadow, base main)', later.out)
})

test('a Claude ESCALATE on an exempt-only change still waits for a review', () => {
  const g = runGate({files: ['SUPPORT.md'], status: claudeSays('ESCALATE')})
  assert.equal(g.state, 'pending', g.out)
  assert.equal(g.desc, NEEDS_REVIEW)
  assert.match(g.out, /risky=true/)
})

test('a Claude ESCALATE needs a clean Codex security review, not one with resolved findings', () => {
  // Resolved findings are not a clean review: CodeRabbit's pass stands, but the security review is still owed.
  const approved = [{user: {login: 'coderabbitai[bot]'}, commit_id: 'h1', state: 'APPROVED', submitted_at: '2026-11-02T00:00:00Z'}]
  const resolved = codexState({security: 'clean'})
  resolved[0].body = resolved[0].body.replace('"has_any_findings":false', '"has_any_findings":true')
  const g = runGate({files: ['src/App.cs'], status: claudeSays('ESCALATE'), reviews: approved, comments: resolved})
  assert.equal(g.state, 'pending', g.out)
  assert.equal(g.desc, 'Passed by CodeRabbit; a risky change also needs a clean Codex security review')
})

test('a Claude failure with no verdict in its description still blocks', () => {
  const g = runGate({files: ['src/App.cs'], comments: codexState({code: 'clean', security: 'clean'}),
    status: [{context: 'claude-review', state: 'failure', updated_at: '2026-11-02T00:00:00Z', description: ''}]})
  assert.equal(g.state, 'failure', g.out)
  assert.equal(g.desc, 'Blocked: Claude review: not PASS')
})

test('a PR into a protected branch other than the default gets no exemption, but is judged by its risky list', () => {
  // Codex security finding on Platform #398: a pass sits on the head commit, so one minted against one protected base
  // would stand after a retarget to another. Only the default branch grants one.
  const protectedBase = {base: 'release', rules: [{type: 'pull_request'}],
                         baseLists: {risky: 'docs/guide/\n', exempt: 'docs/release-notes/\n'}}
  const g = runGate({...protectedBase, files: ['docs/release-notes/1.2.md']})
  assert.equal(g.desc, NEEDS_REVIEW, g.out)
  assert.match(g.out, /no review exemption: base release is not main/)
  assert.match(g.out, /risky=false/)
  // main's exempt list doesn't apply there either.
  assert.equal(runGate({...protectedBase, files: ['SUPPORT.md']}).desc, NEEDS_REVIEW)
  const bot = runGate({...protectedBase, user: 'dependabot[bot]', files: ['src/App.csproj'], commits: [dependabotCommit()]})
  assert.equal(bot.desc, NEEDS_REVIEW, bot.out)
  // The base's own risky list still applies.
  assert.match(runGate({...protectedBase, files: ['docs/guide/setup.md']}).out, /risky=true/)
})

test('a PR into an unprotected branch gets no exemption and is judged by main\'s risky list', () => {
  // Codex security finding on App #88: a pass minted against an unprotected base stays on the head after a retarget.
  const opts = {base: 'feature', rules: [], baseLists: {risky: 'docs/guide/\n', exempt: 'src/\n'}}
  const doc = runGate({...opts, files: ['docs/guide/setup.md']})
  assert.equal(doc.desc, NEEDS_REVIEW, doc.out)
  assert.match(doc.out, /risky=false/)
  assert.equal(runGate({...opts, files: ['src/App.cs']}).desc, NEEDS_REVIEW)
  const bot = runGate({...opts, user: 'dependabot[bot]', files: ['src/App.csproj'], commits: [dependabotCommit()]})
  assert.equal(bot.desc, NEEDS_REVIEW, bot.out)
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
    'one commit with no update-type': [dependabotCommit(), dependabotCommit('Bump x (security)')],
  }
  for (const [name, commits] of Object.entries(cases)) {
    assert.equal(runGate({user: 'dependabot[bot]', files: ['src/App.csproj'], commits}).desc, NEEDS_REVIEW, name)
  }
})

test('a Dependabot PR with more commits than GitHub listed means a review', () => {
  // The commits listing stops at 250; an unlisted commit could be anyone's.
  const commits = Array.from({length: 250}, () => dependabotCommit())
  assert.equal(runGate({user: 'dependabot[bot]', files: ['src/App.csproj'], commits}).state, 'success')
  const g = runGate({user: 'dependabot[bot]', files: ['src/App.csproj'], commits, commitCount: 251})
  assert.equal(g.desc, NEEDS_REVIEW, g.out)
  const s = runGate({user: 'dependabot[bot]', files: ['src/App.csproj'], commits, totalCommits: 251})
  assert.equal(s.desc, NEEDS_REVIEW, s.out)
})

test('a PR not opened by Dependabot gets no Dependabot exemption', () => {
  assert.equal(runGate({files: ['src/App.csproj'], commits: [dependabotCommit()]}).desc, NEEDS_REVIEW)
})

test('an exemption is judged from the base and head commits, so a listing that doesn\'t match them grants none', () => {
  // Codex security finding on App #88: the files are listed by PR number after the head is read, so a push in between
  // could swap them. GitHub's comparison of the recorded commits must list the same files.
  const docs = ['docs/guide/setup.md']
  assert.equal(runGate({files: docs}).state, 'success')
  const swapped = runGate({files: docs, snapshotFiles: [...docs, 'src/App.cs']})
  assert.equal(swapped.desc, NEEDS_REVIEW, swapped.out)
  assert.match(swapped.out, /don't match the comparison/)
  assert.equal(runGate({files: docs, snapshotFiles: ['src/App.cs']}).desc, NEEDS_REVIEW)
  const failed = runGate({files: docs, fail: 'compare'})
  assert.equal(failed.code, 0, failed.out)
  assert.equal(failed.desc, NEEDS_REVIEW, failed.out)
  assert.match(failed.out, /could not compare/)
  const bot = runGate({user: 'dependabot[bot]', files: ['src/App.csproj'], commits: [dependabotCommit()], fail: 'compare'})
  assert.equal(bot.desc, NEEDS_REVIEW, bot.out)
})

test('a PR bigger than the comparison can list gets no exemption', () => {
  const docs = n => Array.from({length: n}, (_, i) => `docs/guide/page${i}.md`)
  assert.equal(runGate({files: docs(300)}).state, 'success')
  const big = runGate({files: docs(301)})
  assert.equal(big.state, 'pending', big.out)
  assert.match(big.desc, /^Waiting for a clean review/)
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
