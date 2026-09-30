#!/usr/bin/env bash
# T-984 r4: api #246's lookup as it stands after #247 (NodeOperatorIdentity.PartyOf).
set -u
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api-manual
OUT=/c/hl-r4t984/manual
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$WT"
T=apps/local-node-host/tests/tests.csproj
build() { timeout 1800 dotnet build $T -nodeReuse:false -v:q -nologo > $OUT/$1.build.log 2>&1; }
tst() { timeout 1800 dotnet test $T --no-build -nodeReuse:false --filter "$2" --logger "trx;LogFileName=$OUT/$1.trx" > $OUT/$1.test.log 2>&1; echo $?; }
mut() {
  local id=$1 file=$2 expr=$3 filter=$4
  perl -0pi -e "$expr" "$file"
  git diff -- "$file" > $OUT/$id.diff
  if [ ! -s $OUT/$id.diff ]; then echo "$(date -Is) $id NOT-APPLIED" >> $OUT/status.log; return; fi
  if build $id; then rc=$(tst $id "$filter"); else rc=build-failed; fi
  git checkout -- "$file"
  echo "$(date -Is) $id test-exit=$rc $(grep -E 'Passed!|Failed!' $OUT/$id.test.log | tail -1)" >> $OUT/status.log
}
O=apps/local-node-host/Data/Identity/NodeOperatorIdentity.cs
F='FullyQualifiedName~DesktopActorRosterIdentityTests'
mut ck11-246-partyof-null $O 's/(internal static string\? PartyOf\(MemberRoster roster, IOperationSigner signer\) =>)\s*roster\.Members[^;]*;/$1 null;/s' "$F"
mut ck11-246-no-retained-admissions $O 's/\?\.PartyId\s*\?\? roster\.EnumerateAdmissions\(\)[^;]*;/?.PartyId;/s' "$F"
echo "$(date -Is) DONE2" >> $OUT/status.log
