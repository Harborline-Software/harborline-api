#!/usr/bin/env bash
# Shared by land.sh and its focused proof: generate the candidate baseline from the merged tree, then compare
# the SET OF FINDINGS with the committed baseline. Bytes are not comparable: the committed file may be
# pretty-printed while the tool writes canonical JSON, and commit/generatedAt are re-derived on every run.
quality_baseline_sets_compare() {
  local candidate=$1 baseline=$2
  node - "$candidate" "$baseline" <<'NODE'
const {readFileSync} = require('node:fs')
const load = file => new Set(JSON.parse(readFileSync(file, 'utf8')).findings.map(f => f.fingerprint))
const candidate = load(process.argv[2]), baseline = load(process.argv[3])
const fresh = [...candidate].filter(f => !baseline.has(f)).length
const resolved = [...baseline].filter(f => !candidate.has(f)).length
console.log(`${fresh} ${resolved}`)
NODE
}
quality_baseline_new_findings() {
  local candidate=$1 baseline=$2
  node - "$candidate" "$baseline" <<'NODE'
const {readFileSync} = require('node:fs')
const load = file => JSON.parse(readFileSync(file, 'utf8')).findings
const candidate = load(process.argv[2]), baseline = new Set(load(process.argv[3]).map(f => f.fingerprint))
for (const finding of candidate.filter(f => !baseline.has(f.fingerprint))) {
  console.log(`quality-baseline: new finding ruleId=${finding.ruleId} path=${finding.path}`)
}
NODE
}
quality_baseline_finding_count() {
  local baseline=$1
  node - "$baseline" <<'NODE'
const {readFileSync} = require('node:fs')
console.log(JSON.parse(readFileSync(process.argv[2], 'utf8')).findings.length)
NODE
}
quality_baseline_engine_table() {
  local candidate=$1
  node - "$candidate" <<'NODE'
const {readFileSync} = require('node:fs')
const engines = JSON.parse(readFileSync(process.argv[2], 'utf8')).engines
if (!Array.isArray(engines) || !engines.length) console.log('none\tunknown\tno engine status recorded')
else for (const row of engines) console.log(`${row.engine}\t${row.status}\t${row.detail ?? ''}`)
NODE
}
quality_baseline_print_engine_table() {
  local candidate=$1 engine status detail
  echo 'quality-baseline: engine table:' >&2
  while IFS="$(printf '\t')" read -r engine status detail; do
    echo "quality-baseline: engine $engine status=$status detail=$detail" >&2
  done <<EOF
$(quality_baseline_engine_table "$candidate")
EOF
}
quality_baseline_gate_candidate_compare() {
  local candidate=$1 baseline=$2 counts new resolved baseline_count engine status detail engine_failed=0
  while IFS="$(printf '\t')" read -r engine status detail; do
    if [ "$status" = 'analyzer-error' ]; then
      # A warning, not a refusal: the tool reports analyzer-error for both engines on EVERY host today,
      # Windows included, while still producing the full finding set there. What tells a hollow run
      # apart is the resolved bound below (mac16, 2026-09-10: 744 of 2363 "resolved" at once).
      echo "quality-baseline: warning: engine $engine reported analyzer-error; the finding set may be incomplete (the resolved bound decides)" >&2
      engine_failed=1
    fi
  done <<EOF
$(quality_baseline_engine_table "$candidate")
EOF
  if [ "$engine_failed" -ne 0 ]; then
    quality_baseline_print_engine_table "$candidate"
  fi
  counts=$(quality_baseline_sets_compare "$candidate" "$baseline") || return 1
  read -r new resolved <<<"$counts"
  if [ "$new" -gt 0 ]; then
    echo "quality-baseline: $new new, $resolved resolved" >&2
    quality_baseline_new_findings "$candidate" "$baseline" >&2 || return 1
    return 1
  fi
  baseline_count=$(quality_baseline_finding_count "$baseline") || return 1
  if [ "$resolved" -gt 50 ] && [ "$((resolved * 10))" -gt "$baseline_count" ]; then
    echo "quality-baseline: resolved $resolved of $baseline_count looks like an engine failure, not a clean-up; run the tool by hand" >&2
    quality_baseline_print_engine_table "$candidate"
    return 1
  fi
  if [ "$resolved" -gt 0 ]; then
    echo "quality-baseline: 0 new, $resolved resolved (passing; re-pin belongs to land)"
  else
    echo 'quality-baseline: unchanged (0 new, 0 resolved)'
  fi
}
quality_baseline_gate() {
  local gate_root=$1
  local baseline="$gate_root/eng/baselines/quality-baseline.json" candidate outcome
  # Keep the candidate relative to the root: a Windows node invoked from Git Bash cannot resolve an MSYS path.
  candidate=$(cd "$gate_root" && mktemp "./.quality-baseline.XXXXXX") || return 1
  if ! ( cd "$gate_root" && node eng/quality-step.mjs --write-baseline "$candidate" ); then
    rm -f "$gate_root/$candidate"
    return 1
  fi
  quality_baseline_gate_candidate_compare "$gate_root/$candidate" "$baseline"
  outcome=$?
  rm -f "$gate_root/$candidate"
  return "$outcome"
}
quality_baseline_compare() {
  local land_root=$1
  local baseline="$land_root/eng/baselines/quality-baseline.json" candidate counts new resolved
  # The candidate stays relative to the land root: node resolves it against its own cwd, and an MSYS
  # absolute path handed to a Windows node does not.
  candidate=$(cd "$land_root" && mktemp "./.quality-baseline.XXXXXX")
  if ! ( cd "$land_root" && node eng/quality-step.mjs --write-baseline "$candidate" ); then
    rm -f "$land_root/$candidate"
    return 1
  fi
  counts=$(quality_baseline_sets_compare "$land_root/$candidate" "$baseline") || { rm -f "$land_root/$candidate"; return 1; }
  rm -f "$land_root/$candidate"
  read -r new resolved <<<"$counts"
  if [ "$new" -eq 0 ] && [ "$resolved" -eq 0 ]; then
    echo 'land: quality baseline unchanged'
    return 0
  fi
  # Ticket 335 carries the re-pin back to the branch for the host baseline; the quality baseline follows it.
  echo "land: quality baseline moved: $new new, $resolved resolved (re-pin on the branch and regate)" >&2
  return 1
}
