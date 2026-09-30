#!/usr/bin/env bash
# Re-run the ck-11 rekey file at d882473e (the test now pins the audit event type).
set -u
OUT=/c/hl-r4t984/reports
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$WT"
mv $OUT/ck11-rekey-final.mutation-report.json $OUT/ck11-rekey-8f1caa30.mutation-report.json
mv $OUT/ck11-rekey-final.config.json $OUT/ck11-rekey-8f1caa30.config.json
mv $OUT/ck11-rekey-final.log $OUT/ck11-rekey-8f1caa30.log
echo "$(date -Is) START ck11-rekey-final" >> $OUT/status.log
timeout 3600 node eng/mutation-report.mjs --only apps/local-node-host/tests/tests.csproj --scoped '**/Data/Authorization/RetiredDesktopActorRekey.cs' --filter 'FullyQualifiedName~DesktopActorRekeyTests' > $OUT/ck11-rekey-final.log 2>&1
rc=$?
cp .stryker/tests-scoped-RetiredDesktopActorRekey.cs/reports/mutation-report.json $OUT/ck11-rekey-final.mutation-report.json && cp apps/local-node-host/tests/.stryker/tests-scoped-RetiredDesktopActorRekey.cs.json $OUT/ck11-rekey-final.config.json
echo "$(date -Is) END ck11-rekey-final rc=$rc" >> $OUT/status.log
