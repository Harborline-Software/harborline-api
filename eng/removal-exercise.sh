#!/usr/bin/env bash
# T-585 item 2 — the installed package is the ONLY source of the surface.
#
# DES-0007 `platform-package-eng-6`: "Removing the package leaves installer, not broken Workshop;
# compiled inspectors, unavailable shapes, grants and unmanifested routes remain absent."
#
# The ticket is explicit that removal "is proved by running, not by reading a manifest", and that
# this exercise's OUTPUT is the evidence. So this is not a test that asserts a manifest says no
# routes remain: it starts the published node, records what the Workshop surface answers, removes
# the installed package through the ordinary installer route, and records what the same requests
# answer afterwards. Every probe prints its own line and every finding names the definitions it
# found, not a count. A reader can see the surface go.
#
# Why a gate step and not an xunit case, the same reasoning as eng/verify-install-artefact.sh next
# door: the first act is a self-contained publish, minutes of wall time and hundreds of files, and
# the host suite's verdict is a baseline comparison -- a removal that stopped removing would read as
# a moved baseline rather than as a broken absence contract. As a named step it fails as itself.
#
# bash 3.2 / BSD safe: no timeout, no sed -i, no mapfile, no associative arrays.
set -uo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
port=${HARBORLINE_REMOVAL_EXERCISE_PORT:-5585}
# A fixed seed so the genesis team is a constant; see eng/verify-install-artefact.sh for why.
root_seed_hex=5858585858585858585858585858585858585858585858585858585858585858
session_token=removal-exercise-token
founder_password=removal-exercise-password
pack_key=harborline.platform
# The version PlatformPackPreloadHostedService installs. Read from source rather than repeated, so
# a version bump does not quietly turn the deactivate below into a no-op that still prints "gone".
pack_version=$(grep -oE 'PackVersion = "[0-9.]+"' \
  "$repo_root/apps/local-node-host/Data/PackProjection/PlatformPackPreloadHostedService.cs" |
  head -1 | sed 's/.*"\(.*\)"/\1/')

# The definition kinds the package contributes. A compiled inspector for a retired pillar would
# serve its own surface whether or not a pack declared it, so all five are probed after removal.
kinds="ViewDefinition FormDefinition ReportDefinition DataExchangeDefinition ScheduleDefinition"

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

fail() { echo "removal-exercise: $*" >&2; exit 1; }

case "$(uname -s)" in
  Darwin|Linux) exe="" ;;
  *)            exe=".exe" ;;
esac

[ -n "$pack_version" ] || fail "could not read the platform pack version from the preload service"

# GET <path>: body into $clean_root/body.txt, HTTP status on stdout.
probe() {
  curl -sS -o "$clean_root/body.txt" -w '%{http_code}' \
    -H "Authorization: Bearer $session_token" \
    "http://127.0.0.1:$port$1" 2>/dev/null
}
body() { head -c 300 "$clean_root/body.txt" | tr -d '\r\n'; }

# The ids in the last response whose PROVENANCE is the named pack. A grep over the raw bytes cannot
# tell a provenance field from the same string inside a definition body, and cannot name what it
# found. node is already a required tool for this gate, so the answer is parsed rather than matched.
pack_definitions() {
  node -e '
    const read = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"))
    const entries = Array.isArray(read) ? read : (read.entries || [])
    process.stdout.write(entries
      .filter(entry => entry && entry.provenance && entry.provenance.packKey === process.argv[2])
      .filter(entry => process.argv[3] !== "reachable" || entry.status !== "Withdrawn")
      .map(entry => entry.id + "@" + entry.version + "[" + entry.status + "]")
      .join(" "))
  ' "$clean_root/body.txt" "$1" "${2:-all}" 2>/dev/null
}

# The workspace ids in the last navigation response.
navigation_workspaces() {
  node -e '
    const read = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"))
    process.stdout.write(((read.pack || {}).seedWorkspaces || []).map(w => w.id).join(" "))
  ' "$clean_root/body.txt" 2>/dev/null
}

if curl -fsS "http://127.0.0.1:$port/health" -o /dev/null 2>/dev/null; then
  fail "something is already listening on 127.0.0.1:$port; set HARBORLINE_REMOVAL_EXERCISE_PORT"
fi

artefact="$repo_root/artifacts/publish/removal-exercise"
bash "$repo_root/eng/publish-local-node.sh" --out "$artefact" || fail "publish failed"

clean_root=$(mktemp -d 2>/dev/null || mktemp -d -t harborline-removal-exercise)
clean="$clean_root/node"
data="$clean_root/data"
mkdir -p "$clean" "$data"
cp -R "$artefact/." "$clean/"
[ -e "$clean/Harborline.LocalNodeHost.csproj" ] && fail "the clean directory carries a csproj"

founder_hash=$("$clean/Harborline.Api.LocalNodeHost$exe" hash-web-password "$founder_password") ||
  fail "hash-web-password failed"

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

healthy=""
for _ in $(seq 1 120); do
  if curl -fsS "http://127.0.0.1:$port/health" -o /dev/null 2>/dev/null; then healthy=yes; break; fi
  kill -0 "$host_pid" 2>/dev/null || break
  sleep 2
done
if [ -z "$healthy" ]; then
  echo "----- host log -----" >&2; cat "$clean_root/host.log" >&2
  fail "the published node never answered /health on 127.0.0.1:$port"
fi

