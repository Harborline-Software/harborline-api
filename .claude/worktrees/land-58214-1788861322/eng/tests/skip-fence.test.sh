#!/usr/bin/env bash
# Enumerates eng/skip-fence.sh against a throwaway git repository:
#   Fact|Theory x Skip argument first|middle|last x whitespace variants x ordinary|verbatim|raw|constant RHS
#   x {no ticket, T-NNN, ticket NNN, tickets/NNN-} — every compile-valid skip is SELECTED; exactly those without
#   an allowed ticket reference are REJECTED; non-test lines that merely contain "Skip =" are not selected.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
fence="$here/../skip-fence.sh"
fails=0; n=0
run_case() { # name expect(0|1) content
  local name=$1 want=$2 content=$3
  local repo; repo=$(mktemp -d)
  ( cd "$repo" && git init -q && printf '%s\n' "$content" > Probe.cs && git add Probe.cs && git -c user.email=t@t -c user.name=t commit -qm x )
  bash "$fence" "$repo" >/dev/null 2>&1; local rc=$?
  n=$((n+1))
  if [ "$rc" != "$want" ]; then echo "FAIL $name: rc=$rc want=$want :: $content"; fails=$((fails+1)); else echo "ok   $name"; fi
  rm -rf "$repo"
}
attrs=(Fact Theory)
rhs=('"no reason"' '@"verbatim reason"' '"""raw reason"""' 'Reasons.X')
tickets=('' ' see T-241' ' ticket 241' ' tickets/241-recovery')
for a in "${attrs[@]}"; do
  for r in "${rhs[@]}"; do
    for t in "${tickets[@]}"; do
      want=1; [ -n "$t" ] && want=0
      # ticket text goes inside string reasons; for a constant RHS it must appear elsewhere on the attribute line
      case "$r" in Reasons.X) line="[$a(Skip = $r, DisplayName = \"x$t\")]";; *) inner=${r%\"*}; tail=${r##*\"}; line="[$a(Skip = ${r/reason/reason$t})]";; esac
      run_case "$a $r ticket='${t:-none}' first" $want "$line"
      case "$r" in
        Reasons.X) mid="[$a(DisplayName = \"d$t\", Skip=$r, Timeout = 1)]"; last="[$a(DisplayName=\"d$t\",Skip=$r)]";;
        *) mid="[$a(DisplayName = \"d\", Skip=${r/reason/reason$t}, Timeout = 1)]"; last="[$a(DisplayName=\"d\",Skip=${r/reason/reason$t})]";;
      esac
      run_case "$a $r ticket='${t:-none}' middle" $want "$mid"
      run_case "$a $r ticket='${t:-none}' last-tight" $want "$last"
    done
  done
done
# whitespace variants around '='
run_case "Fact Skip tabs/spaces no ticket" 1 $'[Fact(Skip\t =  "x")]'
run_case "Theory Skip spaces with ticket" 0 '[Theory(  Skip  =  "flaky T-241"  )]'
# not selected: a non-attribute line mentioning Skip =
run_case "plain code 'Skip =' is not a test skip" 0 'var Skip = "not an attribute";'
run_case "other attribute named SkipList" 0 '[SkipList(Skip = "x")]'
# substring ticket near-miss: T-1234 must not satisfy T-NNN via substring
run_case "T-1234 is not T-NNN" 1 '[Fact(Skip = "see T-1234")]'
echo "$n cases, $fails failures"
[ $fails -eq 0 ]
