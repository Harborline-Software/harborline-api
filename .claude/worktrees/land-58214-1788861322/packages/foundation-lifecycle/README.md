# Harborline.Api.Foundation.Lifecycle

Cross-cutting **lifecycle-state substrate** — the shared vocabulary for "this row
is archived / soft-deleted" that every block can adopt without a storage migration.
Introduced by
[ADR 0108](../../docs/adrs/0108-archive-soft-delete-substrate.md) Step 1.

## What this is

A dependency-free foundation package that ships **two marker interfaces only**:

| Type | Semantics | Mental model |
|---|---|---|
| `IArchivable` | Recoverable archival. Excluded from default lists; **resolvable** for referential history; **restore allowed**; does **not** block transitions. | "put away, can take back out" |
| `ISoftDeletable` | Tombstone soft-deletion. Excluded from default lists; readable for audit; **blocks further state transitions**; not restorable by this contract. | "done / removed, kept for the record" |

`ArchivedAt` / `DeletedAt` are BCL `DateTimeOffset?` — `null ⇒ active` / `null ⇒ live`.

An entity **MAY implement both** (RemodelProject does); the two operations are
independent.

## Why it's dependency-free (ADR 0108 O-1)

Archival is its own cross-cutting concern, parallel to how multitenancy / session /
password-hashing each get a thin foundation package. The markers are domain-lifecycle
**semantics**, not persistence **mechanics**, so they do **not** live in
`foundation-persistence` — that package references `Microsoft.EntityFrameworkCore` and
`foundation-multitenancy`, and co-locating the markers there would force every block
that implements only `IArchivable`/`ISoftDeletable` to transitively pull in EF Core.

This package keeps the two interfaces **BCL-only**: zero `ProjectReference`, zero
`PackageReference`.

## Boundary-adapter discipline (how blocks adopt these)

- **Keep your existing storage representation.** Property's `DisposedAt`, Lease's
  terminal `LeasePhase`, Vendor's `VendorStatus.Inactive`, … — **no rip-and-replace
  migration**. Adapt at the service/repository boundary.
- **Default-list exclusion is the load-bearing invariant.** A default list query MUST
  exclude rows with a non-null `ArchivedAt` / `DeletedAt` unless an explicit
  `includeArchived` / `includeDeleted` flag is passed. This predicate lives at the
  **same repository seam as ADR 0092 tenant-keying** — a second `WHERE` predicate on an
  already-canonical boundary, not a new mechanism.
- **Timestamp-type conversion is an explicit adapter responsibility (F1).** The marker
  exposes BCL `DateTimeOffset?`, but cluster storage is not uniform — `Project` and
  `Invoice` store NodaTime `Instant?`, while `WorkOrder` and `Property` already store
  `DateTimeOffset?`. A NodaTime-backed adapter MUST convert `Instant? ↔ DateTimeOffset?`
  **explicitly** at the boundary (mechanical and lossless: `Instant` → UTC
  `DateTimeOffset`) — never a silent cast.
- **`ISoftDeletable`'s transition-block is a service-layer guard (O-2).** A write handler
  rejects a transition whose target's `DeletedAt is not null`, backed by parity tests. It
  is deliberately **not** a Roslyn analyzer (an analyzer catches call-shape, not runtime
  state). An optional "the service forgot to call the guard" analyzer is a future ADR (F3).

## Canonical adopter pattern

```csharp
// An entity carrying both lifecycle states (the RemodelProject / Project pattern):
public sealed class Project : IArchivable, ISoftDeletable
{
    public DateTimeOffset? ArchivedAt { get; private set; }   // null ⇒ active
    public DateTimeOffset? DeletedAt { get; private set; }    // null ⇒ live
    // … plus Archive() / SoftDelete() write methods on the aggregate.
}
```

The per-entity mapping (which entity gets which marker, and which need net-new write
paths) is the authoritative table in ADR 0108.
