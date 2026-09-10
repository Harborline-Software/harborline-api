#!/usr/bin/env bash
# Ticket 359: prove the published install artefact starts on a CLEAN DIRECTORY with no source tree.
#
# Why this is a gate step and not an xunit test: the proof's first act is `dotnet publish
# --self-contained`, which on a clean clone builds the whole host graph and writes ~640 files. That
# is minutes of wall time and is not what a `dotnet test` run is for; the host suite also runs under
# a baseline comparison, so a publish failure inside it would read as a moved baseline rather than a
# broken install path. As a named step it fails as itself.
#
# The clean directory is the load-bearing part. The artefact is copied OUT of the repository into a
# temp directory with no checkout, no csproj and no SDK-relative path beside it, and started there
# with the two boot inputs and the founder inputs as environment variables -- exactly what
# docs/install/first-start.md tells an operator to type. Anything the host silently took from the
# source tree shows up here as a failure to answer /health.
#
# bash 3.2 / BSD safe: no timeout, no sed -i, no mapfile, no associative arrays.
set -uo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
port=${HARBORLINE_INSTALL_ARTEFACT_PORT:-5359}
# A fixed seed so the genesis team id is a CONSTANT this script can assert. All 32 bytes are 0x33,
# so the first-16-bytes-as-a-GUID derivation (GenesisTeamId.Derive) is 3333...-style regardless of
# the machine's byte order -- the assertion cannot pass for the wrong reason.
root_seed_hex=3333333333333333333333333333333333333333333333333333333333333333
genesis_team=33333333-3333-3333-3333-333333333333
session_token=install-artefact-proof-token
founder_password=install-artefact-proof-password

host_pid=""
clean_root=""
cleanup() {
  if [ -n "$host_pid" ] && kill -0 "$host_pid" 2>/dev/null; then
    kill "$host_pid" 2>/dev/null
    sleep 2
    kill -9 "$host_pid" 2>/dev/null
  fi
  [ -n "$clean_root" ] && rm -rf "$clean_root"
}
trap cleanup EXIT

fail() { echo "install-artefact: $*" >&2; exit 1; }

case "$(uname -s)" in
  Darwin|Linux) exe="" ;;
  *)            exe=".exe" ;;
esac

# A listener already on the port would make every probe below pass against somebody else's node.
if curl -fsS "http://127.0.0.1:$port/health" -o /dev/null 2>/dev/null; then
  fail "something is already listening on 127.0.0.1:$port; set HARBORLINE_INSTALL_ARTEFACT_PORT"
fi

artefact="$repo_root/artifacts/publish/local-node-proof"
bash "$repo_root/eng/publish-local-node.sh" --out "$artefact" || fail "publish failed"

clean_root=$(mktemp -d 2>/dev/null || mktemp -d -t harborline-install-artefact)
clean="$clean_root/node"
data="$clean_root/data"
mkdir -p "$clean" "$data"
cp -R "$artefact/." "$clean/"
# The whole claim is "no source checkout": say so out loud rather than trusting the temp path.
[ -e "$clean/Harborline.LocalNodeHost.csproj" ] && fail "the clean directory carries a csproj"
[ -e "$clean_root/../Harborline.Api.slnx" ] && fail "the clean directory sits inside the checkout"
echo "install-artefact: clean directory $clean ($(find "$clean" -type f | wc -l | tr -d ' ') files, no checkout)"

founder_hash=$("$clean/Harborline.Api.LocalNodeHost$exe" hash-web-password "$founder_password") ||
  fail "hash-web-password failed"
case "$founder_hash" in
  '$argon2id$'*) ;;
  *) fail "hash-web-password did not mint an Argon2id hash: $founder_hash" ;;
esac

# Started from the clean directory, by the documented command.
(
  cd "$clean" || exit 1
  DOTNET_ENVIRONMENT=Production \
  ASPNETCORE_ENVIRONMENT=Production \
  LocalNode__HealthPort="$port" \
  LocalNode__DataDirectory="$data" \
  LocalNode__RootSeedHex="$root_seed_hex" \
  LocalNode__SessionToken="$session_token" \
  LocalNode__WebClient__Enabled=true \
  LocalNode__WebClient__FounderUsername=founder \
  LocalNode__WebClient__FounderPasswordHash="$founder_hash" \
  Logging__EventLog__LogLevel__Default=None \
  exec "./Harborline.Api.LocalNodeHost$exe"
) > "$clean_root/host.log" 2>&1 &
host_pid=$!

# No `timeout` (absent on the mac gate hosts): poll, and give up after a bounded number of tries.
healthy=""
for _ in $(seq 1 120); do
  if curl -fsS "http://127.0.0.1:$port/health" -o "$clean_root/health.txt" 2>/dev/null; then
    healthy=yes
    break
  fi
  kill -0 "$host_pid" 2>/dev/null || break
  sleep 2
done
if [ -z "$healthy" ]; then
  echo "----- host log -----" >&2
  cat "$clean_root/host.log" >&2
  fail "the published artefact never answered /health on 127.0.0.1:$port"
fi
grep -q '^Healthy' "$clean_root/health.txt" ||
  fail "/health answered '$(head -1 "$clean_root/health.txt")', expected Healthy"
grep -q "$genesis_team" "$clean_root/health.txt" ||
  fail "/health does not report the genesis team $genesis_team"
echo "install-artefact: /health -> $(head -1 "$clean_root/health.txt") (genesis team $genesis_team materialized)"

# The operator CLI ships inside the artefact (ADR 0020) and is pointed at the node by URL + token.
cli_out=$("$clean/harborline-node$exe" \
  --url "http://127.0.0.1:$port" --token "$session_token" --json tenant list) ||
  fail "the operator CLI could not reach the node: $cli_out"
case "$cli_out" in
  *"\"teamId\":\"$genesis_team\""*) ;;
  *) fail "the operator CLI did not list the genesis team; it printed: $cli_out" ;;
esac
echo "install-artefact: operator CLI tenant list -> $cli_out"

# Stop it, and prove it stopped: a proof that leaks a node is not a proof of a stoppable install.
# The polite signal first, then the impolite one. On Git Bash a plain SIGTERM is an MSYS-level
# signal that a native Windows console process simply does not receive, so a stop that insisted on
# the graceful path alone would report a failure the product does not have -- and leave a node
# listening. The escalation is the honest portable stop; the ASSERTION is that the port is released.
kill "$host_pid" 2>/dev/null
for _ in $(seq 1 15); do
  kill -0 "$host_pid" 2>/dev/null || break
  sleep 1
done
kill -9 "$host_pid" 2>/dev/null
for _ in $(seq 1 15); do
  curl -fsS "http://127.0.0.1:$port/health" -o /dev/null 2>/dev/null || break
  sleep 1
done
curl -fsS "http://127.0.0.1:$port/health" -o /dev/null 2>/dev/null &&
  fail "the published artefact was still serving 127.0.0.1:$port after being stopped"
host_pid=""

echo "install-artefact: pass 1 (clean-directory publish, boot, /health, operator CLI, stop)"
