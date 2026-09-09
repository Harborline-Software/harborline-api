using Harborline.Api.Foundation.IdentityAtlas;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>
/// Node-local-authoritative TRUST-ROSTER record — the durable row for one synced admission/revocation (the
/// roster-sync doctype; production-wiring gap #1). It is the queryable projection of a
/// <see cref="RosterRecordCrdtState"/> the CRDT roster list converges; on cold start the records re-hydrate
/// into the CRDT list so they replicate to a fresh peer after a restart. Append-only: a record is immutable
/// (a revocation is a NEW record, never an edit) — the same shape as the comms log + the GL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Node-exclusive, NOT a shared <c>IHarborlineEntityModule</c>.</b> Like <see cref="NodeMessage"/> (comms),
/// roster records are a node-only doctype with no Bridge EF persistence, mapped by their own
/// <see cref="NodeLocalRosterDbContext"/> (a separate <see cref="Microsoft.EntityFrameworkCore.DbContext"/>
/// from <c>LocalNodeDbContext</c>), so the council C2 both-provider parity arch-test does not see them.
/// </para>
/// <para>
/// <b>Encrypted at rest + SC4-recoverable (SC-1 / SC4-C2).</b> The row lives in the SAME SQLCipher-encrypted
/// <c>local-node.db</c> file, keyed through the same connection interceptor. It IS the recoverable relational
/// store — the roster CRDT's ONLY durable sink — never the seed-keyed per-team KV store. The roster's TRUST
/// is in the per-record signature (re-validated on rebuild), not in the storage: an attacker who tampered
/// with a row at rest would only produce a record that fails signature validation and is dropped.
/// </para>
/// </remarks>
public sealed class NodeRosterRecord
{
    /// <summary>Stable per-record id (kind + party + nonce) and primary key — the append's identity across replicas.</summary>
    public required string Id { get; set; }

    /// <summary>0 = admission, 1 = revocation (<see cref="RosterRecordKind"/>).</summary>
    public int Kind { get; set; }

    /// <summary>The team this record is into/within (string form of the Guid).</summary>
    public required string TeamId { get; set; }

    /// <summary>The admitted (admission) or revoked (revocation) party id.</summary>
    public required string PartyId { get; set; }

    /// <summary>base64url of the admitted member's public key (admission only; empty for revocation).</summary>
    public required string PublicKeyB64Url { get; set; }

    /// <summary>
    /// base64url of the admitted member's TEAM-SCOPED transport public key — the sync-HELLO key
    /// <c>MemberSetTrustPolicy</c> checks (INFO-2; the ≥3-node mesh follow-on). Persisted so cold-start hydration
    /// preserves the carried key (else the rebuilt transport-trust set would be empty after a restart until a peer
    /// re-emitted). Additive + back-compat: nullable, defaults empty for legacy rows + revocation records. UNSIGNED
    /// by-association (the trust anchor is the signed principal binding; this is routing/handshake material honored
    /// only for a chain-validated member).
    /// </summary>
    public string TransportPublicKeyB64Url { get; set; } = string.Empty;

    /// <summary>
    /// base64url of the admitted member's TEAM-SCOPED DM-encryption public key — the X25519 PUBLIC half the C5
    /// roster-bound DM key resolver runs the ECDH against (C5; DM content confidentiality). Persisted so cold-start
    /// hydration preserves the carried DM key (else a participant could not derive a DM peer's key after a restart
    /// until that peer's record re-emitted). Same additive + back-compat + UNSIGNED-by-association posture as
    /// <see cref="TransportPublicKeyB64Url"/>: nullable, defaults empty for legacy rows + revocation records. The
    /// PRIVATE half is never persisted here (it is seed-derived on the owning node only).
    /// </summary>
    public string DmPublicKeyB64Url { get; set; } = string.Empty;

