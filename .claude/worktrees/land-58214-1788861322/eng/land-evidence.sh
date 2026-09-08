# land-evidence.sh — sourced by eng/land.sh; also sourced by eng/tests/land-evidence.test.sh.

# preserve_land_evidence <root> <land_dir> <head_sha>
#   Copies a red merge-tree gate's exact-clone report outside the throwaway worktree, then prints
#   each failed step's persisted (already scrubbed and ANSI-free) tail for a self-contained log.
preserve_land_evidence() {
  local root=$1 land_dir=$2 head_sha=$3
  local source="$land_dir/.claude/land-evidence/exact-clone-fail.json"
  if [ ! -f "$source" ]; then
    echo "land: exact-clone evidence was not written at $source" >&2
    return 1
  fi

  local evidence_dir="$root/.claude/land-evidence"
  local timestamp destination
  timestamp=$(date -u +%Y%m%dT%H%M%SZ)
  destination="$evidence_dir/$timestamp-$head_sha.json"
  mkdir -p "$evidence_dir"
  cp "$source" "$destination"
  echo "land: exact-clone evidence: $destination"

  node - "$destination" <<'NODE'
const {readFileSync} = require('node:fs')
const report = JSON.parse(readFileSync(process.argv[2], 'utf8'))
for (const step of report.steps.filter(step => step.passed === false)) {
  process.stdout.write(`${step.id}:\n`)
  for (const line of String(step.tail ?? '').split('\n')) process.stdout.write(`  ${line}\n`)
}
NODE
}
