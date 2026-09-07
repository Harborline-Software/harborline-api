# Harborline.Api.Kernel — Kernel Contract Façade

> **Looking for the paper's kernel runtime?** That lives in a sibling
> package: [`Harborline.Api.Kernel.Runtime`](../kernel-runtime/README.md). `Harborline.Api.Kernel` (this package) is the **typed-contract façade** for spec §3
> primitives. `Harborline.Api.Kernel.Runtime` is the **running kernel** per paper
> §5.1 — node lifecycle, plugin registry, extension-point contracts. The
> two packages coexist per [ADR 0027](../../docs/adrs/0027-kernel-runtime-split.md).

`Harborline.Api.Kernel` is a thin façade package that exposes the Harborline platform
spec §3 kernel primitives at the Layer 2 surface called out in §2.3 of the
architecture spec.

It ships as a **virtual package**: no primitive is (re)implemented here. The
already-shipped primitives living in `Harborline.Api.Foundation` are re-exposed via
`[assembly: TypeForwardedTo]`, and the two primitives that are not yet
implemented ship as empty stub interfaces so downstream work has a stable
landing zone.

This package closes gap **G1** from the platform gap analysis
of 2026-04-18 (a pre-Harborline discovery artifact, no longer in the repository set).

## Why this package exists

Platform spec §2.3 names seven kernel primitives and places them at Layer 2
of the layered architecture. Five of those primitives already ship under
`Harborline.Api.Foundation.*` sub-namespaces. Two do not ship yet. Without this
package there is no single `packages/kernel/` entry point that corresponds
to the spec's Layer 2 — the kernel is "everywhere and nowhere" in Foundation.

The façade fixes the mismatch without moving any code, without breaking any
consumer, and without fabricating parallel types.

## The seven primitives — shipping status

| Spec § | Primitive            | Status            | Source                                                                          |
|--------|---------------------|-------------------|---------------------------------------------------------------------------------|
| §3.1   | Entity Store         | Forwarded         | `Harborline.Api.Foundation.Assets.Entities` (`IEntityStore`, `InMemoryEntityStore`, …) |
| §3.2   | Version Store        | Forwarded         | `Harborline.Api.Foundation.Assets.Versions` (`IVersionStore`, `InMemoryVersionStore`)  |
| §3.3   | Audit Log            | Forwarded         | `Harborline.Api.Foundation.Assets.Audit` (`IAuditLog`, `AuditRecord`, `HashChain`, …)  |
| §3.4   | Schema Registry      | **Stub** (gap G2) | `Harborline.Api.Kernel.Schema.ISchemaRegistry`                                          |
| §3.5   | Permission Evaluator | Forwarded         | `Harborline.Api.Foundation.PolicyEvaluator` (`IPermissionEvaluator`, `Decision`, …)    |
| §3.6   | Event Bus            | **Stub** (gap G3) | `Harborline.Api.Kernel.Events.IEventBus`                                                |
| §3.7   | Blob Store           | Forwarded         | `Harborline.Api.Foundation.Blobs` (`IBlobStore`, `Cid`, `FileSystemBlobStore`)         |

Supporting identity types used across primitives are also forwarded:
`EntityId`, `VersionId`, `Instant`, `ActorId`, `TenantId`, `SchemaId`,
`PrincipalId`, `Signature`, and `SignedOperation<T>`.

## How the forwarding works

Types are forwarded at their **shipped** fully-qualified names. A consumer
depending on `Harborline.Api.Kernel` who writes

```csharp
using Harborline.Api.Foundation.Assets.Entities;
IEntityStore store = new InMemoryEntityStore(/* … */);
```

picks the types up from the `Harborline.Api.Kernel` assembly via
`[TypeForwardedTo]`, because the CLR resolves forwarded types transparently.

**Why we did not rename the types into `Harborline.Api.Kernel.*`.** C#'s assembly
type-forwarding preserves a type's original namespace. Renaming would
require parallel empty interfaces (`Harborline.Api.Kernel.IEntityStore : Harborline.Api.Foundation.Assets.Entities.IEntityStore`) which would force every
registration point in the solution to bridge between two equivalent contracts
— the opposite of "no consumer churn". The shipping Foundation names already
match the spec's short names (`IEntityStore`, `IVersionStore`, `IAuditLog`,
`IPermissionEvaluator`, `IBlobStore`); the only thing that differs is the
sub-namespace, which is noise the spec can be relaxed about. If a future spec
revision wants literally `Harborline.Api.Kernel.IEntityStore` as the canonical name,
that is a separate (breaking) change and belongs in its own PR.

## Consumer usage

```bash
dotnet add package Harborline.Api.Kernel
```

```csharp
using Harborline.Api.Foundation.Assets.Entities;          // Entity Store
using Harborline.Api.Foundation.Assets.Versions;          // Version Store
using Harborline.Api.Foundation.Assets.Audit;             // Audit Log
using Harborline.Api.Foundation.PolicyEvaluator;          // Permission Evaluator
using Harborline.Api.Foundation.Blobs;                    // Blob Store
using Harborline.Api.Kernel.Schema;                       // §3.4 stub (G2)
using Harborline.Api.Kernel.Events;                       // §3.6 stub (G3)
```

A consumer may depend on `Harborline.Api.Kernel` *or* `Harborline.Api.Foundation` —
both resolve to the same primitive types. Packages that want to communicate
"we only touch the kernel surface" should take the `Harborline.Api.Kernel`
dependency; packages that need Foundation's supporting infrastructure
(Crypto, Capabilities, Macaroons, Notifications, …) should depend on
`Harborline.Api.Foundation` directly.

## Relationship to `packages/foundation/`

- `Harborline.Api.Foundation` is the **implementation** surface. It ships the
  primitives plus supporting infrastructure (Crypto beyond the identity
  types, Capabilities, Macaroons, Notifications, Services, Data, etc.).
- `Harborline.Api.Kernel` is the **contract** surface. It exposes only the
  spec §3 primitives — the seven Layer 2 capabilities.
- The two packages **coexist** indefinitely. A future consolidation could
  move primitive implementations into `Harborline.Api.Kernel` and demote
  Foundation to "supporting infrastructure only", but that is out of scope
  for G1 and would be a breaking change.

## What is deliberately NOT in this package

- `Harborline.Api.Foundation.Crypto` beyond `PrincipalId`, `Signature`, and
  `SignedOperation<T>` — Crypto is supporting infrastructure per spec §3;
  re-exporting it wholesale would blur the kernel/foundation boundary.
- `Harborline.Api.Foundation.Capabilities` and `Harborline.Api.Foundation.Macaroons` —
  capability tokens are Layer 3 (authorization plane), not Layer 2.
- Postgres store implementations — those live in `Harborline.Api.Foundation
  .Assets.Postgres`, a separate assembly. A consumer that wants Postgres
  stores should add that package directly; forwarding across an additional
  assembly would create a transitive dependency inversion that Layer 2
  should not introduce.

## Links

- Platform spec §2.3 (layering) and §3 (kernel primitives)
- Gap analysis: the 2026-04-18 discovery artifact, no longer in the repository set
  - **G1** — this package
  - **G2** — Schema Registry implementation (fills
    `Harborline.Api.Kernel.Schema.ISchemaRegistry`)
  - **G3** — Event Bus implementation (fills
    `Harborline.Api.Kernel.Events.IEventBus`)
