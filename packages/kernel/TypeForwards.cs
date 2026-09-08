// Harborline.Api.Kernel — Type Forwards
//
// This file is the heart of the G1 "virtual facade" package. Each
// [TypeForwardedTo] entry publishes a Foundation type through the
// Harborline.Api.Kernel assembly under its ORIGINAL fully-qualified name
// (Harborline.Api.Foundation.*), so consumers that take a dependency on
// Harborline.Api.Kernel pick up the exact same primitive contracts as
// consumers depending on Harborline.Api.Foundation directly.
//
// Naming note: platform spec §3 uses the short names `IEntityStore`,
// `IVersionStore`, `IAuditLog`, `IPermissionEvaluator`, `IBlobStore`,
// etc. The shipped Foundation types use the same short names under
// sub-namespaces (`Harborline.Api.Foundation.Assets.Entities.IEntityStore`
// and so on). We forward types at their SHIPPED namespaces rather
// than fabricate synonyms in `Harborline.Api.Kernel.*`; see README for the
// rationale.
//
// All seven primitives from §3 now have real contracts either forwarded here or
// shipped in a sibling `Harborline.Api.Kernel.*` package. Primitives promoted out of this
// facade into sibling packages (the G1-reserved stubs were deleted as each shipped):
//   • §3.4 Schema Registry — shipped via Harborline.Api.Kernel.SchemaRegistry (gap G2).
//     Lives at Harborline.Api.Kernel.Schema.ISchemaRegistry in the sibling assembly.
//   • §3.6 Event Bus — shipped via Harborline.Api.Kernel.EventBus (gap G3).
//     Lives at Harborline.Api.Kernel.Events.IEventBus in the sibling assembly.
// Consumers that want a sibling primitive take its package reference in addition
// to Harborline.Api.Kernel.

using System.Runtime.CompilerServices;

// -----------------------------------------------------------------------------
// §3.1 Entity Store
// -----------------------------------------------------------------------------
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.IEntityStore))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.InMemoryEntityStore))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.Entity))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.EntityQuery))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.CreateOptions))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.UpdateOptions))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.DeleteOptions))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.VersionSelector))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.IEntityValidator))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.NullEntityValidator))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.EntityValidationException))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.IdempotencyConflictException))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Entities.ConcurrencyException))]

// -----------------------------------------------------------------------------
// §3.2 Version Store
// -----------------------------------------------------------------------------
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Versions.IVersionStore))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Versions.InMemoryVersionStore))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Versions.Version))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Versions.IVersionObserver))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Versions.NullVersionObserver))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Versions.BranchOptions))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Versions.MergeOptions))]

// -----------------------------------------------------------------------------
// §3.3 Audit Log
// -----------------------------------------------------------------------------
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Audit.IAuditLog))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Audit.InMemoryAuditLog))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Audit.AuditRecord))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Audit.AuditAppend))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Audit.AuditQuery))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Audit.AuditId))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Audit.HashChain))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Audit.IAuditContextProvider))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Audit.NullAuditContextProvider))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Audit.Op))]

// -----------------------------------------------------------------------------
// §3.3 Supporting: Entity Hierarchy
// Treated as part of the entity-store surface per spec §3.1; re-exported for
// parity with consumers that compose hierarchy on top of entities.
// -----------------------------------------------------------------------------
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyService))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Hierarchy.InMemoryHierarchyService))]

// -----------------------------------------------------------------------------
// §3.5 Permission Evaluator
// -----------------------------------------------------------------------------
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.PolicyEvaluator.IPermissionEvaluator))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.PolicyEvaluator.ReBACPolicyEvaluator))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.PolicyEvaluator.Decision))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.PolicyEvaluator.DecisionKind))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.PolicyEvaluator.Subject))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.PolicyEvaluator.ActionType))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.PolicyEvaluator.PolicyResource))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.PolicyEvaluator.ContextEnvelope))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.PolicyEvaluator.Obligation))]

// -----------------------------------------------------------------------------
// §3.7 Blob Store
// -----------------------------------------------------------------------------
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Blobs.IBlobStore))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Blobs.FileSystemBlobStore))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Blobs.Cid))]

// -----------------------------------------------------------------------------
// Common identity types — referenced across multiple primitives. Re-exported
// per gap G1 scope ("common identity types used across primitives"). The
// broader Crypto / Capabilities / Macaroons surfaces stay in Foundation; this
// re-export is intentionally minimal.
// -----------------------------------------------------------------------------
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Common.EntityId))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Common.VersionId))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Common.Instant))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Common.ActorId))]
[assembly: TypeForwardedTo(typeof(Harborline.Foundation.Assets.Common.TenantId))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Assets.Common.SchemaId))]

[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Crypto.PrincipalId))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Crypto.Signature))]
[assembly: TypeForwardedTo(typeof(Harborline.Api.Foundation.Crypto.SignedOperation<>))]
