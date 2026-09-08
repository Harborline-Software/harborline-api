#!/usr/bin/env bash
# =============================================================================
# Two-process, two-user comms + enrollment E2E harness (Tier 1, automated)
# =============================================================================
#
# Drives the REAL two-user comms/enrollment flow against TWO REAL local-node-host
# OS PROCESSES over real sockets — no GUI, no second machine, runnable on one Mac
# and CI-able. This is the automated backend equivalent of the Mac<->Surface
# hardware verify: it proves the production HTTP routes + the 7473 wire-enrollment
# + forge-proof comms attribution end-to-end, in CI, on a single host.
#
# WHY a two-PROCESS harness (not the in-process xUnit tests): the in-process
# tests (EnrolledPeerConnectSyncTests, BSideEnrollmentJoinE2ETests) hand-build
# daemons / TeamContexts in one process; they proved the protocol but each used a
# convenient shortcut (in-proc transport, ephemeral ports, NoopStoreActivator)
# that masked a real production-config bug (cerebrum [2026-06-21] "recurring
# lesson, 5th"). This harness runs the SHIPPING binary as two separate processes
# with the PRODUCTION config (fixed gossip port, real SQLCipher store, real wire),
# which is the only form that can catch transport/lifecycle/config bugs.
#
# THE FLOW (the canonical two-user flow, automated end-to-end):
#   1. Spawn user A + user B as two real local-node-host processes — distinct
#      root seeds (the REAL two-user case = two DISTINCT verified authors),
#      distinct HTTP ports, distinct gossip ports (the 7473 collision is avoided
#      by config, not luck — see SYNC-PORT note below), clean per-run data dirs,
#      and each its own per-process caller session token (as the Harborline App injects).
#   2. A: POST /admission/invites                  -> a single-use invite.
#   3. B: POST /admission/join (invite + anchor)   -> B dials A over the wire,
#         enrolls, ADOPTS A's team, rebinds its gossip daemon  (the enrollment).
#   4. A: POST /comms ; poll B GET /comms          -> assert B receives it,
#         attributed to A (forge-proof, distinct verified author).
#   5. B: POST /comms ; poll A GET /comms          -> assert A receives it,
#         attributed to B.
#   6. Negative: a no-invite node is rejected (cannot establish trust / no leak).
#
# SYNC-PORT FINDING (the orphan-7473 collision): the gossip/sync listener
# defaults to the FIXED port 7473 in production (peer_config.rs injects
# LocalNode__Sync__BindAddress=0.0.0.0:7473), so two app-spawned nodes on one
# host COLLIDE on bind. BUT the host itself already exposes the port as config:
# LocalNode:Sync:BindAddress accepts any host:port. So NO host code change is
# needed for a two-process-on-one-host harness — we assign A and B DISTINCT fixed
# gossip ports via that existing knob. (See README.md for the full finding.)
#
# EXTENSIBILITY (C2-C6 DMs): the harness spawns nodes via a generic `spawn_node`
# and asserts via generic helpers, so a 3rd node C + an A<->B DM + a "C never
# receives it" assertion drop in cleanly — see the EXTENSION POINT block near the
# bottom (currently inert; flip RUN_DM_EXTENSION=1 once the DM conversation
# wire-fan-out lands).
#
# USAGE:
#   apps/local-node-host/tests/e2e/two-process-comms-e2e.sh            # build + run
#   SKIP_BUILD=1 apps/local-node-host/tests/e2e/two-process-comms-e2e.sh  # reuse last build
#   KEEP_DIRS=1  apps/local-node-host/tests/e2e/two-process-comms-e2e.sh  # keep data dirs + logs for triage
#
# EXIT: 0 = GREEN (both directions converge + attributed; no-invite rejected);
#       non-zero = a FAILED assertion or a setup error (with diagnostics).
# =============================================================================

set -uo pipefail

# ── Locate the repo + project (script lives at apps/local-node-host/tests/e2e) ──
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"          # apps/local-node-host
REPO_ROOT="$(cd "$HOST_DIR/../.." && pwd)"           # shipyard repo root
CSPROJ="$HOST_DIR/Harborline.LocalNodeHost.csproj"

