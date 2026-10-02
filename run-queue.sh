#!/usr/bin/env bash
# T-519 review follow-up: scoped local Stryker.NET evidence, one job at a time on a verified-quiet host.
# Each job is pinned to a worktree commit, a scope and a test filter. Reports and logs are kept, complete or not.
set -uo pipefail
set -f  # scope globs go to Stryker unexpanded
OUT="C:/Users/Chris/AppData/Local/Temp/claude/C--Projects-Harborline-harborline-control/b4aa51b5-3cfc-4c5d-a585-184fcd82bc49/scratchpad/mutation"
W=/c/Projects/Harborline/harborline-api/.codex/worktrees
HOST=apps/local-node-host/tests/tests.csproj

# name | worktree | pinned commit | mutation-report arguments after --only
JOBS=(
"j330-host|ck10s4|9a184866|--scoped **/Data/Financial/NodeBankAccountWriter.cs --scoped **/Data/Identity/AuthorizedGrantRevocationWriter.cs --filter FullyQualifiedName~BankAccount|FullyQualifiedName~GrantRevocation|FullyQualifiedName~AdminTeamAccess|FullyQualifiedName~AuthorizationWriteStageTests|FullyQualifiedName~Mtw2TwoUserAcceptanceE2E"
"j330-executor|ck10s4|9a184866|--project Harborline.Kernel.Runtime.csproj --scoped **/WritePipelineStage.cs --filter FullyQualifiedName~WritePipeline|FullyQualifiedName~RecordWritePipelineTests|FullyQualifiedName~BankAccountWritePipelineTests|FullyQualifiedName~GrantRevocationWritePipelineTests|FullyQualifiedName~AuthorizationWriteStageTests"
"j333|ck10s5a|654af6de|--scoped **/Data/Configuration/ConfigurationActivationTarget.cs --filter FullyQualifiedName~.Tests.Configuration."
"j334|ck10s5b|b01f5470|--project Harborline.Foundation.Packs.csproj --scoped **/Install/PackInstaller.cs --filter FullyQualifiedName~.Tests.Packs.|FullyQualifiedName~PackInstall|FullyQualifiedName~AuthorizationWriteStageTests|FullyQualifiedName~.Tests.Configuration."
"j329|t380|23cdec5b|--scoped **/Health/PackRouteAuthorization.cs --filter FullyQualifiedName~PackRoutePermissionTests|FullyQualifiedName~WebPlaneAuthorizationFenceTests|FullyQualifiedName~.Tests.Feed.|FullyQualifiedName~.Tests.Configuration.|FullyQualifiedName~PackNavigation"
)

quiet() {
  tasklist //FI "IMAGENAME eq Runner.Worker.exe" 2>/dev/null | grep -qi "Runner.Worker" && return 1
  [ -d /c/Projects/Harborline/harborline-api/.git/harborline-gate.lock ] && return 1
  local cpu; cpu=$(powershell -NoProfile -Command "[int](Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average" 2>/dev/null | tr -d '\r')
  [ -n "$cpu" ] && [ "$cpu" -lt 40 ]
}

for job in "${JOBS[@]}"; do
  IFS='|' read -r name wt pin rest <<<"$job"
  # Rebuild the args: everything after the third field, with the filter kept as one argument.
  args_part=${job#*|*|*|}
  scope=${args_part%% --filter *}; filter=${args_part#* --filter }
  dir="$OUT/$name"; mkdir -p "$dir"
  cd "$W/$wt" || { echo "$name: no worktree" | tee "$dir/status"; continue; }
  head=$(git rev-parse --short=8 HEAD)
  if [ "$head" != "$pin" ]; then echo "$name: HEAD $head is not the pinned $pin; skipped" | tee "$dir/status"; continue; fi
  waited=0; until quiet; do sleep 60; waited=$((waited+1)); [ $waited -ge 120 ] && break; done
  if ! quiet; then echo "$name: host not quiet after 2h; skipped" | tee "$dir/status"; continue; fi
  { echo "job=$name worktree=$wt commit=$pin"; echo "scope=$scope"; echo "filter=$filter"; echo "stryker=$(dotnet tool list 2>/dev/null | grep -i stryker | tr -s ' ')"; echo "started=$(date -u +%FT%TZ)"; } > "$dir/pin.txt"
  sleep 2
  source eng/gate-lock.sh
  if ! gate_lock_acquire "T-519-mutation-$name"; then echo "$name: gate lock not acquired" | tee "$dir/status"; continue; fi
  # shellcheck disable=SC2086
  timeout 14400 node eng/mutation-report.mjs --only "$HOST" $scope --filter "$filter" > "$dir/run.log" 2>&1
  rc=$?
  gate_lock_release
  echo "finished=$(date -u +%FT%TZ) exit=$rc" >> "$dir/pin.txt"
  mkdir -p "$dir/stryker"
  [ -d .stryker ] && find .stryker -newer "$dir/pin.txt" -name 'mutation-report.json' -print0 2>/dev/null | while IFS= read -r -d '' f; do mkdir -p "$dir/stryker/$(dirname "$f")"; cp "$f" "$dir/stryker/$f"; done
  echo "$name: exit $rc" | tee "$dir/status"
done
echo QUEUE-DONE
