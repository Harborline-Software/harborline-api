#!/usr/bin/env bash
set -u
cd /c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api-manual
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
T=apps/local-node-host/tests/tests.csproj
OUT=/c/hl-r4t984/manual
timeout 1800 dotnet build $T -nodeReuse:false -v:q -nologo > $OUT/focused.build.log 2>&1 && \
timeout 1800 dotnet test $T --no-build -nodeReuse:false --filter 'FullyQualifiedName~.Authorization.|FullyQualifiedName~.Identity.|FullyQualifiedName~Roster|FullyQualifiedName~AssetRegistry|FullyQualifiedName~NodeAuditOutboxTests|FullyQualifiedName~refuses_a_client_supplied_record_id' --logger "trx;LogFileName=$OUT/focused.trx" > $OUT/focused.test.log 2>&1
echo "$(date -Is) focused $(grep -E 'Passed!|Failed!' $OUT/focused.test.log | tail -1)" >> $OUT/status.log
echo "$(date -Is) DONE-FOCUSED" >> $OUT/status.log
