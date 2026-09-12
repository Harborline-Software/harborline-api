#!/usr/bin/env bash
# Shared by the gate-side quality comparison. The committed baseline re-pin is a deliberate follow-up PR from the gate host's artifact.
quality_baseline_compare_result() {
  local candidate=$1 baseline=$2 diff=${3:-}
  node "$(dirname "${BASH_SOURCE[0]}")/quality-baseline-compare.mjs" "$candidate" "$baseline" "$diff"
}
quality_baseline_sets_compare() {
  local candidate=$1 baseline=$2 diff=${3:-} result
  result=$(quality_baseline_compare_result "$candidate" "$baseline" "$diff") || return 1
  node -e 'const row = JSON.parse(process.argv[1]); console.log(`${row.new} ${row.resolved}`)' "$result"
}
quality_baseline_new_findings() {
  local candidate=$1 baseline=$2 diff=${3:-} result
  result=$(quality_baseline_compare_result "$candidate" "$baseline" "$diff") || return 1
  node -e 'for (const finding of JSON.parse(process.argv[1]).findings) console.log(`quality-baseline: new finding ruleId=${finding.ruleId} path=${finding.path}`)' "$result"
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
quality_baseline_committed_fallback_details() {
  local baseline=$1
  node - "$baseline" <<'NODE'
const {readFileSync} = require('node:fs')
const baseline = JSON.parse(readFileSync(process.argv[2], 'utf8'))
const commit = typeof baseline.commit === 'string' && baseline.commit ? baseline.commit : 'unknown'
const generatedAt = Date.parse(baseline.generatedAt)
let age = 'unknown'
if (Number.isFinite(generatedAt)) {
  const seconds = Math.max(0, Math.floor((Date.now() - generatedAt) / 1000))
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor(seconds % 86400 / 3600)
  const minutes = Math.floor(seconds % 3600 / 60)
  age = days > 0 ? `${days}d ${hours}h` : `${hours}h ${minutes}m`
}
console.log(`pinned commit=${commit}; age=${age}`)
NODE
}
quality_baseline_gate_candidate_compare() {
  local candidate=$1 baseline=$2 diff=${3:-} counts new resolved baseline_count engine status detail engine_failed=0
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
  counts=$(quality_baseline_sets_compare "$candidate" "$baseline" "$diff") || return 1
  read -r new resolved <<<"$counts"
  if [ "$new" -gt 0 ]; then
    echo "quality-baseline: $new new, $resolved resolved" >&2
    quality_baseline_new_findings "$candidate" "$baseline" "$diff" >&2 || return 1
    return 1
  fi
  baseline_count=$(quality_baseline_finding_count "$baseline") || return 1
  if [ "$resolved" -gt 50 ] && [ "$((resolved * 10))" -gt "$baseline_count" ]; then
    echo "quality-baseline: resolved $resolved of $baseline_count looks like an engine failure, not a clean-up; run the tool by hand" >&2
    quality_baseline_print_engine_table "$candidate"
    return 1
  fi
  if [ "$resolved" -gt 0 ]; then
    echo "quality-baseline: 0 new, $resolved resolved (passing; re-pin is a follow-up PR)"
  else
    echo 'quality-baseline: unchanged (0 new, 0 resolved)'
  fi
}
quality_baseline_gate() {
  local gate_root=$1
  local committed="$gate_root/eng/baselines/quality-baseline.json" baseline candidate="$gate_root/artifacts/quality/findings.json" diff="$gate_root/artifacts/quality/base-to-head.diff" outcome
  if [ -n "${HARBORLINE_QUALITY_BASELINE:-}" ] && [ -f "$HARBORLINE_QUALITY_BASELINE" ]; then
    baseline="$HARBORLINE_QUALITY_BASELINE"
    echo "quality-baseline: using merge-base artifact $baseline"
  else
    baseline="$committed"
    echo "quality-baseline: using committed fallback $baseline ($(quality_baseline_committed_fallback_details "$baseline"))"
  fi
  if [ ! -f "$candidate" ] && ! ( cd "$gate_root" && node eng/quality-step.mjs ); then
    return 1
  fi
  quality_baseline_gate_candidate_compare "$candidate" "$baseline" "$diff"
  outcome=$?
  return "$outcome"
}
