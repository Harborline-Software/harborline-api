#!/usr/bin/env bash
# T-585 / T-670: ADR-0144 keeps the bundle and recorded referent inside the test host.
# The test prints each BEFORE, deactivation, AFTER and installer probe as evidence.
set -euo pipefail
repo_root=$(cd "$(dirname "$0")/.." && pwd)
pack_version=$(node "$repo_root/eng/platform-seed-version.mjs")
echo "removal-exercise: seed manifest revision $pack_version"
dotnet test "$repo_root/apps/local-node-host/tests/tests.csproj" -c Release --no-restore \
  --filter 'FullyQualifiedName~Platform_bundle_RemovalExercise' \
  --logger 'console;verbosity=detailed'
