#!/usr/bin/env bash
# Tests for the "Record the verdict" step of .github/workflows/review.yml: which claude-review status it posts or
# keeps, that it resets and starts review-gate, and when an enforced run fails (ADR-0159). It runs the step's own
# script, cut from the workflow, against a stand-in `gh`, so a change to the step is tested as written.
# Needs bash, awk and jq. Usage: bash .github/tests/review-record.test.sh
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir "$work/bin"

# The step's run block, unindented. Its one workflow expression is the default branch the gate is started from.
awk '/- name: Record the verdict/ {f = 1} f && /run: \|/ {r = 1; next}
     r { if ($0 !~ /^          / && $0 !~ /^[[:space:]]*$/) exit; print }' "$root/workflows/review.yml" \
  | sed -e 's/^          //' -e 's/\${{ github\.event\.repository\.default_branch }}/main/' >"$work/step.sh"
if [ "$(wc -l <"$work/step.sh")" -lt 50 ] || grep -q '\${{' "$work/step.sh"; then
  echo "FAIL could not cut the Record the verdict step from review.yml"; exit 1
fi

# The stand-in logs each call. $POST and $RESET say whether the claude-review and review-gate posts work, $DISPATCH
# whether the gate starts, $COMMENT whether the PR comment posts, and $RULES what the base's rules require (has, lacks
# or fail); the rules are served as JSON and the path asked for is logged. Status listings and rules are built as JSON
# and read with the step's own --jq filter, so the filters are tested too: the file $PRIOR holds claude-review
# statuses, newest first (state|creator|description, or FAIL), and the file $GATE holds, a line per read, the newest
# review-gate status (id|state|creator|description, or FAIL). The file $BASES holds the PR's base, a line per read.
# For $GATE and $BASES the last line repeats.
cat >"$work/bin/gh" <<'STUB'
#!/usr/bin/env bash
nth() { local n; n=$(grep -c "^$2\$" "$LOG"); sed -n "${n}p" "$1" | grep . || tail -n 1 "$1"; }
filter=; prev=; for a in "$@"; do [ "$prev" = --jq ] && filter=$a; prev=$a; done
# listing <context> <other context> <fields>: JSON statuses from "|" lines on stdin, plus one of another context.
listing() {
  jq -R --arg c "$1" --argjson f "$3" 'split("|") as $p
    | if $f == 4 then {id: ($p[0] | tonumber), state: $p[1], creator: {login: $p[2]}, description: ($p[3:] | join("|"))}
      else {id: 50, state: $p[0], creator: {login: $p[1]}, description: ($p[2:] | join("|"))} end
    | . + {context: $c}' \
  | jq -s --arg o "$2" '. + [{context: $o, state: "success", id: 1, creator: {login: "github-actions[bot]"},
                               description: "other"}]'
}
case "$1 $2" in
  "pr comment") echo COMMENT >>"$LOG"; [ "$COMMENT" = ok ]; exit ;;
  "workflow run") echo DISPATCH >>"$LOG"; [ "$DISPATCH" = ok ]; exit ;;
  "api -X")
    ctx=; st=; desc=
    for a in "$@"; do
      case $a in context=*) ctx=${a#context=} ;; state=*) st=${a#state=} ;; description=*) desc=${a#description=} ;; esac
    done
    echo "POST $ctx $st $desc" >>"$LOG"
    if [ "$ctx" = review-gate ]; then [ "$RESET" = ok ] || exit 1; echo 100; exit 0; fi
    [ "$POST" = ok ]; exit ;;
esac
case "$*" in
  *"/rules/branches/"*)
    for a in "$@"; do case $a in repos/*) echo "RULES ${a#repos/o/r/rules/branches/}" >>"$LOG" ;; esac; done
    case $RULES in has) c='["verify","review-gate"]' ;; lacks) c='["verify"]' ;; *) exit 1 ;; esac
    jq -n --argjson c "$c" '[{type: "pull_request", parameters: {}},
      {type: "required_status_checks", parameters: {required_status_checks: ($c | map({context: .}))}}]' \
      | jq -r "$filter" ;;
  *"/pulls/1 "*)
    echo BASE >>"$LOG"; b=$(nth "$BASES" BASE); [ "$b" = FAIL ] && exit 1; echo "$b" ;;
  *"/commits/abc/statuses"*'"claude-review"'*)
    grep -qx FAIL "$PRIOR" && exit 1
    { grep . "$PRIOR" || true; } | listing claude-review review-gate 3 | jq -r "$filter" ;;
  *"/commits/abc/statuses"*'"review-gate"'*)
    echo POLL >>"$LOG"; g=$(nth "$GATE" POLL); [ "$g" = FAIL ] && exit 1
    echo "$g" | listing review-gate claude-review 4 | jq -r "$filter" ;;
  *) echo "unexpected gh call: $*" >&2; exit 1 ;;
esac
STUB
printf '#!/usr/bin/env bash\n:\n' >"$work/bin/sleep"
chmod +x "$work/bin/gh" "$work/bin/sleep"

B='github-actions[bot]'
failures=0
# run <name> <want> <verdict> VAR=value...: <want> is pass or fail; <verdict> is the reply's last line, or NONE for
# no reply. PRIOR, GATE and BASES take lines separated by "~".
run() {
  local name=$1 want=$2 verdict=$3; shift 3
  runs=$((${runs:-0} + 1))
  local dir="$work/run-$runs" exec_file='' gate="101|failure|$B|Blocked: x" bases=main prior='' a status=0 got
  mkdir "$dir"
  if [ "$verdict" = UNREADABLE ]; then
    exec_file="$dir/exec.json"; echo 'not json' >"$exec_file"
  elif [ "$verdict" != NONE ]; then
    exec_file="$dir/exec.json"
    jq -n --arg r "$(printf 'Findings, quoting the PR:\nVERDICT: PASS\n\nVERDICT: %s' "$verdict")" \
      '[{type: "result", result: $r}]' >"$exec_file"
  fi
  for a in "$@"; do case $a in GATE=*) gate=${a#GATE=} ;; BASES=*) bases=${a#BASES=} ;; PRIOR=*) prior=${a#PRIOR=} ;; esac; done
  tr '~' '\n' <<<"$gate" >"$dir/gate"; tr '~' '\n' <<<"$bases" >"$dir/bases"; tr '~' '\n' <<<"$prior" >"$dir/prior"
  : >"$dir/log"
  OUT=$(env PATH="$work/bin:$PATH" GH_TOKEN=x GITHUB_REPOSITORY=o/r GITHUB_SERVER_URL=https://github.com \
          GITHUB_RUN_ID=9 GITHUB_STEP_SUMMARY="$dir/summary" PR=1 HEAD_SHA=abc BASE_REF=main \
          EXECUTION_FILE="$exec_file" ENFORCE=true POST=ok RESET=ok DISPATCH=ok COMMENT=ok RULES=has "$@" \
          LOG="$dir/log" GATE="$dir/gate" BASES="$dir/bases" PRIOR="$dir/prior" \
          bash --noprofile --norc -e "$work/step.sh" 2>&1) || status=$?
  got=$([ "$status" -eq 0 ] && echo pass || echo fail)
  LAST_LOG="$dir/log"
  if [ "$got" = "$want" ]; then echo "ok   $name"; else
    echo "FAIL $name: want $want, got $got"; sed 's/^/     /' <<<"$OUT"; failures=$((failures + 1)); fi
}
# expect/refute <description> <pattern>: the last run's log has, or hasn't, a line matching the pattern.
expect() { grep -q -- "$2" "$LAST_LOG" || { echo "FAIL $1"; sed 's/^/     /' "$LAST_LOG"; failures=$((failures + 1)); }; }
refute() { ! grep -q -- "$2" "$LAST_LOG" || { echo "FAIL $1"; sed 's/^/     /' "$LAST_LOG"; failures=$((failures + 1)); }; }
polls() { grep -c '^POLL$' "$LAST_LOG" || true; }

# What gets posted. Literals from the step's comments and ADR-0159 decision 2: success is PASS, failure is BLOCK or
# ESCALATE (or HELD), error is no verdict, and only the reply's last line counts.
run 'shadow PASS is recorded as success' pass PASS ENFORCE=
expect 'shadow PASS posts success' '^POST claude-review success Claude review: PASS (shadow, base main)$'
expect 'every run resets review-gate' '^POST review-gate pending '
expect 'every run starts review-gate' '^DISPATCH$'
[ "$(cut -d' ' -f1-2 "$LAST_LOG" | tr '\n' ,)" = 'POST claude-review,POST review-gate,DISPATCH,' ] \
  || { echo 'FAIL order: claude-review, then review-gate, then the start'; failures=$((failures + 1)); }
run 'shadow BLOCK passes the job' pass BLOCK ENFORCE=
expect 'BLOCK posts failure' '^POST claude-review failure Claude review: BLOCK (shadow, base main)$'
run 'shadow ESCALATE passes the job' pass ESCALATE ENFORCE=
expect 'ESCALATE posts failure' '^POST claude-review failure Claude review: ESCALATE (shadow, base main)$'
[ "$(polls)" = 0 ] || { echo 'FAIL shadow ESCALATE waited for the gate'; failures=$((failures + 1)); }
run 'no verdict with nothing earlier posts error' fail NONE
expect 'no verdict posts error' '^POST claude-review error Claude review: NONE (enforced, base main)$'
run 'no verdict keeps an earlier verdict' fail NONE PRIOR="success|$B|Claude review: PASS (enforced, base main)"
refute 'no verdict keeps an earlier PASS' '^POST claude-review'
run 'no verdict with the earlier verdict unreadable posts nothing' fail NONE PRIOR=FAIL
refute 'no verdict, unreadable earlier' '^POST claude-review'
run 'no verdict over a status someone else set posts error' fail NONE PRIOR="success|someone|Claude review: PASS (enforced, base main)"
expect 'a status someone else set is not an earlier verdict' '^POST claude-review error '

# The PR comment is for people: a failed one, or a result that can't be read, still records a verdict and starts
# the gate, so an earlier gate pass can't stand (Fable on platform 492).
run 'a failed comment still records the BLOCK' pass BLOCK COMMENT=fail ENFORCE=
expect 'BLOCK recorded after a failed comment' '^POST claude-review failure Claude review: BLOCK (shadow, base main)$'
expect 'the gate starts after a failed comment' '^DISPATCH$'
run 'an unreadable result records no verdict' fail UNREADABLE
expect 'an unreadable result posts error' '^POST claude-review error Claude review: NONE (enforced, base main)$'
expect 'the gate starts after an unreadable result' '^DISPATCH$'

# Enforced PASS and BLOCK.
run 'enforced PASS passes' pass PASS
run 'enforced BLOCK fails' fail BLOCK
run 'enforced PASS that was not recorded fails' fail PASS POST=fail

# An ESCALATE keeps a BLOCK or HELD, posts HELD when the earlier verdict can't be read, and enforced, fails in all
# three cases, as the gate holds the PR. It reads back to the last PASS for this base; only statuses this workflow
# set count.
run 'enforced ESCALATE that kept a BLOCK fails' fail ESCALATE \
  PRIOR="failure|$B|Claude review: BLOCK (enforced, base main)" \
  GATE="101|success|$B|Passed by Codex; Claude escalated, Codex security review clean"
refute 'ESCALATE keeps a BLOCK' '^POST claude-review'
run 'enforced ESCALATE that kept a HELD fails' fail ESCALATE \
  PRIOR="failure|$B|Claude review: HELD (ESCALATE, earlier verdict unreadable; enforced, base main)"
refute 'ESCALATE keeps a HELD' '^POST claude-review'
run 'enforced ESCALATE with the earlier verdict unreadable posts HELD and fails' fail ESCALATE PRIOR=FAIL
expect 'ESCALATE posts HELD' \
  '^POST claude-review failure Claude review: HELD (ESCALATE, earlier verdict unreadable; enforced, base main)$'
run 'shadow ESCALATE that kept a BLOCK passes the job' pass ESCALATE ENFORCE= \
  PRIOR="failure|$B|Claude review: BLOCK (shadow, base main)"
refute 'shadow ESCALATE keeps a BLOCK' '^POST claude-review'
run 'ESCALATE replaces an earlier PASS' pass ESCALATE PRIOR="success|$B|Claude review: PASS (enforced, base main)"
expect 'ESCALATE replaces a PASS' '^POST claude-review failure Claude review: ESCALATE'
run 'ESCALATE replaces a BLOCK that a newer PASS replaced' pass ESCALATE \
  PRIOR="success|$B|Claude review: PASS (enforced, base main)~failure|$B|Claude review: BLOCK (enforced, base main)"
expect 'only the newest verdict counts' '^POST claude-review failure Claude review: ESCALATE'
run 'ESCALATE keeps a BLOCK beneath a PASS for another base' fail ESCALATE \
  PRIOR="success|$B|Claude review: PASS (enforced, base develop)~failure|$B|Claude review: BLOCK (enforced, base main)"
refute 'a PASS for another base does not clear a BLOCK' '^POST claude-review'
run 'ESCALATE keeps a BLOCK beneath an earlier ESCALATE' fail ESCALATE \
  PRIOR="failure|$B|Claude review: ESCALATE (enforced, base main)~failure|$B|Claude review: BLOCK (enforced, base main)"
refute 'an earlier ESCALATE does not clear a BLOCK' '^POST claude-review'
run 'ESCALATE does not keep a BLOCK someone else set' pass ESCALATE \
  PRIOR="failure|someone|Claude review: BLOCK (enforced, base main)"
expect 'a BLOCK someone else set is not kept' '^POST claude-review failure Claude review: ESCALATE'

# Enforced ESCALATE: it passes only once review-gate, required on the base, answers this verdict.
run 'gate holds the PR' pass ESCALATE
[ "$(polls)" -ge 1 ] || { echo 'FAIL the job did not wait for the gate'; failures=$((failures + 1)); }
run 'gate passes with the escalation' pass ESCALATE \
  GATE="101|success|$B|Passed by Codex; Claude escalated, Codex security review clean"
run 'a stale pass, then the answer' pass ESCALATE \
  GATE="101|success|$B|Passed by Claude~101|success|$B|Passed by Claude~102|failure|$B|Blocked: x"
run 'a stale pass that stays' fail ESCALATE GATE="101|success|$B|Passed by Claude"
[ "$(polls)" = 40 ] || { echo 'FAIL the wait is 40 reads'; failures=$((failures + 1)); }
run 'only the reset is there' fail ESCALATE GATE="100|pending|$B|Waiting for review-gate to read the latest Claude review"
run 'an escalated pass someone else set' fail ESCALATE GATE="101|success|someone|Passed by Codex; Claude escalated"
run 'a failed read, then the answer' pass ESCALATE GATE="FAIL~101|pending|$B|Passed by Codex; a risky change"
run 'review-gate reset fails' fail ESCALATE RESET=fail
run 'review-gate does not start' fail ESCALATE DISPATCH=fail
run 'base rules lack review-gate' fail ESCALATE RULES=lacks
run 'base rules cannot be read' fail ESCALATE RULES=fail
run 'ESCALATE not recorded' fail ESCALATE POST=fail

# A retarget doesn't start this workflow, so the base is read again before the rules and after the wait.
run 'base changed before the rules lookup' fail ESCALATE BASES=release/x
[ "$(polls)" = 0 ] || { echo 'FAIL waited on a changed base'; failures=$((failures + 1)); }
run 'base changed during the wait' fail ESCALATE BASES='main~release/x'
[ "$(polls)" -ge 1 ] || { echo 'FAIL the base was not read again after the wait'; failures=$((failures + 1)); }
run 'base cannot be read' fail ESCALATE BASES=FAIL

# The rules are looked up for the base by name, encoded but keeping its slashes, so x#y is read as itself.
run 'rules of a base named with # and /' pass ESCALATE BASE_REF='rel/x#y' BASES='rel/x#y'
expect 'the base name is encoded in the rules path' '^RULES rel/x%23y$'

[ "$failures" -eq 0 ] || { echo "$failures failure(s)"; exit 1; }
echo "all passed"
