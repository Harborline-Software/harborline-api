# Harborline.Api.Blocks.Assets.Registry

The **Asset Type System** substrate — ADR 0101 Rev 3.1, **Wave 1** (dual-council cleared:
`.NET-architect` AMBER + `security-engineering` AMBER, no RED).

A generic **typed-entity registry** that lets new inspection/asset stories cost *configuration*,
not *architecture*. It lives beside — and is deliberately decoupled from — the Rev 2 concrete
`Asset` domain (`blocks-assets`) and `blocks-property-equipment.Equipment`, both of which are
**frozen** pending the promotion wave (the two generic models never grow in parallel — A1).

## What Wave 1 ships (substrate + contracts only — no UI, no live capture, no roll-up compute)

| Area | Types |
|---|---|
| **Typed-entity registry** | `EntityTypeSeed` (immutable shared template), `EntityType` (tenant row / override), `EntityTypeDescriptor`, `EntityTrait` (container/maintainable/movable, composable), `RegistryEntity`, `FormBindingRef` (content-addressed `FormDefinitionId` + pinned `SemanticVersion`), `IEntityTypeRegistry` / `IRegistryEntityRepository` |
| **Typed dated relationships** | `TypedRelationship`, `RelationshipKind` (contains / located-at / part-of-system), `ITypedRelationshipStore` — same-tenant, `TenantId.System`-rejecting, containment cycle-guarded + depth-bounded, hierarchy/location as **as-of-clock** VIEWS (no stored parent pointer) |
| **Condition record + binding contract (A3)** | `ConditionAssessment` (keyed off the **generic** `RegistryEntityId`, never `AssetId` — A4), `ConditionRating` (configurable scale), `ConditionRatingFieldBinding` (the *documented* Wave-2 field→side-record contract; no projector built) |
| **Per-field scoring layer (A2 + F1)** | `FieldScoringMetadata`, `FieldScoringOverlay` (separate per-field layer, `FieldOverlay.config` shape precedent — does **not** touch the ADR 0140 `AspectOverlay`), `ScoringCascadeResolver` (raise-strictness-only seed floors; `ScoringFloorViolationException`) |
| **X-AUDIT durable layer** | `IRegistryAuditLog` + `InMemoryRegistryAuditLog` — every mutation appends one hash-chained event |
| **Freeze guard (A1)** | `AssetModelFreezeArchitectureTests` — pins the current consumer set of the two frozen models |

## Council folds honored (review gates)

- **A1** — freeze guard on concrete `Asset` + `Equipment` (allowlist of current consumers).
- **A2 / F1** — scoring rides a **separate** per-field layer; seed critical/safety floors are
  raise-strictness-only (provenance-visible rejection); owner-side re-derivation documented.
- **A3** — condition record + binding *contract* only; the submission→side-record projection seam
  is an explicit Wave-2 task.
- **A4** — condition + scoring key off the generic entity ref, never a concrete `AssetId`/`EquipmentId`.
- **A5a/b/c** — same-tenant edges + sentinel reject; containment cycle-guard + depth-bound;
  as-of-clock queries (never ambient now).
- **F2** — (1) immutable shared seeds + tenant override rows; (2) form-binding reads are
  tenant-authorized (a `FormDefinitionId` hash grants nothing).

## Deliberately deferred (named, not silent)

Wave 2 — Harborline App UI, per-type property forms, inspection-runner binding, and the
submission→`ConditionAssessment` projection seam. Wave 3 — roll-up compute (item → category →
discipline → composite), threshold/budget views. Wave 4 & promotion wave — campaigns/sampling/
delegation and the `Equipment`/`Asset` seeded-type migration (each carries its own dual-council).

## Wiring

```csharp
services.AddInMemoryAssetTypeSystem();
```

Swap the audit log and stores for persistence-backed implementations behind the same interfaces in
production hosts (the same host-swap pattern as the concrete Asset domain's lifecycle-event store).
