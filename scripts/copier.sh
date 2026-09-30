#!/usr/bin/env bash
# Copy each finished scoped report out of .stryker before a later run with the same name removes it.
WT=/c/Projects/Harborline/harborline-api/.codex/worktrees/r4-t984api
OUT=/c/hl-r4t984/reports
declare -A N=([ck11-reader]=NodeAuthorizationRosterConstraintReader.cs [ck11-resolver]=SelectedSessionPermissionResolver.cs [ck11-gate]=AuthorizationGate.cs [ck11-rekey]=RetiredDesktopActorRekey.cs [ck11-operator]=NodeOperatorIdentity.cs [ck11-preinsert]=RosterCrdtProjection.cs [t975-asset]=AssetRegistryRoutes.cs [t519-gate]=AuthorizationGate.cs)
end=$((SECONDS+6*3600))
while [ $SECONDS -lt $end ]; do
  for id in "${!N[@]}" t974-guards; do
    [ -f $OUT/$id.mutation-report.json ] && continue
    grep -q "END $id " $OUT/status.log 2>/dev/null || continue
    if [ $id = t974-guards ]; then d=$(ls -d $WT/.stryker/tests-scoped-AccountingPeriodRoutes* | head -1); c=$(ls $WT/apps/local-node-host/tests/.stryker/tests-scoped-AccountingPeriodRoutes*.json | head -1)
    else d=$WT/.stryker/tests-scoped-${N[$id]}; c=$WT/apps/local-node-host/tests/.stryker/tests-scoped-${N[$id]}.json; fi
    cp "$d/reports/mutation-report.json" $OUT/$id.mutation-report.json && cp "$c" $OUT/$id.config.json && echo "$(date -Is) COPIED $id" >> $OUT/status.log
  done
  grep -q "DONE all" $OUT/status.log && [ -f $OUT/t519-gate.mutation-report.json ] && exit 0
  sleep 30
done
