#!/usr/bin/env bash
# Name the killer of NodeOperatorIdentity.cs:23 `&&` to `||` (Stryker 13256, Timeout).
set -u
cd /c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api-manual
OUT=/c/hl-r4t984/manual
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
T=apps/local-node-host/tests/tests.csproj
S=apps/local-node-host/Data/Identity/NodeOperatorIdentity.cs
id=ck11-operator-23-or
perl -pi -e 's/roster is not null && signer is not null && PartyOf\(roster\.Current, signer\)/(roster is not null || signer is not null) && PartyOf(roster!.Current, signer!)/ if $.==23' $S; git diff -- $S > $OUT/$id.diff
if [ -s $OUT/$id.diff ] && timeout 1800 dotnet build $T -nodeReuse:false -v:q -nologo > $OUT/$id.build.log 2>&1; then
  timeout 900 dotnet test $T --no-build -nodeReuse:false --filter 'FullyQualifiedName~NodeOperatorIdentityTests' --logger "trx;LogFileName=$OUT/$id.trx" > $OUT/$id.test.log 2>&1
  echo "$(date -Is) $id $(grep -E 'Passed!|Failed!' $OUT/$id.test.log | tail -1)" >> $OUT/status.log
else echo "$(date -Is) $id not-applied-or-build-failed" >> $OUT/status.log; fi
git checkout -- $S
echo "$(date -Is) DONE-OPERATOR" >> $OUT/status.log
