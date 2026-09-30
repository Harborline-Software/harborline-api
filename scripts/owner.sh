#!/usr/bin/env bash
# Hand check for RosterCrdtProjection.cs:1117 (AuthorityFor returns Owner for every party), which Stryker reported as Timeout.
set -u
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api-manual
OUT=/c/hl-r4t984/manual
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$WT"
T=apps/local-node-host/tests/tests.csproj
S=apps/local-node-host/Data/Roster/RosterCrdtProjection.cs
id=ck11-preinsert-1117-owner
perl -pi -e 's/string\.Equals\(partyId, chain\.GenesisPartyId, StringComparison\.Ordinal\)/true/ if $.==1117' $S
git diff -- $S > $OUT/$id.diff
if [ -s $OUT/$id.diff ] && timeout 1800 dotnet build $T -nodeReuse:false -v:q -nologo > $OUT/$id.build.log 2>&1; then
  timeout 900 dotnet test $T --no-build -nodeReuse:false --filter 'FullyQualifiedName~RosterPreInsertVerificationTests' --logger "trx;LogFileName=$OUT/$id.trx" > $OUT/$id.test.log 2>&1
  echo "$(date -Is) $id $(grep -E 'Passed!|Failed!' $OUT/$id.test.log | tail -1)" >> $OUT/status.log
else echo "$(date -Is) $id not-applied-or-build-failed" >> $OUT/status.log; fi
git checkout -- $S
echo "$(date -Is) DONE-OWNER" >> $OUT/status.log
