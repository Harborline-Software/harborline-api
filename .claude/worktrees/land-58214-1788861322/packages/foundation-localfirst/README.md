# Harborline.Api.Foundation.LocalFirst

Offline-capable local-operation primitives — offline store, outbound queue, sync engine, conflict resolver, data export/import.

Contracts plus minimal in-memory references. Implements [ADR 0012](../../docs/adrs/0012-foundation-localfirst.md).

## What this ships

### Contracts

- **`IOfflineStore`** — local persistence seam for offline operation; reads return whatever was last cached + any local mutations not yet synced.
- **`IOutboundQueue`** — durable queue of operations performed locally that need to be replayed against the server.
- **`ISyncEngine`** — orchestrator that drains the outbound queue + pulls server-side updates + invokes the conflict resolver on collisions.
- **`ISyncConflictResolver`** — strategy for deciding sync collisions. The shipped default is
  `CausalConflictResolver` (ADR 0053): causal dominance resolves, identical payloads resolve,
  and everything else is surfaced as a `ConflictResolution.Ask` for a person or a richer
  module-owned resolver to decide. Last-write-wins is not offered — per ADR 0053,
  "It silently discards the edit nobody saw." Engines constructing a `SyncConflict` must
  populate `LocalClock` / `RemoteClock`; without a causal basis there is nothing to decide with.
- **`IDataExportService`** — a per-tenant portability dump. It is not the Data Exchange
  row-set/mapping surface and is not a backup or restore mechanism.
- **`IDataImportService`** — a declared portability-import seam with no default implementation.

### Reference impls

- **In-memory** store, queue, and conflict-resolution fixtures.
- **JSON portability export** over registered contributors. The default contributor covers only
  keys in `IOfflineStore` under `tenants/{tenant-id}/`; stores without a contributor are not in the
  package. This is incremental coverage, not evidence that all product data is exportable.

## When to use this

Anchor (per ADR 0031 / 0032) is the primary consumer — multi-team workspace switching with offline tolerance. Bridge in SaaS posture is online-first; the relay posture (per ADR 0026) leans on the same primitives differently.

Mobile clients (W#23 iOS field-capture, when shipped) will also consume these contracts (or their kernel-substrate equivalents — the Anchor + iOS sync surfaces are converging per the Mission Space Matrix research, W#33).

## ADR map

- [ADR 0012](../../docs/adrs/0012-foundation-localfirst.md) — local-first operation
- [ADR 0026](../../docs/adrs/0026-bridge-posture.md) — Bridge SaaS vs Relay postures
- [ADR 0031](../../docs/adrs/0031-bridge-hybrid-multi-tenant-saas.md) — hybrid hosted-node-as-SaaS
- [ADR 0032](../../docs/adrs/0032-multi-team-anchor-workspace-switching.md) — Anchor multi-team workspace switching

## See also

- [apps/docs Overview](../../apps/docs/foundation/localfirst/overview.md)
- [Harborline.Api.Kernel.Sync](../kernel-sync/README.md) — kernel-tier sync substrate consumed by this layer
