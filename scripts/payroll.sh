#!/usr/bin/env bash
# Name the killer of the PayrollRoutes.cs:108 `||` to `&&` mutant, which Stryker reported as Timeout.
set -u
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api-manual
OUT=/c/hl-r4t984/manual
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$WT"
T=apps/local-node-host/tests/tests.csproj
S=apps/local-node-host/Health/PayrollRoutes.cs
id=ck3-t974-payroll-108-and
perl -pi -e 's/if \(body\.Id is not null \|\| body\.EmployeeId is not null\)/if (body.Id is not null && body.EmployeeId is not null)/ if $.==108' $S
git diff -- $S > $OUT/$id.diff
if [ -s $OUT/$id.diff ] && timeout 1800 dotnet build $T -nodeReuse:false -v:q -nologo > $OUT/$id.build.log 2>&1; then
  timeout 900 dotnet test $T --no-build -nodeReuse:false --filter 'FullyQualifiedName~PayrollRouteTests.CreateEmployee_refuses_a_client_supplied_record_id' --logger "trx;LogFileName=$OUT/$id.trx" > $OUT/$id.test.log 2>&1
  echo "$(date -Is) $id $(grep -E 'Passed!|Failed!' $OUT/$id.test.log | tail -1)" >> $OUT/status.log
else echo "$(date -Is) $id not-applied-or-build-failed" >> $OUT/status.log; fi
git checkout -- $S
echo "$(date -Is) DONE-PAYROLL" >> $OUT/status.log
