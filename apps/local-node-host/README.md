# Harborline Local-Node Host

The headless background process that hosts the Harborline kernel runtime.
Wave 2.5 of the [paper-alignment plan](../../_shared/product/paper-alignment-plan.md).

## What it does

Paper [§4](../../_shared/product/local-node-architecture-paper.md) and
[§5.1](../../_shared/product/local-node-architecture-paper.md) describe the
local-node kernel as a persistent background service. This app is that
service. On start-up it composes and hosts:

- **Plugin registry + `INodeHost`** — lifecycle + extension-point contracts
  (Wave 1.1, `packages/kernel-runtime`)
- **Event log** — paper §2.5 / §8 persistent append-only log
  (Wave 1.3, `packages/kernel-event-bus`)
- **Encrypted local store** — SQLCipher + Argon2id + platform keystore
  (Wave 1.4, `packages/foundation-localfirst/Encryption`)
- **Quarantine queue** — paper §11.2 Layer 4 holding pen for failed offline writes
  (Wave 1.5, `packages/foundation-localfirst/Quarantine`)
- **Security primitives** — Ed25519 signing, X25519 key agreement, role keys
  (Wave 1.6, `packages/kernel-security`)
- **CRDT engine** — document abstraction over the chosen CRDT backend
  (Wave 1.2, `packages/kernel-crdt`)

At idle the host parks on `Task.Delay(Timeout.Infinite, stoppingToken)` so
it consumes no CPU while waiting for work. All actual per-tick activity
(event dispatch, sync gossip, projection rebuilds) is driven by the kernel
itself.

## Running locally

```bash
dotnet run --project apps/local-node-host/Harborline.LocalNodeHost.csproj
```

Stop with `Ctrl+C`. The worker traps cancellation, tears the node host down
through `Running → Stopping → Stopped`, then unloads plugins in reverse
topological order.

## Pairing GA gates

Before the first customer seed is created:

- Run a binary that includes the signed admission-provenance envelope
  (`admitted_via_token_id` and `minting_session_evidence`). A seed admitted by an older binary cannot be
  re-serialized into that shape and retain its original signature; create the customer seed only after this floor.
- Treat the admission and roster schemas as one operational migration set even though each `DbContext` records a
  separate history. Back up the encrypted node store before rollback. The pairing-binding `Down()` deletes pending
  bindings, and the roster-provenance `Down()` deletes admission evidence; a partial rollback is unsupported.
- The retired boot grant backfill and `signed_permissions` roster column are not an upgrade path. New admissions
  confer their durable grants when they are accepted, and there is no supported installation to migrate from the
  retired pre-version-3 permission evidence.
- Keep `LocalNode:Diagnostics:CommsDiagnosticLogging` off in the customer profile. Pairing diagnostics never log
  opaque token identifiers, even in development.

## Configuration

`appsettings.json` exposes the `LocalNode` section:

```json
{
  "LocalNode": {
    "NodeId": "…generated on first boot…",
    "TeamId": null,
    "DataDirectory": "…platform default…"
  }
}
```

Platform-conventional `DataDirectory` defaults:

| Platform | Default |
|---|---|
| Windows | `%LOCALAPPDATA%\Sunfish\LocalNode` |
| macOS | `~/Library/Application Support/Sunfish/LocalNode` |
| Linux | `$XDG_DATA_HOME/sunfish/local-node` (falls back to `~/.local/share/sunfish/local-node`) |

## Service-manager integration (roadmap — Wave 4)

Paper §4: *"run the container stack as a persistent background service
registered with the OS service manager (systemd on Linux, launchd on macOS,
Windows Service on Windows), starting at login and running quietly at idle."*

Wave 2.5 (this wave) ships the headless host **process**. Wave 4.5 is
expected to add:

- `Platforms/linux/` — a user-scoped systemd unit
- `Platforms/macos/` — a launchd agent property list (LaunchAgent)
- `Platforms/windows/` — Windows Service installer or WiX fragment

Because the host is a generic-host Worker Service, no host-code changes are
needed to register under any of these service managers — the same binary runs
under `dotnet run` during development and under the service manager in
production. See [`Platforms/README.md`](Platforms/README.md).

## How other processes connect

The application shell (Anchor — Wave 3.3) and the relay-mode Bridge do not
re-host the kernel. They connect to **this** already-running process over
the sync-daemon transport (Unix domain socket on macOS / Linux, named pipe
on Windows). The transport itself lands in Wave 2.1; until that wave lands
the host is usable only for in-process scenarios and integration tests.

## What does not live here

- Platform-specific service installers → Wave 4.5 (`Platforms/…` stubs only)
- Sync-daemon transport listener → Wave 2.1 (`packages/kernel-sync-daemon/`)
- Anchor shell → Wave 3.3 (`accelerators/anchor/`)
- Example / seed plugins → not this wave; empty plugin set is the expected
  boot state. The kernel logs `Loaded 0 plugin(s)` and moves on.
