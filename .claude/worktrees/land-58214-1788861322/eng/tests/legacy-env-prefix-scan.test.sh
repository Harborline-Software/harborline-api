#!/usr/bin/env bash
# Enumerates eng/legacy-env-prefix-scan.sh against throwaway git repositories:
#   {plain read, split literal, doc prose, script, python, C#, json baseline} x
#   {allow-listed, not allow-listed, allow-listed with no reason, row for another file} plus the
#   stale-row case (a row that matches nothing) and the untracked case (git grep sees tracked files).
# Every case is a recorded RED or GREEN for the guard itself: the ticket-245 clean break has no
# fallback, so the only accepted occurrences are the refusal sites named in the allow-list.
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
fence="$here/../legacy-env-prefix-scan.sh"
p=$(printf '\x48\x55\x4c\x4c\x5f')
fails=0; n=0

run_case() { # name expect(0|1) file content [allowlist-rows]
  local name=$1 want=$2 file=$3 content=$4 allow=${5:-}
  local repo; repo=$(mktemp -d)
  ( cd "$repo" && git init -q && mkdir -p eng "$(dirname "$file")" \
    && printf '%s\n' "$content" > "$file" \
    && { [ -z "$allow" ] || printf '%s\n' "$allow" > eng/legacy-env-prefix-allowlist.tsv; } \
    && git add -A && git -c user.email=t@t -c user.name=t commit -qm x )
  bash "$fence" "$repo" >/dev/null 2>&1; local rc=$?
  n=$((n+1))
  if [ "$rc" != "$want" ]; then echo "FAIL $name: rc=$rc want=$want"; fails=$((fails+1)); else echo "ok   $name"; fi
  rm -rf "$repo"
}

row() { printf '%s\t%s\t%s' "$1" "refusal" "$2"; }

# --- a reader anywhere in the tree is red -----------------------------------
run_case "ts: a plain read of a retired-prefix variable" 1 src/runtime.ts \
  "const dev = process.env.${p}DEV"
run_case "ts: a SPLIT literal reads the same variable" 1 src/runtime.ts \
  "const dev = process.env['${p}' + 'DEV']"
run_case "mjs: a script-side read" 1 scripts/run.mjs \
  "if (process.env.${p}IMAGE_REAL === '1') start()"
run_case "py: a worker read" 1 workers/embed.py \
  "cache = os.environ.get(\"${p}KG_EMBED_HF_CACHE\")"
run_case "cs: a host-side read" 1 Data/Search/Options.cs \
  "var seed = Environment.GetEnvironmentVariable(\"${p}NODE_SEED_HEX\");"
run_case "md: doc prose still telling an operator to export the old name" 1 README.md \
  "Run it with \`${p}DEV=1\`."
run_case "sh: a script exporting the old name" 1 eng/run.sh \
  "export ${p}MEMBRANE_TRACE_ID=abc"
run_case "the bare prefix on its own is still red (a split literal declares exactly that)" 1 src/runtime.ts \
  "const LEGACY = '${p}'"

# --- the allow-list, exact rows only ----------------------------------------
run_case "a refusal site with an exact row and a reason is accepted" 0 src/guard.ts \
  "const LEGACY_PREFIX = '${p}'" "$(row src/guard.ts 'the runtime guard must name the prefix to refuse it')"
run_case "a row with NO reason does not cover it" 1 src/guard.ts \
  "const LEGACY_PREFIX = '${p}'" "$(printf 'src/guard.ts\trefusal\t')"
run_case "a row for a DIFFERENT file does not cover it" 1 src/guard.ts \
  "const LEGACY_PREFIX = '${p}'" "$(row src/other.ts 'wrong file')"
run_case "a row is a whole path, not a prefix of one" 1 src/guard.ts \
  "const LEGACY_PREFIX = '${p}'" "$(row src 'directory rows are not allowed')"
run_case "a comment line in the allow-list is not a row" 1 src/guard.ts \
  "const LEGACY_PREFIX = '${p}'" "$(printf '# src/guard.ts\trefusal\tcommented out')"
run_case "the pinned baseline prose is allow-listed like any other file" 0 eng/baselines/b.json \
  "{\"gate\": \"env ${p}IMAGE_REAL=1\"}" "$(row eng/baselines/b.json 'pinned baseline records the pre-rename gate name')"

# --- the allow-list equals the discovered set -------------------------------
run_case "a STALE row (its file no longer names the prefix) fails" 1 src/guard.ts \
  "const LEGACY_PREFIX = 'CAPABILITY_HOST_'" "$(row src/guard.ts 'renamed away; this row should have gone with it')"

# --- clean tree --------------------------------------------------------------
run_case "a tree carrying only CAPABILITY_HOST_ names passes" 0 src/runtime.ts \
  "const dev = process.env.CAPABILITY_HOST_DEV"

echo "$n cases, $fails failures"
[ $fails -eq 0 ]