# ── Tunables ────────────────────────────────────────────────────────────────
# jq and lsof are NOT dependencies (ticket 258): neither is installed on every machine that runs this
# repo, node is. `J` is the stdlib-only node helper next to this script — JSON reads, JSON writes, free
# ports and the port-still-held check all go through it.
command -v node >/dev/null 2>&1 || { printf 'missing required tool: node\n' >&2; exit 2; }
J() { node "$SCRIPT_DIR/e2e-json.mjs" "$@"; }

# Ports are chosen FREE AT RUNTIME (a fixed port collides with a stray node or a parallel run — the
# orphan-7473 class of failure this harness exists to catch). Overridable for triage.
A_HTTP_PORT="${A_HTTP_PORT:-$(J freeport)}"
B_HTTP_PORT="${B_HTTP_PORT:-$(J freeport)}"
C_HTTP_PORT="${C_HTTP_PORT:-$(J freeport)}"
A_SYNC_PORT="${A_SYNC_PORT:-$(J freeport)}"
B_SYNC_PORT="${B_SYNC_PORT:-$(J freeport)}"
C_SYNC_PORT="${C_SYNC_PORT:-$(J freeport)}"
READY_TIMEOUT_SECS="${READY_TIMEOUT_SECS:-90}"   # generous: cold .NET start + team bootstrap + SQLCipher
CONVERGE_TIMEOUT_SECS="${CONVERGE_TIMEOUT_SECS:-45}"
KEEP_DIRS="${KEEP_DIRS:-0}"
SKIP_BUILD="${SKIP_BUILD:-0}"
# The join body's field set lives in ONE place, shared with the contract fence in the host test project.
JOIN_BODY_FIXTURE="join-body.fixture.json"

RUN_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/comms-e2e.XXXXXX")"
declare -a NODE_PIDS=()
declare -a NODE_DIRS=()
declare -A NODE_TOKEN=()    # name -> session token
declare -A NODE_HTTP=()     # name -> http base url
declare -A NODE_LOG=()      # name -> log file
declare -A NODE_PARTY=()    # name -> its OWN party id, read from GET /admission/identity

PASS=0
FAIL=0

# ── Pretty logging ──────────────────────────────────────────────────────────
c_green() { printf '\033[32m%s\033[0m\n' "$*"; }
c_red()   { printf '\033[31m%s\033[0m\n' "$*"; }
c_blue()  { printf '\033[36m%s\033[0m\n' "$*"; }
log()     { printf '[harness] %s\n' "$*"; }
step()    { printf '\n[harness] === %s ===\n' "$*"; }

ok()   { PASS=$((PASS+1)); c_green "  PASS  $*"; }
bad()  { FAIL=$((FAIL+1)); c_red   "  FAIL  $*"; }

# ── Teardown: kill both processes, wipe per-run dirs (never leave an orphan) ───
teardown() {
  step "TEARDOWN"
  for pid in "${NODE_PIDS[@]:-}"; do
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      log "stopping pid $pid"
      kill "$pid" 2>/dev/null || true
    fi
  done
  # give them a moment to release ports/sockets cleanly, then SIGKILL stragglers
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    local alive=0
    for pid in "${NODE_PIDS[@]:-}"; do
      if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then alive=1; fi
    done
    [[ $alive -eq 0 ]] && break
    sleep 0.5
  done
  for pid in "${NODE_PIDS[@]:-}"; do
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
      log "SIGKILL straggler pid $pid"
      kill -9 "$pid" 2>/dev/null || true
    fi
  done

  # Verify no gossip port is left held (the orphan-7473 class of bug).
  for p in "$A_SYNC_PORT" "$B_SYNC_PORT" "$C_SYNC_PORT"; do
    if J listening "$p"; then
      c_red "  WARN  a process is STILL accepting connections on gossip port $p after teardown"
    fi
  done

  if [[ "$KEEP_DIRS" == "1" ]]; then
    log "KEEP_DIRS=1 — preserving run dir for triage: $RUN_ROOT"
  else
    rm -rf "$RUN_ROOT" 2>/dev/null || true
    log "wiped run dir $RUN_ROOT"
  fi
}
trap teardown EXIT INT TERM

