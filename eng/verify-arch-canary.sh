#!/usr/bin/env bash
# Positive control for ticket 340: the real tier fence must surface a planted
# ProjectReference as normalized arch SARIF and must be green after restoration.
set -euo pipefail
# pwd -W gives a Windows path under Git Bash (node cannot open /c/...); macOS falls back to pwd.
root=$(cd "$(dirname "$0")/.." && (pwd -W 2>/dev/null || pwd))
fixture="$root/packages/foundation-arch-canary"
scratch="$root/artifacts/quality/arch-canary.$$"
trx="$scratch/arch-canary.trx"
sarif="$scratch/arch-canary.sarif"
cleanup() { rm -rf "$fixture" "$scratch"; }
trap cleanup EXIT
mkdir -p "$fixture" "$scratch"
printf '<Project Sdk="Microsoft.NET.Sdk">\n  <ItemGroup>\n    <ProjectReference Include="../blocks-access-grant/Harborline.Blocks.AccessGrant.csproj" />\n  </ItemGroup>\n</Project>\n' > "$fixture/Harborline.Foundation.ArchCanary.csproj"
set +e
dotnet test "$root/apps/local-node-host/tests/tests.csproj" -c Release --nologo --no-build -nodeReuse:false -maxcpucount:6 \
  --filter 'FullyQualifiedName~ApiTierDependencyArchTests' --logger 'trx;LogFileName=arch-canary.trx' --results-directory "$scratch"
red_status=$?
set -e
if [[ $red_status -eq 0 ]] || [[ ! -f "$trx" ]]; then
  echo 'arch canary FAIL — planted dependency edge did not fail the tier fence' >&2; exit 1
fi
node "$root/eng/arch-sarif.mjs" --repo-root "$root" "$trx" "$sarif"
node -e '
const s=JSON.parse(require("node:fs").readFileSync(process.argv[1], "utf8")); const r=s.runs?.[0]?.results ?? [];
const x=r[0]; process.exit(s.version === "2.1.0" && s.runs?.[0]?.tool?.driver?.name === "arch"
  && r.length === 1 && x?.ruleId === "HLQ.ARCH.1000"
  && x.locations?.[0]?.physicalLocation?.artifactLocation?.uri === "packages/foundation-arch-canary/Harborline.Foundation.ArchCanary.csproj"
  && ["harborline/primary-location/v1", "harborline/primary-location/v2", "harborline/project/v1"].every(k => /^sha256:[a-f0-9]{64}$/.test(x.partialFingerprints?.[k] ?? "")) ? 0 : 1)
' "$sarif" || { echo 'arch canary FAIL — planted edge is absent from normalized SARIF' >&2; exit 1; }
cleanup
trap - EXIT
dotnet test "$root/apps/local-node-host/tests/tests.csproj" -c Release --nologo --no-build -nodeReuse:false -maxcpucount:6 \
  --filter 'FullyQualifiedName~ApiTierDependencyArchTests'
echo 'arch canary GREEN — planted dependency edge detected and restored'
