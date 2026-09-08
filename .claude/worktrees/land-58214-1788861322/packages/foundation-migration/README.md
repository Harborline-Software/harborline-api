# Harborline.Api.Foundation.Migration

Foundation-tier substrate for cross-form-factor migration semantics — implements [ADR 0028](../../docs/adrs/0028-crdt-engine-selection.md) amendments A5 (cross-form-factor migration) + A8 (post-council surface fixes).

## Phase 1 scope (this slice)

- `FormFactorProfile` — host hardware-tier snapshot.
- `HardwareTierChangeEvent` — re-profile event fired on storage / network / sensor / power / adapter changes.
- 8 enum discriminators for form factor, input modality, display class, network posture, power profile, sensor surface, triggering event, sequestration flag.
- Canonical-JSON wire format: camelCase keys + `JsonStringEnumConverter` on every enum (mirrors W#34 Foundation.Versioning's A7.8 pattern).
- Reuses `Harborline.Api.Foundation.Versioning.InstanceClassKind` per A7.6 + A8 — no enum duplication.

## Subsequent phases

- **P2** — `DerivedSurface` filter + 8-form-factor migration-table lookup.
- **P3** — Invariant DLF (data-loss-vs-feature-loss) sequestration logic + `ISequestrationStore`.
- **P4** — 10 new `AuditEventType` constants + `MigrationAuditPayloads` factory + 6h `AdapterRollbackDetected` dedup per A8.7 + W#32 both-or-neither audit overload.
- **P5** — `AddInMemoryMigration()` DI extension + `apps/docs/foundation/migration/overview.md` + ledger flip.