# ── Preconditions ────────────────────────────────────────────────────────────
require() { command -v "$1" >/dev/null 2>&1 || { c_red "missing required tool: $1"; exit 2; }; }
require dotnet
require curl
require node

# Distinct 32-byte (64-hex) root seeds — distinct roots => distinct verified
# authors (the REAL two-user case, not the shared-root shortcut). Deterministic
# per name so a re-run reproduces (determinism just aids triage).
seed_for()   { SEED_NAME="$1" J seed; }
rand_token() { J hex 32; }

# ── Build once (the shipping binary is what we spawn) ─────────────────────────
build() {
  if [[ "$SKIP_BUILD" == "1" ]]; then
    log "SKIP_BUILD=1 — reusing the existing build output"
    return 0
  fi
  step "BUILD  $CSPROJ (Debug)"
  if ! dotnet build "$CSPROJ" -c Debug -v minimal >"$RUN_ROOT/build.log" 2>&1; then
    c_red "build FAILED — see $RUN_ROOT/build.log"; tail -30 "$RUN_ROOT/build.log"; exit 2
  fi
  c_green "build OK"
}

DLL=""
resolve_dll() {
  DLL="$HOST_DIR/bin/Debug/net11.0/Harborline.Api.LocalNodeHost.dll"
  if [[ ! -f "$DLL" ]]; then
    c_red "host dll not found at $DLL (build first, or unset SKIP_BUILD)"; exit 2
  fi
}

# ── Spawn one real local-node-host process ────────────────────────────────────
#   spawn_node <name> <http-port> <sync-port> <team-id> [peer-endpoint] [admitter-sync-endpoint]
# Distinct root seed + distinct ports + clean data dir + own session token.
# ListenForPeers=true + a DISTINCT BindAddress gives every node a real, reachable,
# non-colliding 7473-class gossip listener on one host (the sync-port finding).
# peer-endpoint     (optional) -> a static peer to dial (LocalNode__Sync__Peers__0)
# admitter-endpoint (optional) -> A's sync listener to ENROLL over (B/Z only)
#
# Env is built into an array + handed to `env` so the OPTIONAL vars are simply
# omitted when empty (the `KEY=VAL \` inline-prefix idiom mis-parses an empty
# conditional expansion as a command — that was the first run's failure).
spawn_node() {
  local name="$1" http_port="$2" sync_port="$3" team_id="$4" peer_endpoint="${5:-}" admitter_endpoint="${6:-}"
  local dir="$RUN_ROOT/$name"
  local log_file="$RUN_ROOT/$name.log"
  local token; token="$(rand_token)"
  local seed; seed="$(seed_for "$name")"
  mkdir -p "$dir"

  NODE_DIRS+=("$dir")
  NODE_TOKEN["$name"]="$token"
  NODE_HTTP["$name"]="http://127.0.0.1:$http_port"
  NODE_LOG["$name"]="$log_file"

  log "spawning node '$name' http=$http_port sync=$sync_port team=$team_id${peer_endpoint:+ peer=$peer_endpoint}${admitter_endpoint:+ admitter=$admitter_endpoint} dir=$dir"

  # Env mirrors EXACTLY what the Harborline App's node_supervisor + peer_config inject,
  # except BindAddress carries a per-node-distinct port (the harness's job).
  #   RootSeedHex          -> distinct root => distinct author + keystore bypass
  #   SessionToken         -> per-process caller-auth token (loopback != trust)
  #   TeamId               -> pin the genesis team deterministically (A's is the
  #                           one B joins; both seed-derived would also work post
  #                           GenesisTeamId.Resolve, but pinning is explicit)
  #   MultiTeam:Enabled    -> false (single-team node, the sidecar contract)
  #   Sync:ListenForPeers  -> true  (offer the 7473 pre-trust enrollment channel +
  #                                  inbound gossip; required for A to be admitter)
  #   Sync:BindAddress     -> 127.0.0.1:<distinct> (the per-node gossip listener)
  #   Sync:EnableMdns      -> false (loopback static peers, no multicast in CI)
  #   Sync:Peers__0        -> the other node's gossip endpoint (static-peer dial;
  #                           trust is roster-anchored, address is just reachability)
  #   Enrollment:AdmitterSyncEndpoint -> (B/Z only) A's gossip listener to enroll over
  #   ASPNETCORE_URLS      -> pin the loopback HTTP port the harness POSTs to
  local -a envs=(
    "ASPNETCORE_URLS=http://127.0.0.1:$http_port"
    "DOTNET_ENVIRONMENT=Production"
    "LocalNode__RootSeedHex=$seed"
    "LocalNode__SessionToken=$token"
    "LocalNode__TeamId=$team_id"
    "LocalNode__DataDirectory=$dir"
    "LocalNode__MultiTeam__Enabled=false"
    "LocalNode__Sync__ListenForPeers=true"
    "LocalNode__Sync__BindAddress=127.0.0.1:$sync_port"
    "LocalNode__Sync__EnableMdns=false"
    # The OPERATOR declares the network's trust level; the host never infers it (LocalNodeOptions.cs:609-612,
    # fail-closed default Unknown). The pre-trust enrollment phase on the sync listener is offered ONLY on a
    # Known network (GossipDaemon.cs:1273 closes the connection with NO reply otherwise — which is exactly the
    # "Peer closed after 0 of 4 expected bytes" the harness hit). Loopback on one host IS the declared-known
    # case, so the harness declares it via the SAME existing knob production uses — no host code change.
    "LocalNode__Sync__NetworkTrust=Known"
    "LocalNode__Sync__RoundIntervalSeconds=1"
  )
  [[ -n "$peer_endpoint" ]]     && envs+=("LocalNode__Sync__Peers__0=$peer_endpoint")
  [[ -n "$admitter_endpoint" ]] && envs+=("LocalNode__Enrollment__AdmitterSyncEndpoint=$admitter_endpoint")

  env "${envs[@]}" dotnet "$DLL" >"$log_file" 2>&1 &
  local pid=$!
  NODE_PIDS+=("$pid")
  log "node '$name' pid=$pid log=$log_file"
}

