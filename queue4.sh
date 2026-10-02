#!/usr/bin/env bash
# T-519: confirming scoped Stryker.NET re-runs after the mutation-gap tests, one job at a time on a quiet host.
set -uo pipefail
set -f  # scope globs go to Stryker unexpanded
OUT="C:/Users/Chris/AppData/Local/Temp/claude/C--Projects-Harborline-harborline-control/b4aa51b5-3cfc-4c5d-a585-184fcd82bc49/scratchpad/mutation"
W=/c/Projects/Harborline/harborline-api/.codex/worktrees
HOST=apps/local-node-host/tests/tests.csproj

# name | worktree | pinned commit | report dir under .stryker | mutation-report arguments after --only
JOBS=(
"j335|ck10s5c|10ef7cd9|tests-scoped-PackInstaller.cs|--project Harborline.Foundation.Packs.csproj --scoped **/Install/PackInstaller.cs --filter FullyQualifiedName~.Tests.Packs.|FullyQualifiedName~PackInstall|FullyQualifiedName~AuthorizationWriteStageTests|FullyQualifiedName~.Tests.Configuration.|FullyQualifiedName~.Tests.Forms.|FullyQualifiedName~.Tests.Catalogue."
"j331-hierarchy|ck10s3|48cf9872|tests-scoped-NodeHierarchyCompositeCoordinator.cs|--scoped **/Data/Entities/NodeHierarchyCompositeCoordinator.cs --filter FullyQualifiedName~Hierarchy|FullyQualifiedName~AuthorizationWriteStageTests|FullyQualifiedName~CompiledSchemaEntityValidationTests|FullyQualifiedName~RecordWriteRulesStageTests"
"j331-forms|ck10s3|48cf9872|tests-scoped-IAuthorizedFormEntityWriter.cs|--project Harborline.Foundation.Forms.Engine.csproj --scoped **/IAuthorizedFormEntityWriter.cs --filter FullyQualifiedName~.Tests.Forms.|FullyQualifiedName~FormSubmission|FullyQualifiedName~FormEngine|FullyQualifiedName~AuthorizationWriteStageTests"
)

quiet() {
  tasklist //FI "IMAGENAME eq Runner.Worker.exe" 2>/dev/null | grep -qi "Runner.Worker" && return 1
  [ -d /c/Projects/Harborline/harborline-api/.git/harborline-gate.lock ] && return 1
  local cpu; cpu=$(powershell -NoProfile -Command "[int](Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average" 2>/dev/null | tr -d '\r')
  [ -n "$cpu" ] && [ "$cpu" -lt 40 ]
}

for job in "${JOBS[@]}"; do
  IFS='|' read -r name wt pin reportdir rest <<<"$job"
  args_part=${job#*|*|*|*|}
  scope=${args_part%% --filter *}; filter=${args_part#* --filter }
  dir="$OUT/$name"; mkdir -p "$dir"
  cd "$W/$wt" || { echo "$name: no worktree" | tee "$dir/status"; continue; }
  head=$(git rev-parse --short=8 HEAD)
  if [ "$head" != "$pin" ]; then echo "$name: HEAD $head is not the pinned $pin; skipped" | tee "$dir/status"; continue; fi
  waited=0; until quiet; do sleep 60; waited=$((waited+1)); [ $waited -ge 120 ] && break; done
  if ! quiet; then echo "$name: host not quiet after 2h; skipped" | tee "$dir/status"; continue; fi
  { echo "job=$name worktree=$wt commit=$pin"; echo "scope=$scope"; echo "filter=$filter"; echo "stryker=$(dotnet tool list 2>/dev/null | grep -i stryker | tr -s ' ')"; echo "started=$(date -u +%FT%TZ)"; } > "$dir/pin.txt"
  source eng/gate-lock.sh
  if ! gate_lock_acquire "T-519-mutation-$name"; then echo "$name: gate lock not acquired" | tee "$dir/status"; continue; fi
  # shellcheck disable=SC2086
  timeout 14400 node eng/mutation-report.mjs --only "$HOST" $scope --filter "$filter" > "$dir/run.log" 2>&1
  rc=$?
  gate_lock_release
  mkdir -p "$dir/stryker"
  cp ".stryker/$reportdir/reports/mutation-report.json" "$dir/stryker/" 2>/dev/null || echo "no report at .stryker/$reportdir" >> "$dir/pin.txt"
  echo "finished=$(date -u +%FT%TZ) exit=$rc" >> "$dir/pin.txt"
  echo "$name: exit $rc" | tee "$dir/status"
done
echo QUEUE-DONE
