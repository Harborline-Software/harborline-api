#!/usr/bin/env bash
# identity-r3-scan.sh [--advisory] [repo_root] — ticket 260 slice 28.
#
# Runs harborline-control's identity checker (tools/scan-identity-standard.mjs) against THIS
# checkout with --fail-on-r3, so an api landing cannot pass with a retired-family spelling in
# tracked text.
#
# WHY THE CHECKER IS NOT VENDORED HERE. The checker owns the family list, the domain exemptions and
# the exemption ledger (tools/era-token-exemptions.json), and those are shared by five repositories.
# A copy in this repository is a second source of truth for one fact, which is exactly what the
# checker's own header refuses. eng/dangling-token-scan.sh vendors two *character sequences* and
# says so; it does not vendor the ledger, and it names the control checker as the source of its
# families (eng/dangling-token-scan.sh:17). So: resolve a control checkout, run the real tool.
#
# HOW THE CHECKOUT IS RESOLVED, and what happens without one. Same shape as harborline-platform's
# cross-repository feed resolver (tooling/resolve-appshell-feed.mjs:80-87, localPathEnv in
# tooling/appshell-feed-provenance.json): an environment variable names a local checkout, with a
# documented default. Here the default is the sibling directory ../harborline-control, which is the
# standard layout on every machine that runs this gate. If neither resolves to a checkout carrying
# the checker, this script EXITS 1 and says so. It never skips: a guard that quietly passes on a
# machine that cannot run it reports coverage nobody has.
#
# ADVISORY MODE. --advisory prints the count and exits 0. The api count is above zero at this pin
# (see the report for the measurement; ticket 267 owns the tail), and a step that lands red is a
# step that gets commented out. THE ONE-LINE CHANGE THAT MAKES THIS BLOCKING: drop `--advisory`
# from the `step identity-r3` line in eng/verify.sh. Nothing else changes.
#
# POSITIVE CONTROL, per docs/adr/0010 in harborline-control. To confirm this guard bites, write a
# retired family word into any tracked file and run this script WITHOUT --advisory: it must exit 1
# and print a non-zero count. Do that after any edit here.
set -uo pipefail

advisory=0
if [ "${1:-}" = "--advisory" ]; then advisory=1; shift; fi
repo_root=${1:-$(git rev-parse --show-toplevel)}
repo_root=$(cd "$repo_root" && pwd)

# The sibling default is taken from the MAIN checkout, not from this working tree: a git worktree
# under .claude/worktrees/<lane> has no sibling control checkout, but its common git directory does.
main_root=$(dirname "$(git -C "$repo_root" rev-parse --path-format=absolute --git-common-dir)")
control=${HARBORLINE_CONTROL_REPO:-$main_root/../harborline-control}
checker="$control/tools/scan-identity-standard.mjs"
if [ ! -f "$checker" ]; then
  echo "identity-r3: no harborline-control checkout at $control" >&2
  echo "  Set HARBORLINE_CONTROL_REPO to a harborline-control checkout, or place one beside this repository." >&2
  echo "  This check is not skippable: it is the only thing standing between a landing and a retired spelling." >&2
  exit 1
fi

# The checker must be one that understands --repo-root. An older checker ignores unknown flags,
# scans zero files under a mis-resolved root, prints R3 0 and exits 0 — a green run that checked
# nothing (review round 1 reproduced exactly that against a stale sibling checkout).
if ! grep -q -- '--repo-root' "$checker"; then
  echo "identity-r3: the checker at $checker predates --repo-root scoping; update the harborline-control checkout." >&2
  exit 1
fi

# --repo/--repo-root scope the scan to this repository's checkout: residues in the sibling
# repositories are not this gate's business, and this checkout is often a worktree or a clean clone
# whose directory is not named after the repository. The JSON report is read for two numbers: the
# R3 count (printed) and the scanned-file count (a scan that read nothing is a failure, not a pass).
report=$(node "$checker" --root "$repo_root/.." --repo harborline-api --repo-root "$repo_root" --fail-on-r3 --json 2>&1)
rc=$?
read -r scanned r3 <<<"$(printf '%s' "$report" | node -e '
  let s = ""; process.stdin.on("data", d => s += d).on("end", () => {
    const i = s.indexOf("{"), j = s.lastIndexOf("}");
    try { const r = JSON.parse(s.slice(i, j + 1)); const f = (r.r3 && (r.r3.scannedFiles ?? r.r3.files)) || 0; const hits = (r.r3 && r.r3.hits) || []; for (const h of hits.slice(0, 40)) console.error(`  ${h.file}:${h.line}  [${h.family}]`); if (hits.length > 40) console.error(`  ... ${hits.length - 40} more`); console.log(Array.isArray(f) ? f.length : f, (r.counts && r.counts.R3) || 0) }
    catch { console.log(0, -1) }
  })')"
echo "identity-r3: scanned $scanned tracked files under $repo_root; R3 unexempted = $r3"
if [ "${scanned:-0}" -eq 0 ] || [ "${r3:-0}" -lt 0 ]; then
  echo "identity-r3: the scan read no files (or produced no report); refusing to call that a pass." >&2
  exit 1
fi

if [ $rc -ne 0 ] && [ $advisory -eq 1 ]; then
  echo "identity-r3: ADVISORY — the count above is not zero and this step is not blocking yet (ticket 267 owns the tail)."
  exit 0
fi
exit $rc