# ── HTTP helpers (authed; /health is allowlisted, everything else needs token) ─
http() {
  # http <name> <method> <path> [json-body]  -> echoes "<status>\n<body>"
  local name="$1" method="$2" path="$3" body="${4:-}"
  local base="${NODE_HTTP[$name]}"
  local token="${NODE_TOKEN[$name]}"
  local tmp="$RUN_ROOT/.http_body.$$"
  local args=(-s -o "$tmp" -w '%{http_code}' -X "$method" -H "Authorization: Bearer $token")
  if [[ -n "$body" ]]; then args+=(-H 'Content-Type: application/json' -d "$body"); fi
  local code; code="$(curl "${args[@]}" "$base$path" 2>/dev/null)"
  local out; out="$(cat "$tmp" 2>/dev/null)"; rm -f "$tmp"
  printf '%s\n%s' "$code" "$out"
}
http_status() { sed -n '1p' <<<"$1"; }
http_body()   { sed -n '2,$p' <<<"$1"; }

# ── Wait for a node's HTTP /health to come up (allowlisted, no token) ──────────
wait_ready() {
  local name="$1"
  local base="${NODE_HTTP[$name]}"
  local deadline=$(( $(date +%s) + READY_TIMEOUT_SECS ))
  while (( $(date +%s) < deadline )); do
    if curl -s -o /dev/null -w '%{http_code}' "$base/health" 2>/dev/null | grep -q '^200$'; then
      ok "node '$name' HTTP is up ($base)"
      return 0
    fi
    sleep 0.5
  done
  bad "node '$name' HTTP did not come up within ${READY_TIMEOUT_SECS}s"
  log "---- last 40 lines of ${NODE_LOG[$name]} ----"
  tail -40 "${NODE_LOG[$name]}" 2>/dev/null | sed 's/^/    /'
  return 1
}

