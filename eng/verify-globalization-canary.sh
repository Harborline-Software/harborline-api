#!/usr/bin/env bash
# Ticket 323 positive control: prove the root globalization promotion reaches
# a production package that none of the four migration lanes touched.
set -uo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
# pwd -W gives a Windows path under Git Bash (dotnet cannot open /c/...); macOS and Linux fall back to pwd.
msbuild_root=$(cd "$(dirname "$0")/.." && (pwd -W 2>/dev/null || pwd))
if command -v cygpath >/dev/null 2>&1; then msbuild_root=$(cygpath -m "$msbuild_root"); fi
project="$msbuild_root/packages/federation-common/Harborline.Federation.Common.csproj"
source_file="$root/packages/federation-common/EnvelopeSigning.cs"
# A dirty plant site is refused, never backed up and clobbered: the restore would hide someone's edit.
if ! git -C "$root" diff --quiet -- packages/federation-common/EnvelopeSigning.cs; then
  echo "globalization canary REFUSED — $source_file has uncommitted changes; commit or revert them first" >&2; exit 2
fi
source_name="EnvelopeSigning.cs"
expected="CA1304"
backup=$(mktemp "${TMPDIR:-/tmp}/harborline-globalization-canary.XXXXXX")

cleanup() {
  cp "$backup" "$source_file"
  rm -f "$backup"
}

cp "$source_file" "$backup"
trap cleanup EXIT

node - "$source_file" <<'NODE'
const { readFileSync, writeFileSync } = require("node:fs");
const file = process.argv[2];
const marker = "    private static string GlobalizationCanary(string value) => value.ToLower();\n\n";
const source = readFileSync(file, "utf8");
const needle = "    public static byte[] ComputeSignableBytes(";
if (source.includes(marker) || !source.includes(needle)) {
  throw new Error(`cannot plant globalization canary in ${file}`);
}
writeFileSync(file, source.replace(needle, marker + needle), "utf8");
NODE

set +e
output=$(dotnet build "$project" -c Release --no-restore --no-dependencies -nodeReuse:false -maxcpucount:6 \
  -p:UseSharedCompilation=false -p:NoWarn=CA1311 2>&1)
build_status=$?
set -e

error_count=$( (grep -E ': error [A-Z]+[0-9]+:' <<<"$output" || true) | sort -u | wc -l | tr -d ' ')
family_count=$( (grep -E "error $expected:" <<<"$output" || true) | sort -u | wc -l | tr -d ' ')
file_count=$( (grep -E "$source_name.*error $expected:|error $expected:.*$source_name" <<<"$output" || true) | sort -u | wc -l | tr -d ' ')

if [ "$build_status" -ne 0 ] && [ "$error_count" -eq 1 ] && [ "$family_count" -eq 1 ] && [ "$file_count" -eq 1 ]; then
  grep "error $expected:" <<<"$output"
  echo "globalization canary OK — exactly 1 $expected error in $source_name"
  exit 0
fi

echo "globalization canary FAIL — expected exactly one $expected error in $source_name; found errors=$error_count family=$family_count file=$file_count status=$build_status" >&2
grep -E ': error |Build succeeded|Build FAILED' <<<"$output" >&2 || true
exit 1
