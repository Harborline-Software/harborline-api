namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// Node-local-authoritative team-comms message — the durable read-model row for one appended message (the
/// first messaging doctype on the live node-host path). It is the queryable projection of a
/// <see cref="MessageCrdtState"/> the CRDT comms list converges; the GET route reads these rows in authored
/// order. Immutable + append-only: there is no update or delete in this pilot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Node-exclusive, NOT a shared <c>IHarborlineEntityModule</c>.</b> Comms is a brand-new node-only doctype
/// with no Bridge EF persistence, so — exactly like <c>PropertyRecord</c> / the bank-feed + payroll
/// node-local stores — it is mapped by its own <see cref="NodeLocalCommsDbContext"/>, a separate
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> from <c>LocalNodeDbContext</c>. Because it never
/// enters <c>LocalNodeDbContext</c>'s injected module set, the council C2 both-provider model-drift
/// arch-test does not see it (it has no Postgres/Bridge counterpart to drift from).
/// </para>
/// <para>
/// <b>Encrypted at rest + SC4-recoverable (SC-1 / SC4-C2).</b> The row lives in the SAME
/// SQLCipher-encrypted <c>local-node.db</c> file as the financial store, keyed through the same
/// connection interceptor (root-seed-derived DEK or the SC-4 injected Store DEK). It is therefore the
/// recoverable relational store — the comms CRDT's ONLY durable sink — never the seed-keyed per-team KV
/// store. There is no plaintext path.
/// </para>
/// <para>
/// <b>Per-author attribution.</b> <see cref="AuthorPartyId"/> is the appending member's id (ADR 0032), and
/// <see cref="AuthorIssuerId"/> + <see cref="SignatureB64Url"/> bind the row to the author's signed
/// identity. Two distinct authors are distinguishable on the log.
/// </para>
/// </remarks>
public sealed class NodeMessage
{
    /// <summary>Stable per-message id (a Guid string) and primary key — the append's identity across replicas.</summary>
    public required string Id { get; set; }

    /// <summary>The active-team-derived data tenant (ADR 0032) this message belongs to.</summary>
    public required string TenantId { get; set; }

    /// <summary>
    /// The conversation (thread) within the team this message belongs to (C1). The well-known team channel is
    /// <see cref="CommsConversation.TeamConversationId"/> (<c>"team"</c>); a 1:1 DM is a <c>dm:</c>-prefixed id
    /// (C2+). The comms read path filters by <c>(TenantId, ConversationId)</c> so each conversation is its own
    /// append-log. The additive migration back-fills pre-C1 rows to <c>"team"</c>.
    /// </summary>
    public required string ConversationId { get; set; }

    /// <summary>The appending member's id — the ADR 0032 active member's ActorId/PartyId (NOT a constant).</summary>
    public required string AuthorPartyId { get; set; }

    /// <summary>The author's signing identity — base64url of the signer's Ed25519 public key (verifier trust anchor).</summary>
    public required string AuthorIssuerId { get; set; }

    /// <summary>The message text.</summary>
    public required string Body { get; set; }

    /// <summary>The signing nonce (string form) — part of the signed envelope, replay-defence at higher layers.</summary>
    public required string NonceGuid { get; set; }

    /// <summary>base64url Ed25519 signature over the canonical signable form of this message.</summary>
    public required string SignatureB64Url { get; set; }

    /// <summary>Authoring instant (UTC). The chronological order the GET route returns.</summary>
    public DateTimeOffset AuthoredAtUtc { get; set; }

    /// <summary>Project a converged CRDT snapshot into its durable read-model row.</summary>
    public static NodeMessage FromCrdtState(MessageCrdtState m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new NodeMessage
        {
            Id = m.MessageId,
            TenantId = m.TenantId,
            // Defensive: a pre-C1 wire record (an old peer) deserializes with a null ConversationId — back-fill
            // it to the team channel so it never violates the not-null column. (A post-C1 message always carries
            // a non-empty ConversationId from CommsMessageFactory.)
            ConversationId = CommsConversation.Normalize(m.ConversationId),
            AuthorPartyId = m.AuthorPartyId,
            AuthorIssuerId = m.AuthorIssuerId,
            Body = m.Body,
            NonceGuid = m.NonceGuid,
            SignatureB64Url = m.SignatureB64Url,
            AuthoredAtUtc = ParseInstant(m.AuthoredAtIso),
        };
    }

    /// <summary>Project a durable row back into its CRDT snapshot (hydration path).</summary>
    public static MessageCrdtState ToCrdtState(NodeMessage row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new MessageCrdtState(
            MessageId: row.Id,
            TenantId: row.TenantId,
            ConversationId: row.ConversationId,
            AuthorPartyId: row.AuthorPartyId,
            AuthorIssuerId: row.AuthorIssuerId,
            AuthoredAtIso: row.AuthoredAtUtc.ToString("O"),
            Body: row.Body,
            NonceGuid: row.NonceGuid,
            SignatureB64Url: row.SignatureB64Url);
    }

    private static DateTimeOffset ParseInstant(string iso) =>
        DateTimeOffset.TryParse(iso, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dto)
            ? dto
            : DateTimeOffset.UtcNow;
}
