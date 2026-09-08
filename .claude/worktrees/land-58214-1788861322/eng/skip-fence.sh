#!/usr/bin/env bash
# skip-fence.sh [repo_root] — R-0006 lesson 2 (ticket 242): a disabled test carries its ticket in code.
# Any xunit [Fact]/[Theory] attribute with a Skip argument — whatever the right-hand side (string,
# verbatim/raw string, or a named constant) — must carry a ticket reference on the same attribute line:
# T-NNN, ticket NNN, or tickets/NNN-. Named constants are flagged by design: the reason must be readable
# at the call site. Exit 1 with the offending lines on stderr; exit 0 otherwise. Baseline: zero skips.
set -uo pipefail
repo_root=${1:-$(git rev-parse --show-toplevel)}
# Selector: an attribute opening [Fact or [Theory (word boundary), then anything but ']' up to a Skip
# named argument. Regex is ERE; \b is GNU grep's word boundary.
skips=$(git -C "$repo_root" grep -nE '\[(Fact|Theory)\b[^]]*\bSkip[[:space:]]*=' -- '*.cs' ':!**/bin/**' ':!**/obj/**' || true)
bad_skips=$(printf '%s\n' "$skips" | grep -vE '\bT-[0-9]{3}\b|\b[Tt]icket [0-9]{3}\b|tickets/[0-9]{3}-' | grep -v '^$' || true)
if [ -n "$bad_skips" ]; then
  echo "A skipped test must name its ticket in the Skip attribute (T-NNN / ticket NNN / tickets/NNN-):" >&2
  printf '%s\n' "$bad_skips" >&2
  exit 1
fi
exit 0
