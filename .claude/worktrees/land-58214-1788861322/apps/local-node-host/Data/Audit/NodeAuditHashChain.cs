using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// Pure, offline, <b>key-independent</b> SHA-256 content-hash chain over <see cref="NodeAuditEventRow"/>
/// records — the node-row analogue of the foundation <c>HashChain</c> (ADR 0126 §D4). Verifying the
/// chain needs no network and no key material, so the integrity verdict survives a passphrase reseed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a node-row helper, not the foundation <c>HashChain</c> directly.</b> The foundation
/// <c>HashChain</c> operates on the foundation <c>AuditRecord</c> (which carries <c>EntityId</c> /
/// <c>Op</c> / a long <c>AuditId</c>) — a different shape than the wire-mirroring
/// <see cref="NodeAuditEventRow"/> ADR 0126 §D1 specifies. This helper applies the SAME hashing
/// discipline (prev-hash-prefixed, canonical-JSON payload, ordinal SHA-256, hex-lowercase output) to
/// the node row's stable fields, so the chain is offline-verifiable with identical guarantees.
/// </para>
/// <para>
/// Hash input = <c>prevHash | auditId | eventType | actor | tenant | occurredAt(O) | canonical(payload)</c>.
/// </para>
/// </remarks>
public static class NodeAuditHashChain
{
    /// <summary>
    /// Computes the content hash for one node audit row given its predecessor's hash. The
    /// <paramref name="payloadJson"/> is re-canonicalised (key-sorted, whitespace-free) so the hash is
    /// stable irrespective of the stored JSON's incidental formatting.
    /// </summary>
    public static string ComputeHash(
        string? prevHash,
        string auditId,
        string eventType,
        string? actor,
        string tenantId,
        DateTimeOffset occurredAt,
        string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(auditId);
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(payloadJson);

        var prefix = prevHash ?? string.Empty;
        var payloadCanonical = CanonicalisePayload(payloadJson);
        var input =
            $"{prefix}|{auditId}|{eventType}|{actor ?? string.Empty}|{tenantId}|{occurredAt:O}|{payloadCanonical}";
        var bytes = Encoding.UTF8.GetBytes(input);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Verifies a sequence of node audit rows ordered by append sequence ascending. <c>OccurredAt</c> is
    /// business time and is deliberately not a chain-order key.
    /// rows against their hash chain. Returns <c>true</c> if every row's <c>PrevHash</c> link + <c>Hash</c>
    /// line up; otherwise <c>false</c>.
    /// </summary>
    public static bool Verify(IReadOnlyList<NodeAuditEventRow> orderedAscending)
    {
        ArgumentNullException.ThrowIfNull(orderedAscending);
        string? previousHash = null;
        foreach (var row in orderedAscending)
        {
            if (!string.Equals(row.PrevHash ?? string.Empty, previousHash ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }
            var expected = ComputeHash(
                previousHash,
                row.AuditId,
                row.EventType,
                row.Actor,
                row.TenantId,
                row.OccurredAt,
                row.Payload);
            if (!string.Equals(expected, row.Hash, StringComparison.Ordinal))
            {
                return false;
            }
            previousHash = row.Hash;
        }
        return true;
    }

    /// <summary>
    /// Recomputes a single row's expected hash from its own fields + the supplied predecessor hash.
    /// The read-time integrity check: the row's stored <see cref="NodeAuditEventRow.Hash"/> must equal
    /// this for the record to read <c>Verified</c> (chain-intact).
    /// </summary>
    public static bool VerifyRow(NodeAuditEventRow row, string? prevHash)
    {
        ArgumentNullException.ThrowIfNull(row);
        var expected = ComputeHash(
            prevHash, row.AuditId, row.EventType, row.Actor, row.TenantId, row.OccurredAt, row.Payload);
        return string.Equals(expected, row.Hash, StringComparison.Ordinal);
    }

    private static string CanonicalisePayload(string payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            return string.Empty;
        }
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return JsonCanonicalizer.ToCanonicalString(doc);
        }
        catch (JsonException)
        {
            // Non-JSON payloads (shouldn't happen — writers always store canonical JSON) hash as
            // their literal text so the chain is still deterministic.
            return payloadJson;
        }
    }
}
