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
// matches $FAIL fails, like a GitHub API error. A POST is logged to $FIX/posts. The statuses listing is served from
// statuses-after.json, when there is one, from read $AFTER on (default the second): another status landed while the script worked.
// The POST numbered $POST_FAIL fails, logged as FAILED.
const stub = `#!/usr/bin/env bash
jq_q=''; path=''; method=GET; fields=''
while [ $# -gt 0 ]; do
  case $1 in
    api|--paginate) ;;
    --jq) jq_q=$2; shift ;;
    -X) method=$2; shift ;;
    -f) fields="$fields $2"; shift ;;
    -F|-H) shift ;;
    *) [ -z "$path" ] && path=$1 ;;
  esac; shift
done
p=\${path%%\\?*}
if [ -n "\${FAIL:-}" ] && [[ $p == *$FAIL* ]]; then echo "stub gh: failing $p" >&2; exit 1; fi
if [ "$method" = POST ]; then
  n=$(( $(cat "$FIX/posts" 2>/dev/null | wc -l) + 1 ))
  if [ "$n" = "\${POST_FAIL:-0}" ]; then echo "FAILED $p$fields" >>"$FIX/posts"; echo "stub gh: POST failed" >&2; exit 1; fi
  echo "POST $p$fields" >>"$FIX/posts"; exit 0
fi
if [[ $p =~ ^repos/[^/]+/[^/]+/pulls/[0-9]+$ ]]; then
  echo read >>"$FIX/pr-reads"
  if [ -f "$FIX/pr-after.json" ] && [ "$(wc -l <"$FIX/pr-reads")" -ge 2 ]; then cp "$FIX/pr-after.json" "$FIX/pr.json"; fi
fi
if [[ $p == repos/*/commits/*/statuses ]]; then
  echo read >>"$FIX/status-reads"
  if [ -f "$FIX/statuses-after.json" ] && [ "$(wc -l <"$FIX/status-reads")" -ge "\${AFTER:-2}" ]; then cp "$FIX/statuses-after.json" "$FIX/statuses.json"; fi
fi
case $p in
  repos/*/contents/.github/*)
    f=base-\${p##*/}; [ -f "$FIX/$f" ] || { echo "stub gh: HTTP 404 $p" >&2; exit 1; }
    cat "$FIX/$f"; exit 0 ;;
  repos/*/rules/branches/*) f=rules.json ;;
  repos/*/collaborators/*/permission) l=\${p#*/collaborators/}; f=role-\${l%/permission}.json ;;
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
// post runs the script for real against the stub, which logs what it would set; statusAfter is the listing the
// stub serves from statuses read afterRead on (default the second).
function runGate({user = 'ctwoodwa', files, commits, commitCount = commits?.length ?? 0, comments, reviews, status, fail,
                  base = 'main', rules, baseLists = {}, snapshotFiles = files, totalCommits = commitCount,
                  post = false, statusAfter, afterRead = 2, postFail = 0, autoMerge = null, autoMergeAfter, events,
                  roles = {ctwoodwa: 'admin'}}) {
  const dir = mkdtempSync(join(tmpdir(), 'review-gate-'))
  try {
    const bin = join(dir, 'bin'); const fix = join(dir, 'fix'); mkdirSync(bin); mkdirSync(fix)
    writeFileSync(join(bin, 'gh'), stub); chmodSync(join(bin, 'gh'), 0o755)
    const pr = {state: 'open', draft: false, head: {sha: 'h1', repo: {full_name: 'o/r'}},
                base: {ref: base, sha: 'b1', repo: {default_branch: 'main'}},
                user: {login: user}, changed_files: files.length, commits: commitCount, auto_merge: autoMerge}
    writeFileSync(join(fix, 'pr.json'), JSON.stringify(pr))
    // autoMergeAfter is the auto-merge state every later read of the PR sees.
    // 'unreadable' serves a broken body, so the read fails.
    if (autoMergeAfter !== undefined) writeFileSync(join(fix, 'pr-after.json'),
      autoMergeAfter === 'unreadable' ? '{' : JSON.stringify({...pr, auto_merge: autoMergeAfter}))
    writeFileSync(join(fix, 'pulls.json'), JSON.stringify([{number: 1, head: {sha: 'h1'}}]))
    writeFileSync(join(fix, 'files.json'), JSON.stringify(files.map(filename => ({filename}))))
    writeFileSync(join(fix, 'compare.json'), JSON.stringify({files: snapshotFiles.map(filename => ({filename})),
      commits: commits ?? [], total_commits: totalCommits}))
    if (comments) writeFileSync(join(fix, 'comments.json'), JSON.stringify(comments))
    if (reviews) writeFileSync(join(fix, 'reviews.json'), JSON.stringify(reviews))
    if (events) writeFileSync(join(fix, 'events.json'), JSON.stringify(events))
    // roles is each person's role on the repository, as GitHub's permission read gives it; anyone not listed can't be read.
    for (const [login, role_name] of Object.entries(roles)) writeFileSync(join(fix, `role-${login}.json`), JSON.stringify({role_name}))
    // Statuses as the listing returns them, newest first; each was set by the workflows unless a case says otherwise.
    const asListed = list => JSON.stringify(list.map(s => ({creator: {login: 'github-actions[bot]'}, ...s})))
    if (status) writeFileSync(join(fix, 'statuses.json'), asListed(status))
    if (statusAfter) writeFileSync(join(fix, 'statuses-after.json'), asListed(statusAfter))
    if (rules) writeFileSync(join(fix, 'rules.json'), JSON.stringify(rules))
    if (baseLists.risky != null) writeFileSync(join(fix, 'base-review-risky-paths'), baseLists.risky)
    if (baseLists.exempt != null) writeFileSync(join(fix, 'base-review-exempt-paths'), baseLists.exempt)
    // Fixed lists, so the cases don't depend on what this repo's own lists hold today.
    writeFileSync(join(dir, 'risky'), 'Directory.Packages.props\npackage.json\nscripts/release/\n')
    writeFileSync(join(dir, 'exempt'), '# docs that nothing builds or gates on\nSUPPORT.md\ndocs/guide/\n!docs/guide/evidence.md\n')
    const r = spawnSync('bash', [script, '1'], {encoding: 'utf8', env: {
      ...process.env, PATH: `${bin}:${process.env.PATH}`, FIX: fix, FAIL: fail ?? '', AFTER: String(afterRead), POST_FAIL: String(postFail),
      GITHUB_REPOSITORY: 'o/r', DRY_RUN: post ? '' : '1', NO_GRAPHQL: '1',
      RISKY_LIST: join(dir, 'risky'), EXEMPT_LIST: join(dir, 'exempt')}})
    const m = /^PR 1 head h1: (\w+): (.*) \(claude=/m.exec(r.stdout)
    let posts = []
    try { posts = readFileSync(join(fix, 'posts'), 'utf8').trim().split('\n') } catch {}
    return {code: r.status, state: m?.[1], desc: m?.[2], posts, out: r.stdout + r.stderr}
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

// ADR-0159 decision 4: an escalated PR lands only when a person queues it. Auto-merge an app turned on before Claude
// escalated would land it anyway, so the gate holds it (Fable on platform 492). GitHub records who turned it on.
const armedBy = (login, type) => ({enabled_by: {login, type}, merge_method: 'squash'})
test('a Claude ESCALATE holds a PR that an app armed for auto-merge', () => {
  const g = runGate({files: ['src/App.cs'], status: claudeSays('ESCALATE'), comments: codexState({code: 'clean', security: 'clean'}),
    autoMerge: armedBy('harborline-merge[bot]', 'Bot')})
  assert.equal(g.state, 'failure', g.out)
  assert.equal(g.desc, 'Blocked: Claude escalated after harborline-merge[bot] armed auto-merge; turn it off and queue the PR yourself')
  // With no record of who turned it on, the PR is held too.
  const h = runGate({files: ['src/App.cs'], status: claudeSays('ESCALATE'), comments: codexState({code: 'clean', security: 'clean'}),
    autoMerge: {merge_method: 'squash'}})
  assert.equal(h.state, 'failure', h.out)
  assert.equal(h.desc, 'Blocked: Claude escalated after an unnamed account armed auto-merge; turn it off and queue the PR yourself')
})

test('a Claude ESCALATE passes when a person turned auto-merge on', () => {
  const g = runGate({files: ['src/App.cs'], status: claudeSays('ESCALATE'), comments: codexState({code: 'clean', security: 'clean'}),
    autoMerge: armedBy('ctwoodwa', 'User')})
  assert.equal(g.state, 'success', g.out)
  assert.equal(g.desc, 'Passed by Codex; Claude escalated, Codex security review clean')
})

// Codex security review on app #98: GitHub lets anyone with write access arm auto-merge, so a person counts only
// with the admin or maintain role, and a role that can't be read holds the PR.
test('a Claude ESCALATE holds a PR that a person without the maintain role armed', () => {
  const comments = codexState({code: 'clean', security: 'clean'})
  const escalated = {files: ['src/App.cs'], status: claudeSays('ESCALATE'), comments}
  const g = runGate({...escalated, autoMerge: armedBy('helper', 'User'), roles: {helper: 'write'}})
  assert.equal(g.state, 'failure', g.out)
  assert.equal(g.desc, 'Blocked: Claude escalated after helper armed auto-merge; turn it off and queue the PR yourself')
  const unread = runGate({...escalated, autoMerge: armedBy('helper', 'User'), roles: {}})
  assert.equal(unread.state, 'failure', unread.out)
  const failing = runGate({...escalated, autoMerge: armedBy('ctwoodwa', 'User'), fail: 'collaborators'})
  assert.equal(failing.state, 'failure', failing.out)
  const maintainer = runGate({...escalated, autoMerge: armedBy('keeper', 'User'), roles: {keeper: 'maintain'}})
  assert.equal(maintainer.state, 'success', maintainer.out)
  // A Claude PASS doesn't look at who armed it.
  const passed = runGate({files: ['src/App.cs'], status: claudeSays('PASS'), autoMerge: armedBy('helper', 'User'), roles: {}})
  assert.equal(passed.state, 'success', passed.out)
})

test('auto-merge an app armed does not hold a PR Claude passed', () => {
  const g = runGate({files: ['src/App.cs'], status: claudeSays('PASS'), autoMerge: armedBy('harborline-merge[bot]', 'Bot')})
  assert.equal(g.state, 'success', g.out)
  assert.equal(g.desc, 'Passed by Claude')
})

test('a Claude ESCALATE with open Codex security findings blocks', () => {
  const g = runGate({files: ['src/App.cs'], status: claudeSays('ESCALATE'), comments: codexState({code: 'clean', security: 'dirty'})})
  assert.equal(g.state, 'failure', g.out)
  assert.equal(g.desc, 'Blocked: Codex security review has open findings')
})

// Codex on App #98: an earlier version of the review workflow could post an ESCALATE over a BLOCK on the same head, so
// the gate reads back to the last PASS. Only the workflow's own failures count; an error (no verdict) says nothing.
test('a Claude ESCALATE over an earlier BLOCK or HELD on the same head still blocks', () => {
  const clean = codexState({code: 'clean', security: 'clean'})
  for (const earlier of ['BLOCK', 'HELD']) {
    const g = runGate({files: ['src/App.cs'], comments: clean, status: [...claudeAt(3, 'ESCALATE'), ...claudeAt(2, earlier)]})
    assert.equal(g.state, 'failure', `${earlier}: ${g.out}`)
    assert.equal(g.desc, `Blocked: an earlier Claude verdict on this head still holds: Claude review: ${earlier} (shadow, base main)`)
  }
  // A PASS after the BLOCK cleared it, a BLOCK someone else set never counted, and an error carries no verdict.
  for (const [name, history] of [
    ['PASS after BLOCK', [...claudeAt(3, 'ESCALATE'), ...claudeAt(2, 'PASS'), ...claudeAt(1, 'BLOCK')]],
    ['forged BLOCK', [...claudeAt(3, 'ESCALATE'), ...claudeAt(2, 'BLOCK').map(s => ({...s, creator: {login: 'ctwoodwa'}}))]],
    ['error', [...claudeAt(3, 'ESCALATE'), {context: 'claude-review', id: 2, state: 'error', updated_at: '2026-11-02T00:00:00Z',
      description: 'Claude review: no verdict'}]],
  ]) {
    const g = runGate({files: ['src/App.cs'], comments: clean, status: history})
    assert.equal(g.state, 'success', `${name}: ${g.out}`)
    assert.equal(g.desc, 'Passed by Codex; Claude escalated, Codex security review clean', name)
  }
})

// Fable on platform #492: a PASS given to another PR with the same head, for another base, lands on top of this PR's
// verdicts. It clears nothing, so the gate reads past it, whatever the standing verdict is.
test('a Claude PASS for another base clears no BLOCK or ESCALATE beneath it', () => {
  const clean = codexState({code: 'clean', security: 'clean'})
  const at = (id, verdict, base = 'main') => claudeSays(verdict, base).map(s => ({...s, id}))
  const passBlock = runGate({files: ['src/App.cs'], comments: clean, status: [...at(2, 'PASS', 'develop'), ...at(1, 'BLOCK')]})
  assert.equal(passBlock.state, 'failure', passBlock.out)
  assert.equal(passBlock.desc, 'Blocked: an earlier Claude verdict on this head still holds: Claude review: BLOCK (shadow, base main)')
  const escPassBlock = runGate({files: ['src/App.cs'], comments: clean,
    status: [...at(3, 'ESCALATE'), ...at(2, 'PASS', 'develop'), ...at(1, 'BLOCK')]})
  assert.equal(escPassBlock.state, 'failure', escPassBlock.out)
  assert.equal(escPassBlock.desc, 'Blocked: an earlier Claude verdict on this head still holds: Claude review: BLOCK (shadow, base main)')
  // The escalation beneath a PASS for another base still makes the PR risky, so auto-merge stays off.
  const passEsc = runGate({files: ['src/App.cs'], comments: clean, status: [...at(2, 'PASS', 'develop'), ...at(1, 'ESCALATE')]})
  assert.equal(passEsc.state, 'success', passEsc.out)
  assert.equal(passEsc.desc, 'Passed by Codex; Claude escalated, Codex security review clean')
  assert.match(passEsc.out, /risky=true/)
  // A BLOCK beneath a run with no verdict still holds, and its whole description is named, | and all.
  const errorBlock = runGate({files: ['src/App.cs'], comments: clean, status: [
    {context: 'claude-review', id: 2, state: 'error', updated_at: '2026-11-02T00:00:00Z', description: 'Claude review: no verdict'},
    ...at(1, 'BLOCK', 'a|b')]})
  assert.equal(errorBlock.state, 'failure', errorBlock.out)
  assert.equal(errorBlock.desc, 'Blocked: an earlier Claude verdict on this head still holds: Claude review: BLOCK (shadow, base a|b)')
  // A PASS for this base, over the same, clears it.
  const cleared = runGate({files: ['src/App.cs'], comments: clean, status: [...at(3, 'PASS'), ...at(2, 'PASS', 'develop'), ...at(1, 'BLOCK')]})
  assert.equal(cleared.state, 'success', cleared.out)
  assert.doesNotMatch(cleared.out, /risky=true/)
})

// Claude review on api #497: an ESCALATE, like a pass, speaks only for the base it reviewed.
test('a Claude ESCALATE given for another base, or before a retarget, blocks until the new base is reviewed', () => {
  const clean = codexState({code: 'clean', security: 'clean'})
  const other = runGate({files: ['src/App.cs'], comments: clean, status: claudeSays('ESCALATE', 'develop')})
  assert.equal(other.state, 'failure', other.out)
  assert.equal(other.desc, 'Blocked: Claude escalated on an earlier base; waiting for a review of the current base')
  assert.match(other.out, /risky=true/)
  const retargeted = runGate({files: ['src/App.cs'], status: claudeSays('ESCALATE'),
    events: [{event: 'base_ref_changed', created_at: '2026-11-03T00:00:00Z'}]})
  assert.equal(retargeted.state, 'failure', retargeted.out)
  assert.equal(retargeted.desc, 'Blocked: Claude escalated on an earlier base; waiting for a review of the current base')
})

// Codex on App #98: the review workflow sets a new claude-review, resets review-gate to pending, then starts a gate run.
// A run that read the statuses before those writes must not leave its pass standing for a verdict it never read.
const gateIs = (id, state, description) => ({context: 'review-gate', id, state, updated_at: '2026-11-02T00:00:00Z', description})
const claudeAt = (id, verdict) => claudeSays(verdict).map(s => ({...s, id}))
const posted = g => g.posts.map(l => /state=(\w+)/.exec(l)?.[1])

test('the gate posts its pass when nothing changed while it read the PR', () => {
  const status = [...claudeAt(1, 'PASS'), gateIs(2, 'pending', 'Waiting')]
  const g = runGate({files: ['src/App.cs'], status, post: true})
  assert.equal(g.code, 0, g.out)
  assert.deepEqual(posted(g), ['success'], g.out)
})

test('a gate run that read an earlier verdict posts nothing once a new verdict and the reset land', () => {
  const status = [...claudeAt(1, 'PASS'), gateIs(2, 'pending', 'Waiting')]
  const statusAfter = [...claudeAt(3, 'ESCALATE'), gateIs(4, 'pending', 'Waiting for review-gate to read the latest Claude review')]
  const g = runGate({files: ['src/App.cs'], status, statusAfter, post: true})
  assert.equal(g.code, 0, g.out)
  assert.deepEqual(g.posts, [], g.out)
  assert.match(g.out, /review-gate left as it is/)
})

test('a gate run takes its pass back when a new verdict lands between its check and its post', () => {
  const status = [...claudeAt(1, 'PASS'), gateIs(2, 'pending', 'Waiting')]
  // The check (second read) still sees what the run read; the read after the post (third) sees the new verdict.
  const statusAfter = [...claudeAt(3, 'ESCALATE'), gateIs(2, 'pending', 'Waiting')]
  const g = runGate({files: ['src/App.cs'], status, statusAfter, afterRead: 3, post: true})
  assert.equal(g.code, 0, g.out)
  assert.deepEqual(posted(g), ['success', 'pending'], g.out)
  assert.match(g.posts[1], /The Claude review changed while review-gate read it/)
})

// Claude review on App #98: when taking the pass back fails, the gate errors rather than leave the pass standing.
test('a gate run whose take-back fails sets review-gate to error', () => {
  const status = [...claudeAt(1, 'PASS'), gateIs(2, 'pending', 'Waiting')]
  const statusAfter = [...claudeAt(3, 'ESCALATE'), gateIs(2, 'pending', 'Waiting')]
  const g = runGate({files: ['src/App.cs'], status, statusAfter, afterRead: 3, post: true, postFail: 2})
  assert.notEqual(g.code, 0, g.out)
  assert.deepEqual(posted(g), ['success', 'pending', 'error'], g.out)
  assert.match(g.posts[2], /review-gate could not finish/)
})

// Claude review on App #98: runs of one PR cancel the older one, which can still post as it goes. A newer run posts its
// hold over a review-gate that changed under an unchanged claude-review, so an older run's pass doesn't stand.
test('a gate run posts a hold over a status another gate run set while the Claude review stayed the same', () => {
  const status = [...claudeAt(1, 'ESCALATE'), gateIs(2, 'pending', 'Waiting')]
  const statusAfter = [...claudeAt(1, 'ESCALATE'), gateIs(5, 'success', 'Passed by Codex; Claude escalated, Codex security review clean')]
  const g = runGate({files: ['src/App.cs'], status, statusAfter, post: true, comments: codexState({code: 'clean', security: 'dirty'})})
  assert.equal(g.code, 0, g.out)
  assert.deepEqual(posted(g), ['failure'], g.out)
})

// Codex on platform #492: nothing says which gate run is newer, so a run never posts a pass over a review-gate another
// run set while it read the PR; that status may be a newer run's hold. The sweep settles it.
test('a gate run posts no pass over a status another gate run set while the Claude review stayed the same', () => {
  const g = runGate({files: ['src/App.cs'], post: true, status: [...claudeAt(1, 'PASS'), gateIs(2, 'success', 'Passed by Claude')],
    statusAfter: [...claudeAt(1, 'PASS'), gateIs(5, 'failure', 'Blocked: Claude escalated on an earlier base')]})
  assert.equal(g.code, 0, g.out)
  assert.deepEqual(g.posts, [], g.out)
  assert.match(g.out, /review-gate left as it is: another gate run set it/)
})

// CodeRabbit on api #497: an app that arms auto-merge on an escalated PR after the gate read the PR would have it
// merge on the gate's pass, so after posting the pass the gate reads the PR again and takes the pass back.
test('an escalated pass is taken back when an app arms auto-merge while the gate reads the PR', () => {
  const comments = codexState({code: 'clean', security: 'clean'})
  const status = [...claudeAt(1, 'ESCALATE'), gateIs(2, 'pending', 'Waiting')]
  const app = {enabled_by: {login: 'merge-app[bot]', type: 'Bot'}}
  const g = runGate({files: ['src/App.cs'], status, comments, post: true, autoMergeAfter: app})
  assert.equal(g.code, 0, g.out)
  assert.deepEqual(posted(g), ['success', 'failure'], g.out)
  assert.match(g.posts[1], /Claude escalated after merge-app\[bot\] armed auto-merge/)
  // A person arming it, or nobody, leaves the pass standing; a PR that can't be read again counts as armed.
  const person = {enabled_by: {login: 'ctwoodwa', type: 'User'}}
  assert.deepEqual(posted(runGate({files: ['src/App.cs'], status, comments, post: true, autoMergeAfter: person})), ['success'])
  assert.deepEqual(posted(runGate({files: ['src/App.cs'], status, comments, post: true, autoMergeAfter: null})), ['success'])
  const lost = runGate({files: ['src/App.cs'], status, comments, post: true, autoMergeAfter: 'unreadable'})
  assert.deepEqual(posted(lost), ['success', 'failure'], lost.out)
  // A person without the maintain role arming it takes the pass back too (Codex security review on app #98).
  const helper = {enabled_by: {login: 'helper', type: 'User'}}
  const h = runGate({files: ['src/App.cs'], status, comments, post: true, autoMergeAfter: helper, roles: {helper: 'write'}})
  assert.deepEqual(posted(h), ['success', 'failure'], h.out)
  assert.match(h.posts[1], /Claude escalated after helper armed auto-merge/)
})

// Claude review on api #497: the gate reads each status as one line, so a line break in a description, here in a
// review-gate status someone posted, must not read as a Claude PASS from the workflow.
test('a line break in a status description cannot pass for a Claude verdict', () => {
  const forged = {context: 'review-gate', id: 9, state: 'pending', updated_at: '2026-11-03T00:00:00Z', creator: {login: 'ctwoodwa'},
    description: 'x\nclaude-review|success|2026-11-03T00:00:00Z|github-actions[bot]|8|Claude review: PASS (shadow, base main)'}
  const g = runGate({files: ['src/App.cs'], comments: codexState({code: 'clean', security: 'clean'}),
    status: [forged, ...claudeAt(1, 'BLOCK')]})
  assert.equal(g.state, 'failure', g.out)
  assert.equal(g.desc, 'Blocked: Claude review: BLOCK (shadow, base main)')
})

// Claude review on Platform #492: an ESCALATE run that can't read the earlier verdict posts HELD rather than nothing.
test('a Claude HELD blocks even when Codex passed both reviews', () => {
  // The description review.yml writes, which has ESCALATE inside it; the verdict is read by position, so it stays HELD.
  const held = 'Claude review: HELD (ESCALATE, earlier verdict unreadable; shadow, base main)'
  const status = [{context: 'claude-review', state: 'failure', updated_at: '2026-11-02T00:00:00Z', description: held}]
  const g = runGate({files: ['src/App.cs'], status, comments: codexState({code: 'clean', security: 'clean'})})
  assert.equal(g.state, 'failure', g.out)
  assert.equal(g.desc, `Blocked: ${held}`)
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

// Fable on platform #492: an ESCALATE beneath a PASS for another base still makes the PR risky, even one given before a
// retarget, so an exempt change still waits for a review and is never armed for auto-merge.
test('a Claude ESCALATE beneath a PASS for another base still makes an exempt change wait for a review', () => {
  const g = runGate({files: ['SUPPORT.md'], status: [...claudeAt(2, 'PASS').map(s => ({...s, description: 'Claude review: PASS (shadow, base develop)'})),
    ...claudeAt(1, 'ESCALATE')], events: [{event: 'base_ref_changed', created_at: '2026-11-03T00:00:00Z'}]})
  assert.equal(g.state, 'pending', g.out)
  assert.equal(g.desc, 'Waiting for a clean review of the current base (Claude reviewed an earlier one)')
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