    /// <summary>
    /// base64url of the admitted member's TEAM-SCOPED <b>X-Wing</b> (X25519 + ML-KEM-768) PUBLIC key — the
    /// 1216-byte <c>pk_M ‖ pk_X</c> a sender encapsulates a suite-#3 tenant-DEK / role-key wrap to (PQC Phase 2 /
    /// BL-01 increment 2c-iii-b; the suite-#3 write-side enabler). Persisted so cold-start hydration preserves the
    /// carried X-Wing key (else a sender could not tell a peer is X-Wing-capable after a restart until that peer's
    /// record re-emitted). Same additive + back-compat + UNSIGNED-by-association posture as
    /// <see cref="DmPublicKeyB64Url"/> / <see cref="TransportPublicKeyB64Url"/>: nullable, defaults empty for
    /// legacy rows + revocation records (and any party that has not published an X-Wing key → stays suite #1). The
    /// PRIVATE half (the 32-byte X-Wing seed) is never persisted here (it is root-seed-derived on the owning node
    /// only). Carried as a RAW base64url (the 1216-byte key is not <c>PrincipalId</c>-shaped).
    /// </summary>
    public string XWingPublicKeyB64Url { get; set; } = string.Empty;

    /// <summary>
    /// #3167 R1.2 — the single-use pairing/admission token id the admission was signed FROM, persisted so cold-start
    /// hydration preserves it (else a pairing admission's SIGNED provenance would reconstruct empty after a restart
    /// → its signature would fail on rebuild → the record would be dropped). Unlike the transport/DM/X-Wing keys this
    /// is part of the SIGNED admission envelope (R1.2), so it MUST survive persistence intact. Additive + back-compat:
    /// defaults empty for legacy rows, non-pairing admissions, and revocation records.
    /// </summary>
    public string AdmittedViaTokenId { get; set; } = string.Empty;

    /// <summary>
    /// #3167 R1.2 — the opaque mint-time SessionCorrelationId the admission was signed under (audit provenance),
    /// persisted for the same cold-start-hydration reason as <see cref="AdmittedViaTokenId"/> (it is signed into the
    /// admission envelope and must survive persistence intact). Additive + back-compat, defaults empty.
    /// </summary>
    public string MintingSessionEvidence { get; set; } = string.Empty;

    /// <summary>Legacy carried permission field. Retained for storage compatibility and ignored on read.</summary>
    public required string PermissionsJson { get; set; }

    /// <summary>base64url of the admitter/revoker public key (the signer).</summary>
    public required string AdmittedByPublicKey { get; set; }

    /// <summary>The admitter/revoker party id.</summary>
    public required string AdmittedByPartyId { get; set; }

    /// <summary>The signing nonce (string form).</summary>
    public required string NonceGuid { get; set; }

    /// <summary>base64url Ed25519 signature over the canonical signable form.</summary>
    public required string SignatureB64Url { get; set; }

    /// <summary>True iff this is the founding self-admission (admission only).</summary>
    public bool IsGenesis { get; set; }

    /// <summary>Issuance instant (UTC). The chronological order of records.</summary>
    public DateTimeOffset IssuedAtUtc { get; set; }

    /// <summary>Attested first receipt, retained across restart and replicated verbatim.</summary>
    public DateTimeOffset? ReceivedAtUtc { get; set; }

    public int WireFormatVersion { get; set; }
    public string ReceivedByPartyId { get; set; } = string.Empty;
    public string ReceivedByPublicKey { get; set; } = string.Empty;
    public string ReceiveAttestationSignatureB64Url { get; set; } = string.Empty;

    /// <summary>The existing sync HELLO clock-skew allowance bounds retrospective ordering.</summary>
    public static readonly TimeSpan ReceiveTimeWindow = TimeSpan.FromSeconds(
        Harborline.Api.Kernel.Sync.Handshake.HandshakeProtocol.HelloTimestampSkewSeconds);

    /// <summary>Legacy rows retain signed order until the receive-attestation wire cutover.</summary>
    public static DateTimeOffset BoundedOrderTime(DateTimeOffset issued, DateTimeOffset? received) =>
        received is { } at && issued < at - ReceiveTimeWindow ? at : issued;

