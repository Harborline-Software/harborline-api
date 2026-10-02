set -euo pipefail
node eng/build-local-feed.mjs
set -o pipefail
mkdir -p .claude/gate-evidence
dotnet test apps/local-node-host/tests/tests.csproj -c Release --nologo -nodeReuse:false             --filter "Lane=perf"             --logger "trx;LogFileName=perf-tests.trx" --results-directory .claude/gate-evidence             --logger "console;verbosity=detailed"             -- RunConfiguration.TreatNoTestsAsError=true | tee .claude/gate-evidence/perf-tests.log
echo "## Layout timing parity (KS)" >> "$GITHUB_STEP_SUMMARY"
grep -E "KS D = " .claude/gate-evidence/perf-tests.log | sed -E 's/^ *(\[xUnit[^]]*\] *)?//' | sort -u | sed 's/^/- /' >> "$GITHUB_STEP_SUMMARY" || true