# Wait for a node's active team to be MATERIALIZED (the team-scoped transport
# identity ready) — /admission/identity returns 200 only once it is. Polling this
# avoids the team_not_ready (503) race when minting an invite or joining.
wait_team_ready() {
  local name="$1"
  local deadline=$(( $(date +%s) + READY_TIMEOUT_SECS ))
  while (( $(date +%s) < deadline )); do
    local r; r="$(http "$name" GET /api/local-node/admission/identity)"
    if [[ "$(http_status "$r")" == "200" ]]; then
      # The node is the SOLE source of truth for its own party id (AdmissionRoutes.cs:271-297,
      # NodeIdentityResponse.PartyId at :654) — the harness NEVER fabricates one. The join body's
      # joiningPartyId (AdmissionRoutes.cs:611) is exactly this value for the joining node.
      NODE_PARTY["$name"]="$(J get partyId <<<"$(http_body "$r")")"
      ok "node '$name' active team materialized (admission identity ready, partyId=${NODE_PARTY[$name]})"
      return 0
    fi
    sleep 0.5
  done
  bad "node '$name' active team never materialized within ${READY_TIMEOUT_SECS}s"
  tail -40 "${NODE_LOG[$name]}" 2>/dev/null | sed 's/^/    /'
  return 1
}

# ── Assert helpers ────────────────────────────────────────────────────────────
# Poll <reader>'s comms log until a message with the given body appears, then
# assert it is attributed to the EXPECTED author party id (forge-proof: distinct
# verified author). Returns 0 on success.
assert_comms_converged_attributed() {
  local reader="$1" expected_body="$2" expected_author="$3" because="$4"
  local started; started=$(date +%s)
  local deadline=$(( started + CONVERGE_TIMEOUT_SECS ))
  local last_authors=""
  while (( $(date +%s) < deadline )); do
    local r; r="$(http "$reader" GET /api/local-node/comms)"
    if [[ "$(http_status "$r")" == "200" ]]; then
      local body; body="$(http_body "$r")"
      local author
      author="$(MATCH_BODY="$expected_body" J authorof <<<"$body")"
      last_authors="$(J authors <<<"$body")"
      if [[ -n "$author" ]]; then
        if [[ "$author" == "$expected_author" ]]; then
          # Remember the OBSERVED convergence time; the non-leak window below is derived from it
          # rather than being a flat constant (a slower machine gets a proportionally longer window).
          LAST_CONVERGE_SECS=$(( $(date +%s) - started ))
          ok "$because  (body seen on '$reader' after ${LAST_CONVERGE_SECS}s, authorPartyId=$author)"
          return 0
        else
          bad "$because  message converged but attributed to '$author', expected '$expected_author' (forge-proof attribution mismatch)"
          return 1
        fi
      fi
    fi
    sleep 0.5
  done
  bad "$because  message '$expected_body' never converged on '$reader' within ${CONVERGE_TIMEOUT_SECS}s (authors seen: ${last_authors:-none})"
  return 1
}

# Assert a body NEVER appears on a reader within a bounded window (the DM
# negative + a general non-leak primitive). Returns 0 if it stays absent.
assert_comms_never_received() {
  local reader="$1" forbidden_body="$2" window_secs="$3" because="$4"
  local deadline=$(( $(date +%s) + window_secs ))
  while (( $(date +%s) < deadline )); do
    local r; r="$(http "$reader" GET /api/local-node/comms)"
    if [[ "$(http_status "$r")" == "200" ]]; then
      local hit
      hit="$(MATCH_BODY="$forbidden_body" J countof <<<"$(http_body "$r")")"
      if [[ "${hit:-0}" != "0" ]]; then
        bad "$because  forbidden body '$forbidden_body' LEAKED to '$reader'"
        return 1
      fi
    fi
    sleep 0.5
  done
  ok "$because  (body correctly absent from '$reader' over ${window_secs}s)"
  return 0
}