    internal static Func<string, DateTimeOffset, DateTimeOffset> OrderTimes(IEnumerable<NodeRosterRecord> rows)
    {
        var receipts = rows.GroupBy(r => r.SignatureB64Url, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First() is { WireFormatVersion: RosterWireFormat.CurrentVersion } row
                ? row.ReceivedAtUtc : null, StringComparer.Ordinal);
        return (signature, issued) => BoundedOrderTime(issued, receipts.GetValueOrDefault(signature));
    }

    /// <summary>Project a converged CRDT snapshot into its durable read-model row.</summary>
    public static NodeRosterRecord FromCrdtState(RosterRecordCrdtState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new NodeRosterRecord
        {
            Id = s.RecordId,
            Kind = (int)s.Kind,
            TeamId = s.TeamId,
            PartyId = s.PartyId,
            PublicKeyB64Url = s.PublicKeyB64Url,
            TransportPublicKeyB64Url = s.TransportPublicKeyB64Url ?? string.Empty,
            DmPublicKeyB64Url = s.DmPublicKeyB64Url ?? string.Empty,
            XWingPublicKeyB64Url = s.XWingPublicKeyB64Url ?? string.Empty,
            AdmittedViaTokenId = s.AdmittedViaTokenId ?? string.Empty,
            MintingSessionEvidence = s.MintingSessionEvidence ?? string.Empty,
            PermissionsJson = string.Empty,
            AdmittedByPublicKey = s.AdmittedByPublicKey,
            AdmittedByPartyId = s.AdmittedByPartyId,
            NonceGuid = s.NonceGuid,
            SignatureB64Url = s.SignatureB64Url,
            IsGenesis = s.IsGenesis,
            IssuedAtUtc = ParseInstant(s.IssuedAtIso),
            ReceivedAtUtc = ParseOptionalInstant(s.ReceivedAtIso),
            WireFormatVersion = s.WireFormatVersion,
            ReceivedByPartyId = s.ReceivedByPartyId ?? string.Empty,
            ReceivedByPublicKey = s.ReceivedByPublicKey ?? string.Empty,
            ReceiveAttestationSignatureB64Url = s.ReceiveAttestationSignatureB64Url ?? string.Empty,
        };
    }

    /// <summary>Project a durable row back into its CRDT snapshot (hydration path).</summary>
    public static RosterRecordCrdtState ToCrdtState(NodeRosterRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new RosterRecordCrdtState(
            RecordId: row.Id,
            Kind: (RosterRecordKind)row.Kind,
            TeamId: row.TeamId,
            PartyId: row.PartyId,
            PublicKeyB64Url: row.PublicKeyB64Url,
            AdmittedByPublicKey: row.AdmittedByPublicKey,
            AdmittedByPartyId: row.AdmittedByPartyId,
            IssuedAtIso: row.IssuedAtUtc.ToString("O"),
            NonceGuid: row.NonceGuid,
            SignatureB64Url: row.SignatureB64Url,
            IsGenesis: row.IsGenesis,
            TransportPublicKeyB64Url: row.TransportPublicKeyB64Url ?? string.Empty,
            DmPublicKeyB64Url: row.DmPublicKeyB64Url ?? string.Empty,
            XWingPublicKeyB64Url: row.XWingPublicKeyB64Url ?? string.Empty,
            AdmittedViaTokenId: row.AdmittedViaTokenId ?? string.Empty,
            MintingSessionEvidence: row.MintingSessionEvidence ?? string.Empty,
            WireFormatVersion: row.WireFormatVersion,
            ReceivedAtIso: row.ReceivedAtUtc?.ToString("O") ?? string.Empty,
            ReceivedByPartyId: row.ReceivedByPartyId ?? string.Empty,
            ReceivedByPublicKey: row.ReceivedByPublicKey ?? string.Empty,
            ReceiveAttestationSignatureB64Url: row.ReceiveAttestationSignatureB64Url ?? string.Empty);
    }

    private static DateTimeOffset ParseInstant(string iso) =>
        DateTimeOffset.TryParse(iso, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dto)
            ? dto
            : DateTimeOffset.UtcNow;

    private static DateTimeOffset? ParseOptionalInstant(string iso) =>
        DateTimeOffset.TryParse(iso, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dto) ? dto : null;
}
