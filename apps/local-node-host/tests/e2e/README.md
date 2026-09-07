# Two-process, two-user comms + enrollment E2E harness (Tier 1, automated)

`two-process-comms-e2e.sh` drives the **real** two-user comms/enrollment flow
against **two real `local-node-host` OS processes** over real sockets — no GUI,
no second machine, runnable on a single Mac and CI-able.

It is the automated backend equivalent of the Mac↔Surface hardware verify
(cerebrum [2026-06-21] milestone): it exercises the *shipping* HTTP routes + the
7473-class wire-enrollment + forge-proof comms attribution end-to-end, in CI, on
one host.

## Run it

```bash
# from the Harborline repo root (or anywhere — the script self-locates):
apps/local-node-host/tests/e2e/two-process-comms-e2e.sh
```

Options (env vars):

| Var | Default | Meaning |
|---|---|---|
| `SKIP_BUILD=1` | off | Reuse the last `dotnet build` output (faster re-runs) |
| `KEEP_DIRS=1` | off | Keep per-run data dirs + node logs for triage (else wiped) |
| `A_HTTP_PORT` / `B_HTTP_PORT` / `C_HTTP_PORT` | 5581 / 5582 / 5583 | Loopback HTTP ports |
| `A_SYNC_PORT` / `B_SYNC_PORT` / `C_SYNC_PORT` | 17473 / 17474 / 17475 | Per-node gossip listener ports |
| `READY_TIMEOUT_SECS` | 90 | Cold-start + team-bootstrap readiness budget |
| `CONVERGE_TIMEOUT_SECS` | 45 | Comms-convergence poll budget |

**Exit:** `0` = GREEN (both directions converge + attributed; no-invite rejected);
non-zero = a FAILED assertion or setup error (with diagnostics + log tails).

Requires `dotnet`, `jq`, `curl`, `lsof` (and `openssl` or `python3` for seeds).

## What it asserts

1. **Two real processes spawn** — User A + User B, each with a distinct root seed
   (⇒ two **distinct verified authors** — the real two-user case, not the
   shared-root shortcut), a clean per-run data dir, distinct HTTP ports, and
   **distinct gossip ports** (see the sync-port finding below).
2. **A mints an invite** — `POST /admission/invites`.
3. **B joins over the wire** — `POST /admission/join` → B dials A's gossip
   listener, enrolls from the invite, **adopts A's team**, and rebinds its gossip
   daemon to its A-team key (the enrollment).
4. **A → B comms converges, attributed to A** — `POST /comms` on A, poll
   `GET /comms` on B; assert the body appears **attributed to A's verified
   author** (forge-proof).
5. **B → A comms converges, attributed to B** — the reverse.
6. **Distinct verified authors** — A's and B's author party ids differ.
7. **No-invite node rejected** — a fresh node Z with no invite is rejected at
   `/admission/join` (opaque 400, no leak) and never receives team traffic.

**Teardown** kills both/all processes, wipes the per-run data dirs, and verifies
no gossip port is left held (the orphan-7473 class of bug) — it never leaves a
stray `local-node-host` holding a port.

**Determinism:** convergence + readiness are poll-with-timeout, not sleeps.

## The sync-port finding (orphan-7473)

The gossip/sync listener defaults to the **fixed port 7473** in production:
the app's `src-tauri/src/peer_config.rs` injects
`LocalNode__Sync__BindAddress=0.0.0.0:7473`. Two app-spawned nodes on one
host therefore collide on bind (the orphan-7473 bug — cerebrum [2026-06-21]).

**The port is already configurable** — the host exposes
`LocalNode:Sync:BindAddress` (`SyncTransportOptions.BindAddress`, consumed at
`Program.cs` → `SyncEndpoint.NormalizeTcpEndpoint`), which accepts any
`host:port`. The fixed 7473 is only the *Harborline App's injected default*, not a
host-level hard-code. So a two-process-on-one-host harness needs **no host code
change** — it assigns A/B/Z **distinct fixed gossip ports** via that existing
knob. (Recorded in the dispatch return + cerebrum.)

## How the nodes are configured (mirrors the Harborline App)

Each node is spawned with env mirroring exactly what the Harborline App's
`node_supervisor` + `peer_config` inject, except `BindAddress` carries a
per-node-distinct port:

| Env | Role |
|---|---|
| `LocalNode__RootSeedHex` | distinct 32-byte root ⇒ distinct verified author + keystore bypass |
| `LocalNode__SessionToken` | per-process caller-auth token (loopback ≠ trust) |
| `LocalNode__TeamId` | pin the genesis team deterministically (A's is the join target) |
| `LocalNode__MultiTeam__Enabled=false` | single-team node (the sidecar contract) |
| `LocalNode__Sync__ListenForPeers=true` | offer the 7473 pre-trust enrollment channel + inbound gossip |
| `LocalNode__Sync__BindAddress=127.0.0.1:<distinct>` | the per-node gossip listener (the collision fix) |
| `LocalNode__Sync__Peers__0=<other-node>` | static-peer dial (trust is roster-anchored; address is just reachability) |
| `LocalNode__Enrollment__AdmitterSyncEndpoint=<A>` | (B/Z only) A's gossip listener to enroll over |
| `ASPNETCORE_URLS=http://127.0.0.1:<http>` | pin the loopback HTTP port the harness POSTs to |

## Why two PROCESSES (not the in-process xUnit tests)

The in-process tests (`EnrolledPeerConnectSyncTests`,
`BSideEnrollmentJoinE2ETests`) hand-build daemons / `TeamContext`s in one
process; they proved the protocol but each used a convenient shortcut (in-proc
transport, ephemeral ports, `NoopStoreActivator`) that masked a real
production-config bug (cerebrum [2026-06-21] "recurring lesson, 5th"). This
harness runs the **shipping binary** as separate processes with the
**production config** (fixed gossip port, real SQLCipher store, real wire) — the
only form that can catch transport/lifecycle/config bugs.

## Extension point — C2-C6 DMs

The harness spawns nodes via a generic `spawn_node` and asserts via generic
helpers, so a 3rd enrolled node C + an A↔B DM + a "C never receives it"
assertion drop in cleanly. The seam is the `RUN_DM_EXTENSION` block near the
bottom of the script (currently inert). The primitives a DM test plugs into:

- `spawn_node` / `wait_ready` / `wait_team_ready` — bring C up + enroll it
- the conversation-addressed routes `/api/local-node/comms/{conversationId}`
  (`CommsRoutes.ConversationRoute`) — POST/GET a `dm:<a>:<b>` conversation
- `assert_comms_converged_attributed <reader> <body> <author> <because>` — the
  DM converges between the two participants, attributed
- `assert_comms_never_received <C> <body> <window> <because>` — the DM body
  **never** appears on the non-participant C

Enable once the per-conversation DM access/wire-fan-out ships (CommsRoutes C2+).