# =============================================================================
# RUN
# =============================================================================
step "TWO-PROCESS TWO-USER COMMS + ENROLLMENT E2E"
log "run root: $RUN_ROOT"

build
resolve_dll

# A's genesis team is a fixed, valid GUID we pin so B has a stable join target.
# (Post GenesisTeamId.Resolve a seed-derived team would also work; pinning is the
# explicit, deterministic choice and documents the join target.)
TEAM_A="11111111-1111-1111-1111-111111111111"
# B starts on its OWN distinct genesis team; on join it ADOPTS A's team.
TEAM_B="22222222-2222-2222-2222-222222222222"

A_SYNC_ENDPOINT="tcp://127.0.0.1:$A_SYNC_PORT"
B_SYNC_ENDPOINT="tcp://127.0.0.1:$B_SYNC_PORT"

# A = the ADMITTER/host: listens on its gossip port, knows B's address as a static
# peer (so once B is enrolled, A dials B for the gossip session). No admitter
# endpoint (A doesn't join anyone).
spawn_node "A" "$A_HTTP_PORT" "$A_SYNC_PORT" "$TEAM_A" "$B_SYNC_ENDPOINT"

# B = the JOINER: listens on its own gossip port, has A as its static peer AND as
# its enrollment admitter endpoint (it dials A's 7473-class listener to enroll).
spawn_node "B" "$B_HTTP_PORT" "$B_SYNC_PORT" "$TEAM_B" "$A_SYNC_ENDPOINT" "$A_SYNC_ENDPOINT"

step "WAIT FOR BOTH NODES READY"
wait_ready "A" || exit 1
wait_ready "B" || exit 1
wait_team_ready "A" || exit 1
wait_team_ready "B" || exit 1

# ── 1) A mints an invite ──────────────────────────────────────────────────────
step "A: POST /admission/invites  (mint a single-use invite)"
INVITE_RESP="$(http "A" POST /api/local-node/admission/invites '{}')"
if [[ "$(http_status "$INVITE_RESP")" != "200" ]]; then
  bad "A invite mint failed: status=$(http_status "$INVITE_RESP") body=$(http_body "$INVITE_RESP")"
  exit 1
fi
INVITE_BODY="$(http_body "$INVITE_RESP")"
TOKEN_ID="$(J get tokenId <<<"$INVITE_BODY")"
A_TEAM_ID="$(J get teamId <<<"$INVITE_BODY")"
A_GENESIS_PARTY="$(J get genesisPartyId <<<"$INVITE_BODY")"
A_GENESIS_PUBKEY="$(J get genesisPublicKey <<<"$INVITE_BODY")"
if [[ -z "$TOKEN_ID" ]]; then
  bad "A invite response missing tokenId: $INVITE_BODY"; exit 1
fi
ok "A minted invite token=$TOKEN_ID team=$A_TEAM_ID genesisParty=$A_GENESIS_PARTY"

# ── 2) B joins A's team over the wire (the enrollment) ────────────────────────
step "B: POST /admission/join  (dial A over the wire, enroll, adopt A's team)"
# The route binds the default ASP.NET Core camelCase JSON (no custom naming policy on the host). The
# body's SHAPE is NOT written out here — it comes from join-body.fixture.json, the single file the
# contract fence (JoinBodyFixtureContractTests) asserts against JoinTeamBody's required properties
# (AdmissionRoutes.cs:606-612). Ticket 258: the shape written inline here had drifted — it omitted
# joiningPartyId, which the route has required at AdmissionRoutes.cs:540, so every join was refused 400.
JOIN_BODY_JSON="$(TOKEN_ID="$TOKEN_ID" JOINING_PARTY_ID="${NODE_PARTY[B]}" TEAM_ID="$A_TEAM_ID" \
  GENESIS_PARTY_ID="$A_GENESIS_PARTY" GENESIS_PUBLIC_KEY="$A_GENESIS_PUBKEY" \
  J joinbody "$SCRIPT_DIR/$JOIN_BODY_FIXTURE")"
