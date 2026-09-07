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
}