# The addressed surfaces the installed package is claimed to be the only source of. `forms` stands
# for the thirteen members because the claim under test is about the package; T-585 item 1 covers
# per-member coverage from the register.
surfaces="
/api/local-node/catalogue/definitions/ViewDefinition/platform.list.forms
/api/local-node/catalogue/definitions/ViewDefinition/platform.health.forms
/api/local-node/catalogue/definitions/ViewDefinition/platform.browse.forms
/api/local-node/catalogue/definitions/ViewDefinition/platform.list.views
/api/local-node/catalogue/definitions/ViewDefinition/platform.list.data-exchanges
/api/local-node/catalogue/definitions/FormDefinition/platform.detail.form
/api/local-node/catalogue/definitions/FormDefinition/platform.pack.author
"
# The installer must survive removal: "removing the package leaves installer, not broken Workshop".
installer="
/health
/api/local-node/packs/installed
"

echo
echo "removal-exercise: BEFORE removal -- the installed package is serving the surface"
present=0
for path in $surfaces; do
  status=$(probe "$path")
  echo "  $status  GET $path"
  case "$status" in 200) present=$((present + 1)) ;; esac
done
[ "$present" -gt 0 ] ||
  fail "nothing answered before removal; the exercise would prove the absence of a surface that was never there"

status=$(probe /api/local-node/packs/installed)
echo "  $status  GET /api/local-node/packs/installed -> $(body)"
grep -q "$pack_key" "$clean_root/body.txt" ||
  fail "$pack_key $pack_version is not installed before removal; there is nothing to remove"

# Everything the AFTER half will assert the absence of, asserted POSITIVELY here first. Without
# this, a declaration that never carried a Workshop workspace and a catalogue that never carried a
# platform definition would let both absence checks below pass by finding nothing.
status=$(probe /api/local-node/navigation/workspaces)
echo "  $status  GET /api/local-node/navigation/workspaces -> workspaces: $(navigation_workspaces)"
case " $(navigation_workspaces) " in
  *" workshop "*) ;;
  *) fail "the Workshop workspace is not declared before removal; the absence check below would be vacuous" ;;
esac
before=""
for kind in $kinds; do
  probe "/api/local-node/catalogue/definitions?kind=$kind" > /dev/null
  found=$(pack_definitions "$pack_key")
  echo "  $kind from $pack_key: ${found:-none}"
  before="$before$found"
done
[ -n "$before" ] ||
  fail "the catalogue carries no $pack_key definition before removal; the absence check below would be vacuous"

echo
echo "removal-exercise: REMOVING $pack_key $pack_version through the ordinary installer route"
remove_status=$(curl -sS -o "$clean_root/body.txt" -w '%{http_code}' \
  -X POST -H "Authorization: Bearer $session_token" -H 'Content-Type: application/json' \
  -d "{\"packKey\":\"$pack_key\",\"version\":\"$pack_version\"}" \
  "http://127.0.0.1:$port/api/local-node/packs/deactivate" 2>/dev/null)
echo "  $remove_status  POST /api/local-node/packs/deactivate -> $(body)"
[ "$remove_status" = "200" ] || fail "the installed package could not be removed ($remove_status)"

echo
echo "removal-exercise: AFTER removal -- no route answers with the package surface"
answered=""
for path in $surfaces; do
  status=$(probe "$path")
  echo "  $status  GET $path -> $(body)"
  case "$status" in 200) answered="$answered $path" ;; esac
done
status=$(probe /api/local-node/navigation/workspaces)
echo "  $status  GET /api/local-node/navigation/workspaces -> workspaces: $(navigation_workspaces)"
# A 200 here is the route answering, not the package surface: the declaration is whatever the ACTIVE
# composition contains, and what must be gone is the removed package's workspace.
case " $(navigation_workspaces) " in
  *" workshop "*) answered="$answered /api/local-node/navigation/workspaces(workshop workspace still declared)" ;;
esac
[ -z "$answered" ] ||
  fail "the installed package was not the only source of the surface; these still answer:$answered"

echo
echo "removal-exercise: AFTER removal -- no compiled inspector is reachable"
reachable=""
for kind in $kinds; do
  status=$(probe "/api/local-node/catalogue/definitions?kind=$kind")
  # Withdrawn is the retraction's own end state -- deactivation withdraws a definition rather than
  # deleting it ("no seed or tenant-data deletion"), so the row survives as lifecycle evidence and
  # nothing can be rendered from it. What must not survive is a REACHABLE one.
  survivors=$(pack_definitions "$pack_key" reachable)
  retracted=$(pack_definitions "$pack_key")
  # The finding names the definitions, not the count: "one still reachable" does not tell a reader
  # WHICH surface came back, and which one is the whole question.
  echo "  $status  GET /api/local-node/catalogue/definitions?kind=$kind -> reachable: ${survivors:-none}; rows: ${retracted:-none}"
  [ -n "$survivors" ] && reachable="$reachable [$kind: $survivors]"
done
[ -z "$reachable" ] ||
  fail "a compiled inspector still serves $pack_key definitions after removal:$reachable"

echo
echo "removal-exercise: AFTER removal -- the installer is still there"
for path in $installer; do
  status=$(probe "$path")
  echo "  $status  GET $path"
  [ "$status" = "200" ] || fail "removing the package broke the installer: $path answered $status"
done

kill "$host_pid" 2>/dev/null
for _ in $(seq 1 15); do kill -0 "$host_pid" 2>/dev/null || break; sleep 1; done
kill -9 "$host_pid" 2>/dev/null
host_pid=""

echo
echo "removal-exercise: pass (publish, boot, surface present, remove, surface gone, no compiled inspector, installer intact)"