JOIN_RESP="$(http "B" POST /api/local-node/admission/join "$JOIN_BODY_JSON")"
if [[ "$(http_status "$JOIN_RESP")" != "200" ]]; then
  bad "B join FAILED: status=$(http_status "$JOIN_RESP") body=$(http_body "$JOIN_RESP")"
  log "---- A log tail ----"; tail -30 "${NODE_LOG[A]}" | sed 's/^/    /'
  log "---- B log tail ----"; tail -30 "${NODE_LOG[B]}" | sed 's/^/    /'
  exit 1
fi
JOINED_TEAM="$(J get teamId <<<"$(http_body "$JOIN_RESP")")"
ok "B joined team=$JOINED_TEAM (adopted A's team; daemon rebound)"

# No settling sleep: the convergence asserts below already POLL to a deadline, so a flat sleep only
# added dead time on a fast machine and was not long enough to help a slow one (checklist (g)).

# ── 3) A -> B comms, forge-proof attributed ───────────────────────────────────
step "A -> B  comms (assert B receives, attributed to A)"
MSG_AB="hello-from-A-$(date +%s)"
SEND_AB="$(http "A" POST /api/local-node/comms "$(MSG_BODY="$MSG_AB" J msgbody)")"
if [[ "$(http_status "$SEND_AB")" != "201" ]]; then
  bad "A comms POST failed: status=$(http_status "$SEND_AB") body=$(http_body "$SEND_AB")"; exit 1
fi
A_AUTHOR="$(J get authorPartyId <<<"$(http_body "$SEND_AB")")"
log "A posted '$MSG_AB' (authorPartyId on A=$A_AUTHOR)"
assert_comms_converged_attributed "B" "$MSG_AB" "$A_AUTHOR" \
  "A->B: a message posted on A converges to B, attributed to A's verified author" || true

# ── 4) B -> A comms, forge-proof attributed ───────────────────────────────────
step "B -> A  comms (assert A receives, attributed to B)"
MSG_BA="hello-from-B-$(date +%s)"
SEND_BA="$(http "B" POST /api/local-node/comms "$(MSG_BODY="$MSG_BA" J msgbody)")"
if [[ "$(http_status "$SEND_BA")" != "201" ]]; then
  bad "B comms POST failed: status=$(http_status "$SEND_BA") body=$(http_body "$SEND_BA")"; exit 1
fi
B_AUTHOR="$(J get authorPartyId <<<"$(http_body "$SEND_BA")")"
log "B posted '$MSG_BA' (authorPartyId on B=$B_AUTHOR)"
assert_comms_converged_attributed "A" "$MSG_BA" "$B_AUTHOR" \
  "B->A: a message posted on B converges to A, attributed to B's verified author" || true

# ── 4b) Distinct verified authors (forge-proof identity axis) ─────────────────
step "ASSERT distinct verified authors"
if [[ -n "$A_AUTHOR" && -n "$B_AUTHOR" && "$A_AUTHOR" != "$B_AUTHOR" ]]; then
  ok "two DISTINCT verified authors (A=$A_AUTHOR, B=$B_AUTHOR) — the real two-user case"
else
  bad "authors not distinct (A=$A_AUTHOR, B=$B_AUTHOR) — would be the shared-root residual, not real two-user"
fi

# ── 5) Negative: a no-invite node is rejected ─────────────────────────────────
# A fresh node Z that NEVER got an invite tries to join A. The enrollment must be
# rejected fail-closed (no team adoption), so Z can never converge with A.
step "NEGATIVE: a no-invite node is rejected"
TEAM_Z="33333333-3333-3333-3333-333333333333"
spawn_node "Z" "$C_HTTP_PORT" "$C_SYNC_PORT" "$TEAM_Z" "$A_SYNC_ENDPOINT" "$A_SYNC_ENDPOINT"
wait_ready "Z" || exit 1
wait_team_ready "Z" || exit 1

