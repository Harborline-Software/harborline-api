using System;
using System.Security.Cryptography;
using System.Text;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// Derives the stable, non-reversible pseudonym that replaces a crypto-shredded
/// subject's identifying fields in its <see cref="SubjectTombstone"/>.
/// </summary>
/// <remarks>
/// <para>
/// The pseudonym is <c>HMAC-SHA256(key = tenant, message = "subject-tombstone-v1:" || subject)</c>,
/// hex-encoded — deterministic (same subject ⇒ same pseudonym, for idempotent
/// re-erasure + stable cross-reference) yet non-reversible (you cannot recover the
/// original subject id from the pseudonym without brute-forcing the id space). The
/// HMAC key is the tenant id, which keeps pseudonyms domain-separated per tenant.
/// This deliberately reuses NO erased plaintext — only the (still-pseudonymous in
/// GDPR terms) subject surrogate id, which is itself the unit of erasure.
/// </para>
/// <para>
/// <b>Threat model — what "non-reversible" does and does NOT mean.</b> The HMAC
/// key here is the tenant id, which is an <em>identifier, not a secret</em>. So
/// the pseudonym resists reversal <em>from the pseudonym alone</em> (you cannot
/// invert HMAC-SHA256 to recover the subject id), but it does NOT resist a
/// <em>confirmation oracle</em>: anyone who already knows (or can guess) a
/// candidate <c>(tenant, subject)</c> pair can recompute this value and confirm a
/// match. That is acceptable and intentional for the stated goal — an idempotent,
/// per-tenant domain-separated, non-reversible surrogate that carries no erased
/// plaintext — and the pseudonym is NOT advertised as unlinkable. If a future
/// requirement needs resistance to that confirmation oracle, key the HMAC with a
/// per-tenant <em>secret</em> (not the tenant id) so recomputation requires the
/// key; do not assume the current construction provides it.
/// </para>
/// </remarks>
public static class SubjectPseudonym
{
    private const string DomainPrefix = "subject-tombstone-v1:";

    /// <summary>Compute the tombstone pseudonym for a (tenant, subject) pair.</summary>
    public static string Derive(TenantId tenant, SubjectId subject)
    {
        var key = Encoding.UTF8.GetBytes(tenant.Value);
        var message = Encoding.UTF8.GetBytes(DomainPrefix + subject.Value);
        var mac = HMACSHA256.HashData(key, message);
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}
