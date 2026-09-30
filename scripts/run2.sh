#!/usr/bin/env bash
# Retry the ck-11 resolver run (the first crashed on vstest connection timeouts under host load), after the driver finishes.
set -u
OUT=/c/hl-r4t984/reports
end=$((SECONDS+5*3600))
until grep -q "DONE all" $OUT/status.log; do [ $SECONDS -gt $end ] && { echo "$(date -Is) run2 GAVE-UP" >> $OUT/status.log; exit 1; }; sleep 30; done
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$WT"
echo "$(date -Is) START ck11-resolver-retry" >> $OUT/status.log
timeout 3600 node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '**/Data/Identity/SelectedSessionPermissionResolver.cs' --filter 'FullyQualifiedName~SelectedSessionEffectivePermissionsTests|FullyQualifiedName~SelectedSessionPepTests' > $OUT/ck11-resolver-retry.log 2>&1
rc=$?
cp .stryker/tests-scoped-SelectedSessionPermissionResolver.cs/reports/mutation-report.json $OUT/ck11-resolver.mutation-report.json && cp apps/local-node-host/tests/.stryker/tests-scoped-SelectedSessionPermissionResolver.cs.json $OUT/ck11-resolver.config.json
echo "$(date -Is) END ck11-resolver-retry rc=$rc" >> $OUT/status.log