# Z forges a join with a token A never minted (random token id) but the real
# anchor — the invite gate must reject it.
BOGUS_TOKEN="$(date +%s)-never-minted"
# Z presents a WELL-FORMED body (the same fixture), so the refusal proves the INVITE GATE rejected it,
# not that a required field was missing. Z uses its OWN party id.
Z_JOIN_JSON="$(TOKEN_ID="$BOGUS_TOKEN" JOINING_PARTY_ID="${NODE_PARTY[Z]}" TEAM_ID="$A_TEAM_ID" \
  GENESIS_PARTY_ID="$A_GENESIS_PARTY" GENESIS_PUBLIC_KEY="$A_GENESIS_PUBKEY" \
  J joinbody "$SCRIPT_DIR/$JOIN_BODY_FIXTURE")"
Z_JOIN_RESP="$(http "Z" POST /api/local-node/admission/join "$Z_JOIN_JSON")"
Z_STATUS="$(http_status "$Z_JOIN_RESP")"
if [[ "$Z_STATUS" == "400" || "$Z_STATUS" == "409" ]]; then
  ok "no-invite node REJECTED at join (status=$Z_STATUS, opaque reason — no leak)"
else
  bad "no-invite node was NOT rejected (status=$Z_STATUS body=$(http_body "$Z_JOIN_RESP"))"
fi

# And prove A->B traffic NEVER reaches Z (Z is not on A's team).
MSG_LEAK="leak-check-$(date +%s)"
http "A" POST /api/local-node/comms "$(MSG_BODY="$MSG_LEAK" J msgbody)" >/dev/null
# The non-leak window is DERIVED from the convergence time observed on THIS machine above (4x, floor
# 8s), so a slow machine gets a proportionally longer window instead of a flat constant that would
# pass vacuously there.
LEAK_WINDOW_SECS=$(( 4 * ${LAST_CONVERGE_SECS:-2} )); (( LEAK_WINDOW_SECS < 8 )) && LEAK_WINDOW_SECS=8
assert_comms_never_received "Z" "$MSG_LEAK" "$LEAK_WINDOW_SECS" \
  "no-invite node never receives team traffic (rejected => no convergence)" || true

# =============================================================================
# EXTENSION POINT — C2-C6 DMs (currently inert)
# =============================================================================
# When the DM conversation wire-fan-out lands (per-conversation membership +
# access), enable this block by exporting RUN_DM_EXTENSION=1. It stands up a 3rd
# enrolled node C and asserts an A<->B DM is NEVER delivered to C. The harness
# primitives needed are already here:
#   · spawn_node_with_peer / wait_ready / wait_team_ready  — bring C up + enroll
#   · the conversation-addressed routes are /api/local-node/comms/{conversationId}
#     (CommsRoutes.ConversationRoute) — POST/GET a "dm:<a>:<b>" conversation
#   · assert_comms_converged_attributed <reader> <body> <author> <because>
#       — A<->B DM converges between the two participants
#   · assert_comms_never_received <C> <body> <window> <because>
#       — the DM body NEVER appears on C
if [[ "${RUN_DM_EXTENSION:-0}" == "1" ]]; then
  step "C2-C6 EXTENSION: A<->B DM, assert C never receives it"
  # 1. Enroll a 3rd node C into A's team (same shape as B's join above).
  # 2. DM_CONV="dm:${A_AUTHOR}:${B_AUTHOR}"  (the DM conversation id convention)
  # 3. POST /api/local-node/comms/$DM_CONV on A; assert it converges to B (a
  #    participant) attributed to A; assert it NEVER reaches C (a non-participant).
  # left intentionally unimplemented until the DM access/fan-out ships — the
  # primitives above are the seams it plugs into.
  c_blue "  (DM extension scaffold reached; per-conversation fan-out not yet shipped — see CommsRoutes C2+)"
fi

# =============================================================================
# RESULT
# =============================================================================
step "RESULT"
log "passed: $PASS   failed: $FAIL"
if [[ "$FAIL" -eq 0 ]]; then
  c_green "GREEN — two-process two-user comms E2E PASSED (both directions converge + attributed; no-invite rejected)"
  exit 0
else
  c_red "RED — $FAIL assertion(s) failed"
  exit 1
fi
