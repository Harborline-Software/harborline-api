#!/usr/bin/env bash
# Red-first proof for the rekey-audit test: pass unmutated, then empty the audit body (Stryker's 54:21 shape) and each field.
set -u
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api-manual
OUT=/c/hl-r4t984/manual
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$WT"
T=apps/local-node-host/tests/tests.csproj
F='FullyQualifiedName~DesktopActorRekeyTests.The_rekey_audit_names_the_moved_grant_and_both_subjects'
S=apps/local-node-host/Data/Authorization/RetiredDesktopActorRekey.cs
one() { timeout 1800 dotnet build $T -nodeReuse:false -v:q -nologo > $OUT/$1.build.log 2>&1 || { echo "$(date -Is) $1 build-failed" >> $OUT/status.log; return; }
  timeout 900 dotnet test $T --no-build -nodeReuse:false --filter "$F" > $OUT/$1.test.log 2>&1; echo "$(date -Is) $1 test-exit=$? $(grep -E 'Passed!|Failed!' $OUT/$1.test.log | tail -1)" >> $OUT/status.log; }
one rekey-audit-control
for m in 's/\["grantId"\] = row\.GrantId,//' 's/\["fromSubject"\] = NodeOperatorIdentity\.RetiredDesktopActor,//' 's/\["toSubject"\] = holder\.Value,//'; do
  id=rekey-audit-$(echo "$m" | grep -o '"[a-zA-Z]*"' | head -1 | tr -d '"')
  perl -pi -e "$m" $S; git diff -- $S > $OUT/$id.diff; one $id; git checkout -- $S
done
echo "$(date -Is) DONE-REKEY" >> $OUT/status.log
