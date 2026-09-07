using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.SecurityPolicy.Models;
using Harborline.Api.Foundation.SecurityPolicy.Retention;
using Harborline.Api.Foundation.Governance.Resolution;

namespace Harborline.Api.Foundation.Governance.Enforcement;

/// <summary>
/// The four SPINE-2 PEPs (Policy Enforcement Points, ADR 0140 D2 §4). Each is a thin
/// orchestrator that, given a field's resolved policy, fires the bound BUILT primitives in a
/// fixed, fail-closed order — it never re-implements them. The ordering is itself the spec
/// (encrypt-before-persist; gate-hold-before-shred); the test plan pins each ordering with
/// mocked primitives.
/// </summary>
public interface IFieldPolicyEnforcer
{
    /// <summary>
    /// The Store PEP (write path, inside the ADR 0126 atomic txn). Order: residency →
    /// encrypt → retention-clock → audit. Any failure throws BEFORE the caller persists, so
    /// the atomic txn rolls back with no partial persist.
    /// </summary>
    Task<FieldStoreOutcome> StoreAsync(StoreFieldContext ctx, CancellationToken ct = default);

    /// <summary>
    /// The Read PEP (render path). Order: access → consent → redact/mask → audit-sensitive-read.
    /// </summary>
    Task<FieldReadProjection> ProjectForReadAsync(ReadFieldContext ctx, CancellationToken ct = default);

    /// <summary>
    /// The Export PEP (export/report path) — as Read, plus mandatory audit of classified
    /// data and a residency egress check.
    /// </summary>
    Task<FieldReadProjection> ProjectForExportAsync(ExportFieldContext ctx, CancellationToken ct = default);

    /// <summary>
    /// The EraseSubject PEP (crypto-shred path). Order: legal-hold / retention-floor gate →
    /// shred (the BUILT erasure service: ≥2-approver + dwell + key-destroy + tombstone +
    /// audit + propagate). A propagator fault propagates (fail-loud).
    /// </summary>
    Task<SubjectErasureOutcomeReport> EraseSubjectAsync(EraseSubjectContext ctx, CancellationToken ct = default);
}

/// <summary>Context for the Store PEP.</summary>
/// <param name="Policy">The field's resolved policy.</param>
/// <param name="Value">The field plaintext bytes to store (opaque).</param>
/// <param name="Tenant">The active tenant.</param>
/// <param name="Actor">The writing actor (for the audit envelope).</param>
/// <param name="Entity">The entity the field belongs to (for the audit envelope).</param>
/// <param name="RecordCreatedAt">The record's creation instant (the retention clock origin).</param>
/// <param name="TargetJurisdiction">Where the store would persist (the residency target).</param>
/// <param name="Subject">The data subject, required when the encrypt effect is subject-scoped.</param>
public sealed record StoreFieldContext(
    ResolvedFieldPolicy Policy,
    ReadOnlyMemory<byte> Value,
    TenantId Tenant,
    ActorId Actor,
    EntityId Entity,
    DateTimeOffset RecordCreatedAt,
    string TargetJurisdiction,
    SubjectId? Subject = null);

/// <summary>Context for the Read PEP.</summary>
/// <param name="Policy">The field's resolved policy.</param>
/// <param name="Value">The stored value as a display string (already decrypted by the caller).</param>
/// <param name="Tenant">The active tenant.</param>
/// <param name="Actor">The reading actor.</param>
/// <param name="Entity">The entity the field belongs to.</param>
/// <param name="ActorRoles">The reading actor's effective roles (capability scope ∩ assignment).</param>
/// <param name="Subject">The data subject, for a consent check.</param>
public sealed record ReadFieldContext(
    ResolvedFieldPolicy Policy,
    string? Value,
    TenantId Tenant,
    ActorId Actor,
    EntityId Entity,
    IReadOnlyCollection<string> ActorRoles,
    SubjectId? Subject = null);

