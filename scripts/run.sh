#!/usr/bin/env bash
# T-984 r4 api Stryker driver: one run at a time, each with a deadline.
set -u
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api
OUT=/c/hl-r4t984/reports
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$WT"
f() { local IFS='|'; local parts=(); for c in "$@"; do parts+=("FullyQualifiedName~$c"); done; echo "${parts[*]}"; }
run() { # id seconds args...
  local id=$1 secs=$2; shift 2
  echo "$(date -Is) START $id" >> $OUT/status.log
  timeout "$secs" node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj "$@" > $OUT/$id.log 2>&1
  local rc=$?
  local rep=$(grep -o 'apps/local-node-host/tests/.stryker/[^ ]*mutation-report.json' $OUT/$id.log | tail -1)
  [ -n "$rep" ] && [ -f "$rep" ] && cp "$rep" $OUT/$id.mutation-report.json && cp "$(dirname $(dirname $rep)).json" $OUT/$id.config.json 2>/dev/null
  echo "$(date -Is) END $id rc=$rc report=${rep:-none}" >> $OUT/status.log
}
ONLY=${1:-all}
if [ "$ONLY" = all ] || [ "$ONLY" = ck11 ]; then
run ck11-reader 3600 --scoped '**/Data/Identity/NodeAuthorizationRosterConstraintReader.cs' --filter "$(f DesktopActorRosterIdentityTests AdminTeamAccessAuthorityTests RosterGateDecisionTests AttributionIsNotAuthorityTests NodeAuthorizationRosterConstraintReaderTests)"
run ck11-resolver 3600 --scoped '**/Data/Identity/SelectedSessionPermissionResolver.cs' --filter "$(f SelectedSessionEffectivePermissionsTests SelectedSessionPepTests)"
run ck11-gate 5400 --scoped '**/AuthorizationGate.cs' --project Harborline.Foundation.Authorization.csproj --filter "$(f RosterGateDecisionTests AttributionIsNotAuthorityTests SelectedSessionPepTests NodeAuthorizationRosterConstraintReaderTests)"
run ck11-rekey 3600 --scoped '**/Data/Authorization/RetiredDesktopActorRekey.cs' --filter "$(f DesktopActorRekeyTests)"
run ck11-operator 3600 --scoped '**/Data/Identity/NodeOperatorIdentity.cs' --filter "$(f DesktopActorRosterIdentityTests DesktopActorRekeyTests NodeOperatorIdentityTests)"
run ck11-preinsert 5400 --scoped '**/Data/Roster/RosterCrdtProjection.cs' --filter "$(f RosterPreInsertVerificationTests)"
fi
if [ "$ONLY" = all ] || [ "$ONLY" = rest ]; then
run t975-asset 3600 --scoped '**/Health/AssetRegistryRoutes.cs' --filter 'FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.AssetRegistry.'
eval run t974-guards 3600 $(cat /c/hl-r4t984/spans.txt) --filter "'FullyQualifiedName~refuses_a_client_supplied_record_id'"
run t519-gate 9000 --scoped '**/AuthorizationGate.cs' --project Harborline.Foundation.Authorization.csproj --filter 'FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.Authorization.|FullyQualifiedName~Harborline.Api.LocalNodeHost.Tests.Identity.'
fi
echo "$(date -Is) DONE $ONLY" >> $OUT/status.log
