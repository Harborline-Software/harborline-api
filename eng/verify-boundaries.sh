#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)

# The forbidden consumer codename is assembled from character codes rather than written out.
#
# A guard that bans a name by spelling it out defeats its own purpose: a grep for that name across
# this repository finds the guard itself, and the repository is meant to carry no trace of any
# consumer -- in its code, its paths, its fixtures, or its checks. An earlier version of this file
# split the literal across a concatenation so the script would not match ITSELF. That solved a
# smaller, different problem: the name stayed plainly readable to anyone, or any tool, reading
# these files.
#
# printf with hex escapes is used rather than base64 because it needs no external binary and
# behaves identically under bash on Linux, macOS, and Git Bash (macOS base64 spells its decode
# flag differently, which would make this pass vacuously on one platform).
#
# POSITIVE CONTROL, per docs/adr/0010 in harborline-control. A guard nobody has watched fail is
# not yet a guard. To confirm this one still bites, write the decoded string into any tracked file
# and run this script: it must exit 1 and print that file's path. Do that after any edit here.
consumer_name=$(printf '\x43\x6f\x6d\x65\x74\x58')

content_matches=$(git -C "$repo_root" grep -ni "$consumer_name" -- . || true)
path_matches=$(git -C "$repo_root" ls-files | grep -i "$consumer_name" || true)

if [ -n "$content_matches" ] || [ -n "$path_matches" ]; then
  echo "Consumer-specific naming is forbidden in Harborline API." >&2
  [ -z "$path_matches" ] || printf '%s\n' "$path_matches" >&2
  [ -z "$content_matches" ] || printf '%s\n' "$content_matches" >&2
  exit 1
fi

# R-0006 lesson 2 (ticket 242): a disabled test carries its ticket in code (eng/skip-fence.sh; enumerated by
# eng/tests/skip-fence.test.sh).
bash "$repo_root/eng/skip-fence.sh" "$repo_root" || exit 1

# Ticket 260 slice 1: doc, comment and csproj text must not name a registration or interface that
# does not exist (eng/dangling-token-scan.sh; enumerated by eng/tests/dangling-token-scan.test.sh).
bash "$repo_root/eng/dangling-token-scan.sh" "$repo_root" || exit 1

# Ticket 245: the 21 capability-host operational environment variables are CAPABILITY_HOST_* as a
# clean break; the retired-family prefix appears in tracked text only at the refusal sites named in
# eng/legacy-env-prefix-allowlist.tsv (eng/legacy-env-prefix-scan.sh; enumerated by
# eng/tests/legacy-env-prefix-scan.test.sh).
bash "$repo_root/eng/legacy-env-prefix-scan.sh" "$repo_root" || exit 1

# Ticket 286: nothing in the repository ran the eng/tests/*.test.sh self-tests, so the gate lock's
# GNU-only `mv -T` shipped and hung the gate on macOS. The portability self-test is cheap (a temp
# repo and a fake mv) and runs here, in the preflight step, rather than as a receipt step id.
bash "$repo_root/eng/tests/gate-lock-portable-mv.test.sh" || exit 1

# Ticket 333: prove nested lock reuse independently of Bash's last-command exec.
bash "$repo_root/eng/tests/gate-lock-reentry.test.sh" || exit 1
bash "$repo_root/eng/tests/land-verify-last-in-subshell.test.sh" || exit 1
bash "$repo_root/eng/tests/land-main-moved.test.sh" || exit 1
bash "$repo_root/eng/tests/land-dirty-tree.test.sh" || exit 1
bash "$repo_root/eng/tests/fixture-git-retry.test.sh" || exit 1

# Ticket 324: exercise the comparison and receipt refusal on the gate's preflight route.
node --test "$repo_root/eng/tests/host-baseline.test.mjs" || exit 1
node --test "$repo_root/eng/tests/platform-feed.test.mjs" || exit 1
node --test "$repo_root/eng/tests/exact-clone-platform-feed.test.mjs" || exit 1
node --test "$repo_root/eng/tests/normalize-roslyn-sarif.test.mjs" || exit 1
node --test "$repo_root/eng/tests/arch-sarif.test.mjs" || exit 1
bash "$repo_root/eng/tests/verify-arch-canary.test.sh" || exit 1
bash "$repo_root/eng/tests/quality-step.test.sh" || exit 1
node --test "$repo_root/eng/tests/quality-artifacts.test.mjs" || exit 1
node --test "$repo_root/eng/tests/receipt-accept.test.mjs" || exit 1
bash "$repo_root/eng/tests/quality-baseline-landing.test.sh" || exit 1
bash "$repo_root/eng/tests/quality-baseline-gate.test.sh" || exit 1

echo "Harborline API consumer-neutral boundary: PASS"
