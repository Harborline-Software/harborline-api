#!/usr/bin/env bash
# T-984 r4 manual mutations: apply, build, run the named tests, restore. One at a time.
set -u
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api-manual
OUT=/c/hl-r4t984/manual
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$WT"
T=apps/local-node-host/tests/tests.csproj
build() { timeout 1800 dotnet build $T -nodeReuse:false -v:q -nologo > $OUT/$1.build.log 2>&1; }
tst() { timeout 1800 dotnet test $T --no-build -nodeReuse:false --filter "$2" --logger "trx;LogFileName=$OUT/$1.trx" > $OUT/$1.test.log 2>&1; echo $?; }
mut() { # id file perl-expr filter
  local id=$1 file=$2 expr=$3 filter=$4
  perl -pi -e "$expr" "$file"
  git diff -- "$file" > $OUT/$id.diff
  if [ ! -s $OUT/$id.diff ]; then echo "$(date -Is) $id NOT-APPLIED" >> $OUT/status.log; return; fi
  if build $id; then rc=$(tst $id "$filter"); else rc=build-failed; fi
  git checkout -- "$file"
  echo "$(date -Is) $id test-exit=$rc $(grep -E 'Passed!|Failed!' $OUT/$id.test.log | tail -1)" >> $OUT/status.log
}
R=apps/local-node-host/Data/Identity/NodeAuthorizationRosterConstraintReader.cs
S=apps/local-node-host/Data/Identity/SelectedSessionPermissionResolver.cs
G=packages/foundation-authorization/AuthorizationGate.cs
A=apps/local-node-host/Health/AssetRegistryRoutes.cs
SD=apps/local-node-host/Health/SchedulingDefinitionRoutes.cs
FR=apps/local-node-host/Health/FormsRoutes.cs
F_246='FullyQualifiedName~DesktopActorRosterIdentityTests'
F_PEP='FullyQualifiedName~SelectedSessionPepTests'
F_519='FullyQualifiedName~RosterGateDecisionTests'
F_975='FullyQualifiedName~PackBoundSelectedPrincipalTests'
F_SD='FullyQualifiedName~SchedulingDefinitionRouteTests&FullyQualifiedName~refuses_a_client_supplied_record_id'
F_FR='FullyQualifiedName~FormsRouteTests&FullyQualifiedName~refuses_a_client_supplied_record_id'
# control: the unmutated tree
build control && echo "$(date -Is) control test-exit=$(tst control "$F_246|$F_PEP|$F_519|$F_975|FullyQualifiedName~refuses_a_client_supplied_record_id") $(grep -E 'Passed!|Failed!' $OUT/control.test.log | tail -1)" >> $OUT/status.log
mut ck11-246-lookup-null $R 's/party\?\.PartyId\.Value \?\? principal\.Value/principal.Value/ if $.==36' "$F_246"
mut ck11-276-absence-refuses $S 's/if \(inputs\.Ejected\) return null;/if (inputs.Ejected || !inputs.Member) return null;/ if $.==144' "$F_PEP"
mut ck11-276-285-ejection-removed $S 's/if \(inputs\.Ejected\) return null;/\/\/ mutated: ejection guard removed/ if $.==144' "$F_PEP"
mut ck5-t519-m2-grantrefusal $G 's/^(\s*)GrantRefusal = null,/$1\/\/ mutated: GrantRefusal = null removed/ if $.==124' "$F_519"
for l in 152 201 283 564; do
  mut ck5-t975-tenant-$l $A "s/http\.Features\.Get<SelectedSessionRequestPrincipal>\(\)\?\.TenantId// if \$.==$l; s/\?\? NodeTenant/NodeTenant/ if \$.==$((l+1))" "$F_975"
done
mut ck3-t974-sched-219 $SD 's/^(\s*)if \(request\.Id is not null \|\| request\.EventId is not null\)/$1if (Environment.ProcessorCount < 0)/ if $.==219' "$F_SD"
mut ck3-t974-sched-277 $SD 's/^(\s*)if \(request\.Id is not null \|\| request\.EventId is not null\)/$1if (Environment.ProcessorCount < 0)/ if $.==277' "$F_SD"
mut ck3-t974-forms-170 $FR 's/^(\s*)if \(body\.TryGetProperty\("instanceId", out _\)\)/$1if (Environment.ProcessorCount < 0)/ if $.==170' "$F_FR"
echo "$(date -Is) DONE" >> $OUT/status.log
