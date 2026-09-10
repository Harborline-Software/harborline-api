#!/usr/bin/env bash
# Publish the local-node host as a startable install artefact (ticket 359).
#
# One self-contained directory: the .NET runtime, the ASP.NET Core shared framework, the host, and
# the operator CLI (the host csproj's PublishOperatorCliWithHost target puts harborline-node beside
# it). A machine that has this directory needs no source checkout and no .NET SDK — which is the
# whole point: the only documented way to run the reference app today is `dotnet run` from a
# checkout on an exactly pinned preview SDK.
#
# NOT single-file and NOT trimmed, deliberately. EF Core, the DI container, the pack verifier and
# the analyzer-facing reflection in this host all resolve types by name at runtime; a trimmed or
# bundled publish is where that stops working, and the artefact is a dev deliverable (ADR 0066: no
# production installs), not a distribution package. Directory publish keeps every assembly on disk
# where the runtime — and a person debugging it — can see it.
#
# bash 3.2 / BSD safe: no timeout, no sed -i, no mapfile, no associative arrays.
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)

rid=""
out=""
while [ $# -gt 0 ]; do
  case "$1" in
    --rid) rid=${2:-}; shift 2 ;;
    --out) out=${2:-}; shift 2 ;;
    -h|--help)
      echo "usage: eng/publish-local-node.sh [--rid <runtime-identifier>] [--out <directory>]"
      exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

# The host's RID, from the host — an operator publishing for the machine in front of them should not
# have to know the .NET spelling of it. --rid overrides for a cross-publish.
if [ -z "$rid" ]; then
  case "$(uname -s)" in
    Darwin) case "$(uname -m)" in arm64) rid=osx-arm64 ;; *) rid=osx-x64 ;; esac ;;
    Linux)  case "$(uname -m)" in aarch64|arm64) rid=linux-arm64 ;; *) rid=linux-x64 ;; esac ;;
    *)      rid=win-x64 ;;
  esac
fi

[ -n "$out" ] || out="$repo_root/artifacts/publish/local-node-$rid"
case "$out" in
  /*|[A-Za-z]:*) ;;
  *) out="$repo_root/$out" ;;
esac

echo "publishing local-node host: rid=$rid out=$out"
rm -rf "$out"

# A RID-targeted restore ADDS a per-RID target to every packages.lock.json in the graph
# (packages/foundation-password-hashing pins Konscious behind one, ADR 0097's supply-chain floor).
# Those entries are machine-dependent noise, not a pin decision, and a publish must not leave the
# checkout dirty. Snapshot the lock files, publish, put them back byte-for-byte. RestoreLockedMode is
# forced off for the same reason: that project turns it ON under ContinuousIntegrationBuild, where a
# lock file with no entry for this RID would refuse the restore outright.
lock_backup=$(mktemp -d 2>/dev/null || mktemp -d -t harborline-publish-locks)
lock_index=0
for lock in $(find "$repo_root" -name packages.lock.json -not -path "*/artifacts/*" -not -path "*/obj/*" -not -path "*/bin/*"); do
  lock_index=$((lock_index + 1))
  cp "$lock" "$lock_backup/$lock_index"
  echo "$lock" > "$lock_backup/$lock_index.path"
done
restore_locks() {
  index=0
  while [ "$index" -lt "$lock_index" ]; do
    index=$((index + 1))
    cp "$lock_backup/$index" "$(cat "$lock_backup/$index.path")"
  done
  rm -rf "$lock_backup"
}
trap restore_locks EXIT

# The host's PublishOperatorCliWithHost target publishes the operator CLI into the same directory by
# invoking MSBuild's Publish target directly, which does NOT restore. On a tree where nothing has
# restored that project yet (a fresh clone, or this script run on its own) the host publishes and
# then dies on NETSDK1004 for the CLI's missing assets file. Restore it for the same RID first.
dotnet restore "$repo_root/apps/node-operator-cli/Harborline.NodeOperatorCli.csproj" \
  -r "$rid" -p:RestoreLockedMode=false -nodeReuse:false -maxcpucount:6

dotnet publish "$repo_root/apps/local-node-host/Harborline.LocalNodeHost.csproj" \
  -c Release \
  --self-contained \
  -r "$rid" \
  -o "$out" \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false \
  -p:RestoreLockedMode=false \
  -nodeReuse:false \
  -maxcpucount:6

host_exe="$out/Harborline.Api.LocalNodeHost"
case "$rid" in win-*) host_exe="$host_exe.exe" ;; esac
[ -x "$host_exe" ] || { echo "publish produced no host executable at $host_exe" >&2; exit 1; }

cli_exe="$out/harborline-node"
case "$rid" in win-*) cli_exe="$cli_exe.exe" ;; esac
[ -x "$cli_exe" ] || { echo "publish produced no operator CLI at $cli_exe" >&2; exit 1; }

echo
echo "published $(find "$out" -type f | wc -l | tr -d ' ') files to $out"
echo
echo "start it with (see docs/install/first-start.md for the founder inputs):"
echo
echo "  LocalNode__RootSeedHex=\$(openssl rand -hex 32) LocalNode__SessionToken=\$(openssl rand -hex 16) LocalNode__HealthPort=5050 \"$host_exe\""
echo
