#!/usr/bin/env bash
# Ticket 008 checklist step 10 / 16d — analyzer-attach canary.
#
# Every discovered canary project MUST fail to compile ON THE SPECIFIC DIAGNOSTIC. Asserting merely that
# the build failed proves nothing: a syntax error in the canary would also fail it. That is not
# hypothetical — the first draft of these canaries failed on CS8955 while the analyzer never ran.
#
# Covers both MSBuild predicates separately (Harborline.Blocks.* and Harborline.Foundation*), because
# they are distinct StartsWith checks. Re-point BOTH the canary names and the predicates together
# during a rename: the provider-neutrality predicate exists twice (Directory.Build.props for
# attachment, ProviderNeutralityAnalyzer.IsTargetCompilation for action).
set -uo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
expected="HARBORLINE_API_PROVNEUT_001"
status=0
projects=(
  "$root"/eng/canary/*/*.csproj
  "$root"/apps/provider-neutrality*-canary/*.csproj
)
for project in "${projects[@]}"; do
  name=$(basename "$project" .csproj)
  output=$(dotnet build "$project" -c Release --nologo 2>&1)
  if grep -q "$expected" <<<"$output"; then
    echo "canary OK   $name — fails on $expected as required"
  else
    echo "canary FAIL $name — $expected did NOT fire; the analyzer fence is not effective" >&2
    grep -E "error|Build succeeded" <<<"$output" | head -3 >&2
    status=1
  fi
done

suppression_ids=$(rg --no-filename -o '#pragma warning disable HARBORLINE_API_PROVNEUT_[A-Za-z0-9_]+' \
  --glob '*.cs' --glob '!**/bin/**' --glob '!**/obj/**' "$root" |
  awk '{print $4}' | sort -u)
unexpected_suppressions=$(grep -vFx "$expected" <<<"$suppression_ids" || true)
if [[ -n "$unexpected_suppressions" ]]; then
  echo "suppression audit FAIL — unexpected provider-neutrality diagnostic id(s):" >&2
  echo "$unexpected_suppressions" >&2
  status=1
else
  suppression_count=$(rg --no-filename -o '#pragma warning disable HARBORLINE_API_PROVNEUT_[A-Za-z0-9_]+' \
    --glob '*.cs' --glob '!**/bin/**' --glob '!**/obj/**' "$root" |
    wc -l | tr -d ' ')
  echo "suppression audit OK — $suppression_count suppression(s) name $expected"
fi
exit $status
