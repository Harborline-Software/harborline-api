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

# Ticket 340's canary is intentionally outside Harborline.Api.slnx so the host
# baseline has no new row. First build it as non-packable: this follows the same
# source but omits the threading analyzer reference, and the assertion is red
# because its SARIF lacks the two VSTHRD IDs. Then build the production-shaped
# project and require all three upstream IDs plus the SARIF fields CQG consumes.
quality_canary="$root/eng/quality-analyzer-canary/Harborline.Quality.AnalyzerCanary.csproj"
quality_dir="$root/artifacts/quality"
without_threading="$quality_dir/roslyn-canary-without-threading.sarif"
with_threading="$quality_dir/roslyn-canary.sarif"
mkdir -p "$quality_dir"
rm -f "$without_threading" "$with_threading"

set +e
without_output=$(dotnet build "$quality_canary" -c Release --nologo --no-restore -nodeReuse:false -maxcpucount:6 \
  -p:IsPackable=false "-p:ErrorLog=\"$without_threading,version=2.1\"" 2>&1)
without_status=$?
set -e
if [[ $without_status -ne 0 || ! -f "$without_threading" ]]; then
  echo "quality canary FAIL — unattached control build did not emit SARIF" >&2
  echo "$without_output" | tail -20 >&2
  status=1
elif ! node "$root/eng/normalize-roslyn-sarif.mjs" "$without_threading"; then
  echo "quality canary FAIL — unattached control emitted invalid SARIF" >&2
  status=1
elif node -e '
const sarif = require(process.argv[1]);
const ids = new Set(sarif.runs.flatMap(run => run.results ?? []).map(result => result.ruleId));
process.exit(ids.has("VSTHRD002") || ids.has("VSTHRD100") ? 0 : 1)
' "$without_threading"; then
  echo "quality canary FAIL — unattached control unexpectedly found threading diagnostics" >&2
  status=1
else
  echo "quality canary RED recorded — unattached control lacks VSTHRD002 and VSTHRD100"
fi

set +e
with_output=$(dotnet build "$quality_canary" -c Release --nologo --no-restore -nodeReuse:false -maxcpucount:6 \
  "-p:ErrorLog=\"$with_threading,version=2.1\"" 2>&1)
with_status=$?
set -e
if [[ ! -f "$with_threading" ]]; then
  echo "quality canary FAIL — attached build did not emit SARIF" >&2
  echo "$with_output" | tail -20 >&2
  status=1
elif ! node "$root/eng/normalize-roslyn-sarif.mjs" "$with_threading"; then
  echo "quality canary FAIL — attached build emitted invalid SARIF" >&2
  status=1
elif node -e '
const sarif = require(process.argv[1]);
const results = sarif.runs.flatMap(run => run.results ?? []);
const ids = new Set(results.map(result => result.ruleId));
const valid = sarif.version === "2.1.0"
  && typeof sarif.runs[0]?.tool?.driver?.name === "string"
  && ["VSTHRD002", "VSTHRD100", "CA1031"].every(id => ids.has(id))
  && results.every(result => typeof result.ruleId === "string"
    && typeof result.locations?.[0]?.physicalLocation?.artifactLocation?.uri === "string"
    && Number.isInteger(result.locations?.[0]?.physicalLocation?.region?.startLine)
    && result.partialFingerprints && typeof result.partialFingerprints === "object");
process.exit(valid ? 0 : 1)
' "$with_threading"; then
  if [[ $with_status -eq 0 ]]; then
    echo "quality canary FAIL — attached violations compiled without VSTHRD100's configured error" >&2
    status=1
  else
    echo "quality canary OK — VSTHRD002, VSTHRD100 and CA1031 appear in SARIF"
  fi
else
  echo "quality canary FAIL — attached SARIF is missing a required rule or CQG field" >&2
  echo "$with_output" | tail -20 >&2
  status=1
fi
exit $status
