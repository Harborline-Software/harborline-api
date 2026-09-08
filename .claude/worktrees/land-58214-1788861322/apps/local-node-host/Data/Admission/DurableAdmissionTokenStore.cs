using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.IdentityAtlas.Enrollment;

namespace Harborline.Api.LocalNodeHost.Data.Admission;

/// <summary>
/// The DURABLE <see cref="IAdmissionTokenStore"/> — the SQLCipher-backed replacement for the v1 in-memory store
/// (<c>InMemoryAdmissionTokenStore</c>). A minted invite is persisted to the recoverable
/// <see cref="NodeLocalAdmissionDbContext"/> so it SURVIVES a node restart within its TTL: a node that mints an
/// invite and then recycles (crash, redeploy, OS restart) can still have that invite redeemed by the joiner
/// (cerebrum [2026-06-21] in-memory admission token store follow-on — "invites lost on restart" is the gap this
/// closes). Single-use + TTL semantics are PRESERVED exactly, and every rejection path is FAIL-CLOSED.
/// </summary>
/// <remarks>
/// <para>
/// <b>Synchronous interface over async EF — by design.</b> <see cref="IAdmissionTokenStore"/> is deliberately
/// synchronous (the <see cref="AdmissionCoordinator"/> calls <see cref="Issue"/> / <see cref="Redeem"/> inline,
/// and admission is a LOW-frequency, human-paced operation — a few invites a day, never a hot path). Each call
/// opens a short-lived <see cref="NodeLocalAdmissionDbContext"/> via the factory and runs the EF round-trip
/// synchronously. Redemption is a DATABASE compare-and-swap:
/// <c>UPDATE ... SET redeemed = 1 WHERE token_id = ? AND redeemed = 0 AND not-expired</c>. Exactly one process can
/// affect the row, so single-use remains atomic across multiple host processes; no process-local lock is
/// load-bearing.
/// </para>
/// <para>
/// <b>Fail-closed.</b> Unknown / already-redeemed / expired all REJECT. An expired-but-unredeemed token is purged
/// on redeem (it can never succeed) but is NOT marked redeemed (it was not a successful single-use). The TTL is
/// checked against the caller-supplied <c>now</c> (testable clock; no ambient time), exactly as the in-memory
/// store did.
/// </para>
/// </remarks>
public sealed class DurableAdmissionTokenStore : IAdmissionTokenStore
{
    private readonly IDbContextFactory<NodeLocalAdmissionDbContext> _factory;

    /// <summary>Construct over the SQLCipher-keyed admission DbContext factory (registered by
    /// <c>AddLocalNodeSqlCipherStore</c>).</summary>
    public DurableAdmissionTokenStore(IDbContextFactory<NodeLocalAdmissionDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public void Issue(AdmissionToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(token.TokenId);
        ArgumentNullException.ThrowIfNull(token.Anchor);

        var record = new NodeAdmissionTokenRecord
        {
            TokenId = token.TokenId,
            AnchorTeamId = token.Anchor.TeamId,
            AnchorGenesisPartyId = token.Anchor.GenesisPartyId,
            AnchorGenesisPublicKey = token.Anchor.GenesisPublicKey,
            IssuedAtUtc = token.IssuedAt,
            TtlMilliseconds = (long)token.Ttl.TotalMilliseconds,
            Redeemed = false,
        };

        using var ctx = _factory.CreateDbContext();
        // A freshly-minted token id is a new GUID — it will not collide. Upsert-by-overwrite is the safest
        // shape if a (vanishingly unlikely) id were re-minted: replace the prior unredeemed row.
        var existing = ctx.AdmissionTokens.Find(token.TokenId);
        if (existing is null)
        {
            ctx.AdmissionTokens.Add(record);
        }
        else
        {
            existing.AnchorTeamId = record.AnchorTeamId;
            existing.AnchorGenesisPartyId = record.AnchorGenesisPartyId;
            existing.AnchorGenesisPublicKey = record.AnchorGenesisPublicKey;
            existing.IssuedAtUtc = record.IssuedAtUtc;
            existing.TtlMilliseconds = record.TtlMilliseconds;
            existing.Redeemed = false;
        }
        ctx.SaveChanges();
    }

    /// <inheritdoc />
    public RedeemResult Redeem(string tokenId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);

        using var ctx = _factory.CreateDbContext();
        var nowUnixMs = now.ToUnixTimeMilliseconds();

        // R6/D — one SQL statement is the cross-process CAS. SQLite takes the write lock while evaluating the
        // predicate + flipping the bit, so two independent store instances/processes cannot both affect this row.
        var affected = ctx.Database.ExecuteSql($"""
            UPDATE admission_tokens
            SET redeemed = 1
            WHERE token_id = {tokenId}
              AND redeemed = 0
              AND ttl_ms > 0
              AND issued_at + ttl_ms > {nowUnixMs}
            """);

        if (affected == 1)
        {
            var accepted = ctx.AdmissionTokens.AsNoTracking().Single(row => row.TokenId == tokenId);
            return new RedeemResult(RedeemOutcome.Accepted, ToToken(accepted));
        }

        // The CAS lost or its predicate rejected. Classify without changing a live row.
        var row = ctx.AdmissionTokens.AsNoTracking().SingleOrDefault(row => row.TokenId == tokenId);
        if (row is null)
        {
            return new RedeemResult(RedeemOutcome.UnknownToken, null);
        }
        if (row.Redeemed)
        {
            return new RedeemResult(RedeemOutcome.AlreadyRedeemed, null);
        }

        // Expired/non-positive TTL rows can never succeed. Purge only while still unredeemed; a concurrent CAS
        // cannot match an expired row, and a re-issue with the same unguessable id is outside the protocol.
        ctx.Database.ExecuteSql($"""
            DELETE FROM admission_tokens
            WHERE token_id = {tokenId}
              AND redeemed = 0
              AND (ttl_ms <= 0 OR issued_at + ttl_ms <= {nowUnixMs})
            """);
        return new RedeemResult(RedeemOutcome.Expired, null);
    }

    private static AdmissionToken ToToken(NodeAdmissionTokenRecord row) =>
        new(
            TokenId: row.TokenId,
            Anchor: new TeamTrustAnchor(
                TeamId: row.AnchorTeamId,
                GenesisPartyId: row.AnchorGenesisPartyId,
                GenesisPublicKey: row.AnchorGenesisPublicKey),
            IssuedAt: row.IssuedAtUtc,
            Ttl: TimeSpan.FromMilliseconds(row.TtlMilliseconds));
}
