#!/usr/bin/env bash
# Re-run the ck-11 rekey file with the new rekey-audit test, after the resolver retry.
set -u
OUT=/c/hl-r4t984/reports
end=$((SECONDS+5*3600))
until grep -q "END ck11-resolver-retry" $OUT/status.log; do [ $SECONDS -gt $end ] && { echo "$(date -Is) run3 GAVE-UP" >> $OUT/status.log; exit 1; }; sleep 30; done
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$WT"
echo "$(date -Is) START ck11-rekey-final" >> $OUT/status.log
timeout 3600 node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '**/Data/Authorization/RetiredDesktopActorRekey.cs' --filter 'FullyQualifiedName~DesktopActorRekeyTests' > $OUT/ck11-rekey-final.log 2>&1
rc=$?
cp .stryker/tests-scoped-RetiredDesktopActorRekey.cs/reports/mutation-report.json $OUT/ck11-rekey-final.mutation-report.json && cp apps/local-node-host/tests/.stryker/tests-scoped-RetiredDesktopActorRekey.cs.json $OUT/ck11-rekey-final.config.json
echo "$(date -Is) END ck11-rekey-final rc=$rc" >> $OUT/status.log
