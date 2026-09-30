#!/usr/bin/env bash
# Red-first for the rekey audit event type: the test passes unmutated and fails with the type emptied (Stryker 24:68).
set -u
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api-manual
OUT=/c/hl-r4t984/manual
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$WT"
T=apps/local-node-host/tests/tests.csproj
S=apps/local-node-host/Data/Authorization/RetiredDesktopActorRekey.cs
F='FullyQualifiedName~DesktopActorRekeyTests.The_rekey_audit_is_typed_and_names_the_moved_grant_and_both_subjects'
one() { timeout 1800 dotnet build $T -nodeReuse:false -v:q -nologo > $OUT/$1.build.log 2>&1 || { echo "$(date -Is) $1 build-failed" >> $OUT/status.log; return; }
  timeout 900 dotnet test $T --no-build -nodeReuse:false --filter "$F" > $OUT/$1.test.log 2>&1; echo "$(date -Is) $1 $(grep -E 'Passed!|Failed!' $OUT/$1.test.log | tail -1)" >> $OUT/status.log; }
one rekey-eventtype-control
perl -pi -e 's/new\("AuthorizationGrantSubjectRekeyed"\)/new("")/ if $.==24' $S; git diff -- $S > $OUT/rekey-eventtype-empty.diff
one rekey-eventtype-empty
git checkout -- $S apps/local-node-host/tests/Identity/DesktopActorRekeyTests.cs
echo "$(date -Is) DONE-EVENTTYPE" >> $OUT/status.log
