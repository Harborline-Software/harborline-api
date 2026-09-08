# Harborline.Api.Foundation.Forms

**Foundation-tier dynamic-forms keystone substrate (ADR 0055).**

This package lands the canonical type surface for the dynamic-forms
substrate — the single load-bearing record (`FormDefinition`) plus the
overlay (`HarborlineOverlay`, `FieldOverlay`, `FormSection`, `SectionAccess`,
`RuleDefinition`) plus the `IFormDefinitionStore` CRUD/lifecycle facade,
with an in-memory reference implementation suitable for tests and for
single-process bootstrapping.

## Scope (keystone PR)

This is **type-surface only** — the keystone establishes the canonical
records every other surface binds against. The companion packages that
compose this keystone ship in subsequent PRs:

| Package | Purpose | When |
|---|---|---|
| `Harborline.Api.Foundation.Compounds` | 20-primitive catalog (Money, Address, GeoPoint, Coding, etc.) | Phase 2 per ADR 0055 |
| `Harborline.Api.Foundation.Forms.Engine` | `IFormEngine` rendering + section-permission enforcement | Phase 5 |
| `Harborline.Api.Foundation.RuleEngine` | Three-tier rule evaluator (JSON Schema / JsonLogic / Power Fx v2) | Phase 4 |
| `foundation-assets-postgres` (extended) | JSONB entity-instance store + tenant-key-encrypted PII | Phase 3 |
| `accelerators/anchor` (extended) | Admin authoring UX (schema editor + section/role editor + rule builder) | Phase 6 |

## Why a foundation-tier `FormDefinition` distinct from kernel `Schema`

The kernel-tier `Harborline.Api.Kernel.Schema.Schema` (in `kernel-schema-registry`)
holds raw JSON Schema documents content-addressed by their CID — the lowest
level of the schema substrate. The foundation-tier `FormDefinition` here is the
higher-level composite: a content-addressed `SchemaRef` into the kernel
registry **plus** the Harborline overlay (sections, rules, permissions, i18n)
**plus** lifecycle metadata (status, version, tenant scope, authorship).
Consumers (form engine, entity store, authoring UX) need the composite; the
keystone is what they bind to.

The keystone deliberately **does not inline the JSON Schema body** — it
references it by content address (ADR 0055 OQ-3, dual-council ratified).
This preserves the kernel registry's INV-S5 content-addressing /
TOCTOU-immutability guarantee and keeps the kernel registry the single
source of truth for schema bodies. Consumers that need the document body
resolve it via the kernel registry; the foundation-tier composite carries
everything beyond the document itself.

The two layers are intentionally separable so callers operating below the
form-substrate seam (federation peers exchanging raw schemas,
schema-validation unit tests, etc.) can use the kernel registry without
taking a foundation-tier dependency.

## Reference docs

- **ADR 0055** — Dynamic Forms Substrate (Accepted via earlier repository ticket #209)
- **ADR 0001** — Schema Registry Governance (the keystone's `IFormDefinitionStore` is the v1 implementation of the governance contract)
- **ADR 0005** — Type Customization Model (`FormDefinitionLineage` formalizes)
- **ADR 0028** — CRDT engine (schema definitions sync via this substrate; the keystone records the shape, the sync substrate carries the bytes)
- **ADR 0032** — Macaroon capability model (the form engine intersects `FormSection.Access` with the actor's macaroon scope; the keystone declares the access shape)
- **ADR 0046** — Key-loss recovery (`PiiSensitivity.Sensitive` fields encrypt at rest via this substrate's primitives)
- **ADR 0049** — Audit Trail Substrate (the production registry emits schema-lifecycle audit records via this substrate; the in-memory reference impl is deliberately audit-free)
