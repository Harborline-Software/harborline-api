#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
bash "$repo_root/eng/verify-boundaries.sh"
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
# The nuget.org URL is listed FIRST deliberately. With the local feed first, NuGet
# normalises the URL that follows as though it were a path -- the "//" collapses and
# it is then resolved relative to the project directory, so restore dies with
#   NU1301: The local source '...\tests\package-consumer\https:\api.nuget.org\...'
# Observed on SDK 11.0.100-preview.7 under both bash and PowerShell, so it is a NuGet
# argument-handling quirk rather than MSYS path conversion. It bites on every run:
# each run mints a brand-new version, so NuGet can never satisfy the restore from
# cache and always walks the remote source. Listing the URL first avoids it, and is
# inert wherever the quirk does not reproduce.
dotnet restore "$repo_root/tests/package-consumer/Consumer.csproj" \
  -p:HarborlinePackageVersion="$version" \
  --source https://api.nuget.org/v3/index.json \
  --source "$artifact_dir" \
  --force-evaluate
dotnet run --project "$repo_root/tests/package-consumer/Consumer.csproj" \
  -c Release --no-restore -p:HarborlinePackageVersion="$version"
