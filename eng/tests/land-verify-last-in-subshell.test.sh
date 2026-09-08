#!/usr/bin/env bash
# Ticket 333: a nested `bash eng/verify.sh` under a held gate lock must be the LAST command of its subshell.
# Bash execs the last command of `( ... )` in the subshell process, whose parent is the lock owner, so the
# lock's parent-pid re-entrancy rule admits it. Any command appended after it forks verify.sh one level
# deeper and land.sh deadlocks on itself (api #27 hung 70 minutes). Red on 6ee7c98a, green after the split.
set -euo pipefail
root=$(git rev-parse --show-toplevel)
bad=$(grep -n 'bash eng/verify.sh &&' "$root/eng/land.sh" || true)
if [ -n "$bad" ]; then echo "FAIL: verify.sh is followed by another command in its subshell:"; echo "$bad"; exit 1; fi
count=$(grep -c 'bash eng/verify.sh )' "$root/eng/land.sh")
[ "$count" -ge 2 ] || { echo "FAIL: expected verify.sh to close its subshell in land.sh twice (land worktree and gate_main), found $count"; exit 1; }
echo "PASS land-verify-last-in-subshell ($count nested verify runs close their subshell)"
