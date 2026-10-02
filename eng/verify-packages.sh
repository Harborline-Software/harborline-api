#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
# On tag publication, skip the repository boundary check already proved by the merge group.
# Every new set of packed bytes still requires the isolated consumer proof.
pack_only=${HARBORLINE_PACKAGE_PACK_ONLY:-}
[ "$pack_only" = 1 ] || bash "$repo_root/eng/verify-boundaries.sh"
version=${HARBORLINE_PACKAGE_VERSION:-"0.1.0-preview.local.$(date -u +%Y%m%d%H%M%S)"}
if [ -n "${HARBORLINE_PACKAGE_OUTPUT:-}" ]; then
  artifact_dir=$HARBORLINE_PACKAGE_OUTPUT
else
  artifact_dir=$(mktemp -d "${TMPDIR:-/tmp}/harborline-api-packages.XXXXXX")
  trap 'rm -rf "$artifact_dir"' EXIT
fi

mkdir -p "$artifact_dir"
package_projects=(
  "$repo_root/src/Harborline.Api.Contracts/Harborline.Api.Contracts.csproj"
  "$repo_root/src/Harborline.Api.Client/Harborline.Api.Client.csproj"
  "$repo_root/src/Harborline.Api.Testing/Harborline.Api.Testing.csproj"
)
for project in "${package_projects[@]}"; do
  # ADR 0164 D6 puts MinVer in charge of the version, and MinVer overrides a
  # command-line -p:Version. Packs asked for "$version" but emitted the
  # git-derived one instead, so the consumer restore below then hunted the feed
  # for a version nothing had produced. MinVerVersionOverride is MinVer's own
  # supported pin, and it is what makes the uploaded artifact actually carry the
  # immutable version the workflow selected.
  dotnet pack "$project" -c Release -p:MinVerVersionOverride="$version" -o "$artifact_dir"
done
python3 "$repo_root/eng/package-proof.py" consume "$artifact_dir" "$version"
