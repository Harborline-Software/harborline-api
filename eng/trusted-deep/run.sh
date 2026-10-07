#!/bin/bash
set -euo pipefail
umask 077
python3 -I /opt/trusted/job.py admit "$1"
kind="$1"
export HARBORLINE_GATE_COVERAGE=0
if [ "$kind" = portable-coverage ]; then export HARBORLINE_GATE_COVERAGE=1; fi
if [ "$kind" = portable ] || [ "$kind" = portable-coverage ]; then
  bash /opt/mini/portable-gate.sh
else
  root=/runner/gate
  mkdir -p "$root/out" "$root/tmp" "$root/cache"/{nuget,nuget-http,dotnet,npm,pnpm,xdg,xdg-data,xdg-config,cargo}
  export TMPDIR="$root/tmp/" NUGET_PACKAGES="$root/cache/nuget" NUGET_HTTP_CACHE_PATH="$root/cache/nuget-http" DOTNET_CLI_HOME="$root/cache/dotnet"
  export npm_config_cache="$root/cache/npm" pnpm_config_store_dir="$root/cache/pnpm" XDG_CACHE_HOME="$root/cache/xdg" CARGO_HOME="$root/cache/cargo"
  export HARBORLINE_PLATFORM_REPO="$root/platform" HARBORLINE_CONTROL_REPO="$root/control" HARBORLINE_QUALITY_REPO="$root/quality"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_NOLOGO=1
  pwsh -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.ToString()' > "$root/out/powershell-preflight.log" 2>&1
  python3 -I /opt/mini/checkout-sources.py > "$root/out/restore-source.log" 2>&1
  cd "$root/api"
  node eng/build-local-feed.mjs > "$root/out/feed.log" 2>&1
  dotnet tool restore > "$root/out/tool-restore.log" 2>&1
  set +e
  node eng/mutation-report.mjs --full --only tests/Harborline.Api.Tests/Harborline.Api.Tests.csproj > "$root/out/mutation.log" 2>&1
  code=$?
  set -e
  printf '%s\n' "$code" > "$root/out/mutation-exit.txt"
  mkdir "$root/out/mutation-reports"
  find .stryker -name mutation-report.json -exec cp '{}' "$root/out/mutation-reports/report.json" \;
  [ "$code" = 0 ] || exit "$code"
fi
python3 -I /opt/trusted/job.py finish "$kind"
