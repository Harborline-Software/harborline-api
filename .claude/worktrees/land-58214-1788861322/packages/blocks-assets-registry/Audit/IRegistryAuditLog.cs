using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Audit;

/// <summary>
/// The append-only, hash-chained durable mutation journal every registry mutation rides (ADR 0101
/// Rev 3.1 Wave 1 constraint). Wave 1 ships an in-memory implementation
/// (<see cref="InMemoryRegistryAuditLog"/>); a host wires this seam to the foundation
/// <c>Harborline.Api.Foundation.Assets.Audit.IAuditLog</c> / kernel-audit substrate in production, the
/// same way the concrete Asset domain's lifecycle-event store is host-swapped.
/// </summary>
public interface IRegistryAuditLog
{
    /// <summary>
    /// Appends one mutation record to the <c>(tenant, subject)</c> chain and returns it (with its
    /// assigned sequence + hash). The system / default tenant sentinel is rejected fail-closed.
    /// </summary>
    RegistryAuditEvent Append(
        TenantId tenant,
        string subject,
        RegistryOp op,
        Instant at,
        string? actorRef = null,
        string? detail = null);

    /// <summary>All events for a subject on a tenant, in append order.</summary>
    IReadOnlyList<RegistryAuditEvent> ForSubject(TenantId tenant, string subject);

    /// <summary>Every event for a tenant, in append order.</summary>
    IReadOnlyList<RegistryAuditEvent> ForTenant(TenantId tenant);

    /// <summary>
    /// Walks a subject's chain and returns <c>true</c> iff every hash + previous-link lines up
    /// (tamper-evidence).
    /// </summary>
    bool VerifyChain(TenantId tenant, string subject);
}
