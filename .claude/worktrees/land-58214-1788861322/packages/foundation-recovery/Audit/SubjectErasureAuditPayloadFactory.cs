using System.Collections.Generic;
using System.Linq;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Foundation.Recovery.Audit;

/// <summary>
/// Builds the <see cref="AuditPayload"/> body for
/// <see cref="AuditEventType.SubjectErased"/> (ADR 0135 GDPR direction). The
/// payload deliberately carries NO erased plaintext — only the tenant, the
/// pseudonymous tombstone label, the approving actors, and the operator-supplied
/// legal basis. The original (cleartext) subject identifier is NOT recorded: the
/// whole point of the erasure is that it stops being resolvable.
/// </summary>
internal static class SubjectErasureAuditPayloadFactory
{
    /// <summary>Body for <see cref="AuditEventType.SubjectErased"/>.</summary>
    public static AuditPayload Erased(SubjectTombstone tombstone) =>
        new(new Dictionary<string, object?>
        {
            ["tenant"] = tombstone.TenantId.Value,
            ["pseudonym"] = tombstone.Pseudonym,
            ["erased_at"] = tombstone.ErasedAt,
            ["approving_actors"] = tombstone.ApprovingActors.Select(a => a.Value).ToArray(),
            ["legal_basis"] = tombstone.LegalBasis,
        });
}
