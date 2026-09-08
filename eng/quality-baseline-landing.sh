#!/usr/bin/env bash
# Shared by land.sh and its focused proof: generate the candidate from the merged tree, then compare raw bytes.
quality_baseline_compare() {
  local land_root=$1
  local baseline="$land_root/eng/baselines/quality-baseline.json" candidate decision counts new resolved
  candidate=$(mktemp "$land_root/.quality-baseline.XXXXXX")
  decision=$(git -C "$land_root" rev-parse --path-format=absolute --git-common-dir)/harborline-api-quality-decision.json
  if ! ( cd "$land_root" && node eng/quality-step.mjs --write-baseline "$candidate" ); then
    rm -f "$candidate"
    return 1
  fi
  if cmp -s "$candidate" "$baseline"; then
    rm -f "$candidate"
    echo 'land: quality baseline unchanged'
    return 0
  fi
  counts=$(node - "$decision" <<'NODE'
const {readFileSync} = require('node:fs')
const decision = JSON.parse(readFileSync(process.argv[2], 'utf8'))
const fresh = decision.findings.filter(finding => finding.baselineState === 'new').length
const resolved = decision.resolved.length
console.log(`${fresh} ${resolved}`)
NODE
)
  read -r new resolved <<<"$counts"
  rm -f "$candidate"
  echo "land: quality baseline moved: $new new, $resolved resolved (re-pin on the branch and regate)" >&2
  return 1
}
