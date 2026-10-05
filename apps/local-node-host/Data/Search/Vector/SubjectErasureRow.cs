namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The durable EF projection of a crypto-shred entry in the subject-erasure registry (ADR 0135 GDPR direction;
/// the #1378 M-1 durable-erasure-store fix). One write-once row per erased <c>(tenant, subject)</c> — its mere
/// PRESENCE is the tombstone the per-subject key-resolution path consults (<c>IsErasedAsync</c>) and fails closed
/// on, so an erased subject cannot receive a replacement key after restart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this must be durable (the M-1 resurrection hole this closes).</b> The registry is a compliance record:
/// the stored provider deletes the subject key, while the registry prevents replacement-key creation. If the
/// registry is the restart-volatile in-memory default, a restart forgets the erasure and permits new writes for
/// that identity. Co-locating the registry in the SAME SQLCipher <c>local-node.db</c> file as the
/// per-subject-encrypted index (which it gates) makes the shred survive restart, and the
/// <c>RequireDurableErasureStores()</c> gate at the composition root makes the volatile default a hard startup
/// failure rather than a silent hole.
/// </para>
/// <para>
/// <b>Append-only.</b> Grow-only — there is no removal, mirroring the one-directional crypto-shred semantics
/// (<see cref="Harborline.Api.Foundation.Recovery.Erasure.ISubjectErasureRegistry"/>). The composite
/// <c>(TenantId, SubjectId)</c> is the cross-tenant isolation + idempotency key.
/// </para>
/// </remarks>
public sealed class SubjectErasureRow
{
    /// <summary>The tenant the erased subject belongs to (PK with <see cref="SubjectId"/>; isolation boundary).</summary>
    public required string TenantId { get; set; }

    /// <summary>The crypto-shredded data subject (PK with <see cref="TenantId"/>).</summary>
    public required string SubjectId { get; set; }

    /// <summary>When the erasure was recorded, Unix-ms UTC (compliance metadata; not consulted by the gate).</summary>
    public required long ErasedAtUnixMs { get; set; }

    // T-1048 approval evidence: what the recovery pass needs to finish an interrupted erasure and write its
    // SubjectErased audit without the request. PERSONAL DATA (the approver ids). Read only by
    // NodeEfSubjectErasureRegistry's recovery path, never by a route, query surface or projection, and cleared in
    // the commit that stages the audit in the outbox. Null on a completed row and on a row marked before T-1048.

    /// <summary>The approver principal ids, a JSON string array; the audit's <c>approving_actors</c>.</summary>
    public string? ApprovingActorsJson { get; set; }

    /// <summary>The legal-basis code; the audit's <c>legal_basis</c>.</summary>
    public string? LegalBasis { get; set; }

    /// <summary>The instant the approved erasure took effect, Unix-ms UTC; the tombstone's and the audit's instant.</summary>
    public long? ApprovedAtUnixMs { get; set; }

    /// <summary>When the audit was secured and the evidence cleared, Unix-ms UTC; null while the erasure is owed.</summary>
    public long? CompletedAtUnixMs { get; set; }

    /// <summary>Failed recovery attempts, for the backoff.</summary>
    public int RecoveryAttempts { get; set; }

    /// <summary>The earliest instant the next recovery attempt is due, Unix-ms UTC; null when due now.</summary>
    public long? NextRecoveryAtUnixMs { get; set; }
}
