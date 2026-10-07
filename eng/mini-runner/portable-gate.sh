#!/bin/bash
set -euo pipefail
umask 077
root=/runner/gate
mkdir "$root"
mkdir -p "$root/out" "$root/tmp" "$root/cache"/{nuget,nuget-http,dotnet,npm,pnpm,xdg,xdg-data,xdg-config,cargo}
export TMPDIR="$root/tmp/" NUGET_PACKAGES="$root/cache/nuget" NUGET_HTTP_CACHE_PATH="$root/cache/nuget-http" DOTNET_CLI_HOME="$root/cache/dotnet"
export npm_config_cache="$root/cache/npm" pnpm_config_store_dir="$root/cache/pnpm" XDG_CACHE_HOME="$root/cache/xdg" CARGO_HOME="$root/cache/cargo"
export HARBORLINE_PLATFORM_REPO="$root/platform" HARBORLINE_CONTROL_REPO="$root/control" HARBORLINE_QUALITY_REPO="$root/quality" HARBORLINE_VERIFY_LANE=all
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_NOLOGO=1
pwsh -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.ToString()' > "$root/out/powershell-preflight.log" 2>&1
export HARBORLINE_INSTALL_ARTEFACT_PORT=15359 HARBORLINE_REMOVAL_EXERCISE_PORT=15585
python3 -I /opt/mini/checkout-sources.py
cd "$root/api"
# Existing preflight enforces global.json and all private pins. Do not override
# SDK selection or replace the gate with its exit-code-only local pilot.
node eng/build-local-feed.mjs > "$root/out/feed.log" 2>&1
# A main-only run can skip focused builds. Restore the solution explicitly so
# the dependency ledger can inspect package licences in this fresh private cache.
dotnet restore Harborline.Api.slnx > "$root/out/solution-restore.log" 2>&1
# The dependency ledger checks centrally pinned packages even when no project
# currently consumes them. Restore that metadata in the private cache, using the
# source tree's target framework and exact central pin (no source edits).
mkdir -p "$root/tmp/ledger-prerequisites"
python3 -I - <<'METADATA'
import xml.etree.ElementTree as ET
from pathlib import Path
root=Path('/runner/gate')
api=root/'api'
framework=ET.parse(api/'Directory.Build.props').findtext('.//TargetFramework')
pins=ET.parse(api/'Directory.Packages.props').iter('PackageVersion')
version=next(p.attrib['Version'] for p in pins if p.attrib.get('Include')=='Microsoft.Extensions.Http')
project=ET.Element('Project',Sdk='Microsoft.NET.Sdk')
ET.SubElement(ET.SubElement(project,'PropertyGroup'),'TargetFramework').text=framework
ET.SubElement(ET.SubElement(project,'ItemGroup'),'PackageReference',Include='Microsoft.Extensions.Http',Version=version)
ET.ElementTree(project).write(root/'tmp/ledger-prerequisites/restore.csproj')
METADATA
dotnet restore "$root/tmp/ledger-prerequisites/restore.csproj" > "$root/out/ledger-prerequisites.log" 2>&1
set +e
bash eng/verify.sh > "$root/out/gate.log" 2>&1
code=$?
printf '%s\n' "$code" > "$root/out/gate-exit.txt"
for file in .git/harborline-api-verify-receipt.json .git/harborline-api-quality-decision.json; do
  if [ -f "$file" ]; then cp "$file" "$root/out/"; fi
done
if [ -d artifacts/quality ]; then cp -R artifacts/quality "$root/out/quality"; fi
if [ -d .claude/gate-evidence ]; then cp -R .claude/gate-evidence "$root/out/gate-evidence"; fi
tail -n 60 "$root/out/gate.log"
exit "$code"
