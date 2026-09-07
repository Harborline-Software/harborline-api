#!/usr/bin/env bash
# legacy-env-prefix-scan.sh [repo_root] — ticket 245.
#
# Invariant: the 21 operational environment variables of the capability host carry the
# CAPABILITY_HOST_ prefix, and the retired-family prefix they used to carry appears NOWHERE in
# tracked text. Ticket 245 renamed them as a CLEAN BREAK — no compatibility window, no dual-read,
# no fallback (all installs are local dev; the 2026-08-20 ruling). Ticket 260 slice S-I kept a
# documented legacy fallback for its five operator-facing HARBORLINE_* renames; that set and this
# one are DISJOINT (different retired prefix, different variables), so the two rules do not meet.
#
# A runtime refusal is not enough on its own: it only bites on a machine where the stale name is
# actually exported. This fence bites in the tree, so a reader reintroduced by a copy-paste is a
# red gate rather than a support call.
#
# The retired prefix is assembled from character codes, not written out, for the same reason
# eng/verify-boundaries.sh and eng/dangling-token-scan.sh do it: a guard that spells the word it
# retires plants a fresh copy of that word in the tree and shows up in every scan for it.
#
# Allow-list: eng/legacy-env-prefix-allowlist.tsv. Exact rows, no wildcard — one row per tracked
# FILE, `<path>\t<class>\t<reason>`. The only legitimate class is a site that must NAME the retired
# prefix in order to refuse it (or a pinned baseline that records history). The allow-list must
# EQUAL the discovered set: a row whose file no longer matches is a stale row and fails too, so the
# list cannot rot into a wildcard.
#
# POSITIVE CONTROL, per docs/adr/0010 in harborline-control. To confirm this guard bites, write the
# retired prefix followed by a name into any tracked file outside the allow-list and run this
# script: it must exit 1 and print that file. eng/tests/legacy-env-prefix-scan.test.sh enumerates
# that, and the stale-row case, against throwaway repositories.
set -uo pipefail
repo_root=${1:-$(git rev-parse --show-toplevel)}
allowlist="$repo_root/eng/legacy-env-prefix-allowlist.tsv"

prefix=$(printf '\x48\x55\x4c\x4c\x5f')

# ANY occurrence of the retired prefix, not just prefix-followed-by-a-name. A reader that splits the
# literal (`'<prefix>' + 'DEV'`) reads the same variable and must be found; the refusal guards below
# declare the bare prefix and are covered by exact allow-list rows rather than by a looser pattern.
matches=$(git -C "$repo_root" grep -lIF "$prefix" -- . || true)

allowed_files=$(if [ -f "$allowlist" ]; then
  grep -vE '^[[:space:]]*(#|$)' "$allowlist" | grep -E '^[^	]+	[^	]+	.+$' | cut -f1
fi)

offenders=""
for file in $matches; do
  printf '%s\n' "$allowed_files" | grep -qxF "$file" && continue
  offenders+="LEGACY-ENV-PREFIX $file"$'\n'
done

# The allow-list equals the discovered set: no row survives its match.
stale=""
for file in $allowed_files; do
  [ -z "$file" ] && continue
  printf '%s\n' "$matches" | grep -qxF "$file" && continue
  stale+="STALE-ALLOWLIST-ROW $file"$'\n'
done

if [ -n "$offenders" ]; then
  echo "A retired-prefix capability-host environment variable is read or named in tracked text." >&2
  echo "Ticket 245 renamed all 21 to CAPABILITY_HOST_* as a clean break: rename the reader. There is" >&2
  echo "no fallback. A site that must name the old prefix to REFUSE it earns an exact row with a" >&2
  echo "reason in eng/legacy-env-prefix-allowlist.tsv:" >&2
  printf '%s' "$offenders" >&2
fi
if [ -n "$stale" ]; then
  echo "eng/legacy-env-prefix-allowlist.tsv has rows that no longer match anything; delete them:" >&2
  printf '%s' "$stale" >&2
fi
[ -z "$offenders" ] && [ -z "$stale" ]