/// <summary>Context for the Export PEP.</summary>
/// <param name="Policy">The field's resolved policy.</param>
/// <param name="Value">The stored value as a display string.</param>
/// <param name="Tenant">The active tenant.</param>
/// <param name="Actor">The exporting actor.</param>
/// <param name="Entity">The entity the field belongs to.</param>
/// <param name="ActorRoles">The exporting actor's effective roles.</param>
/// <param name="DestinationJurisdiction">Where the export would send the value (egress target).</param>
/// <param name="Subject">The data subject, for a consent check.</param>
public sealed record ExportFieldContext(
    ResolvedFieldPolicy Policy,
    string? Value,
    TenantId Tenant,
    ActorId Actor,
    EntityId Entity,
    IReadOnlyCollection<string> ActorRoles,
    string DestinationJurisdiction,
    SubjectId? Subject = null);

/// <summary>Context for the EraseSubject PEP.</summary>
/// <param name="Tenant">The tenant the subject belongs to.</param>
/// <param name="Subject">The data subject to crypto-shred.</param>
/// <param name="Request">The erasure request (approval chain + dwell + legal basis).</param>
/// <param name="AsOf">The instant the gate evaluates against.</param>
/// <param name="RetainedRecords">The subject's retained records — each resolved against the
/// tenant retention resolver; any unexpired floor blocks the shred.</param>
public sealed record EraseSubjectContext(
    TenantId Tenant,
    SubjectId Subject,
    SubjectErasureRequest Request,
    DateTimeOffset AsOf,
    IReadOnlyList<RetainedRecord>? RetainedRecords = null);

/// <summary>A retained record contributing to the subject's retention-floor gate.</summary>
/// <param name="Class">The record's audit-event class (the retention resolver's axis).</param>
/// <param name="CreatedAt">When the record was created (the retention-clock origin).</param>
public sealed record RetainedRecord(AuditEventClass Class, DateTimeOffset CreatedAt);

/// <summary>The outcome of a Store PEP run.</summary>
/// <param name="Encrypted">True when the field was encrypted at rest.</param>
/// <param name="Cipher">The envelope, when <see cref="Encrypted"/>; else null. The caller persists this (or the cleartext) inside the txn.</param>
/// <param name="Retention">The retention verdict (clock), when a Retain effect fired; else null.</param>
/// <param name="Audited">True when a store audit record was appended.</param>
/// <param name="AppliedEffects">The effects applied, in order (observability / tests).</param>
public sealed record FieldStoreOutcome(
    bool Encrypted,
    EncryptedField? Cipher,
    RetentionVerdict? Retention,
    bool Audited,
    IReadOnlyList<string> AppliedEffects);

/// <summary>The projected outcome of a Read / Export PEP run.</summary>
/// <param name="Readable">True when a value (clear or masked) is returned to the caller.</param>
/// <param name="Redacted">True when the value was omitted entirely.</param>
/// <param name="Masked">True when the value was partially revealed.</param>
/// <param name="Value">The projected value (null when redacted / unauthorized; masked string when masked).</param>
/// <param name="Audited">True when a read/export audit record was appended.</param>
public sealed record FieldReadProjection(
    bool Readable,
    bool Redacted,
    bool Masked,
    string? Value,
    bool Audited);

/// <summary>The outcome of an EraseSubject PEP run.</summary>
/// <param name="Erased">True when the subject was crypto-shredded by this call.</param>
/// <param name="BlockedByHold">True when an active legal hold / retention floor blocked the shred.</param>
/// <param name="BlockReason">The reason, when blocked; else null.</param>
/// <param name="ServiceOutcome">The underlying erasure-service outcome, when the shred ran.</param>
public sealed record SubjectErasureOutcomeReport(
    bool Erased,
    bool BlockedByHold,
    string? BlockReason,
    SubjectErasureOutcome? ServiceOutcome);
