using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Data.Admission;

/// <summary>
/// MTW-2 #3167 (R6/D) — the DURABLE <see cref="IWebPairingInviteBindingStore"/>: the SQLCipher-backed replacement
/// for <c>InMemoryWebPairingInviteBindingStore</c>, so a device-pairing binding minted before a node recycle can
/// still be looked up (and its pins re-verified) when the joining device redeems within the token's TTL. Mirrors
/// <see cref="DurableAdmissionTokenStore"/>: a single process-wide lock serialises the short-lived EF round-trips
/// (admission is human-paced + low-frequency), and every read/write opens a fresh
/// <see cref="NodeLocalAdmissionDbContext"/> from the factory.
/// </summary>
/// <remarks>
/// This store is a pure keyed LOOKUP — single-use enforcement lives in the token store's atomic CAS
/// <c>Redeem</c>, NOT here (a binding read after the token is redeemed still returns the pins; the redemption's
/// single-use gate is the token store). Binding a token twice would be a mint-side bug (upsert-by-overwrite is the
/// safe shape). The bound <c>TenantMembershipSnapshot</c> is persisted as canonical JSON and re-read live at
/// redemption for pin re-verification — the stored snapshot carries only coordinates, never authority.
/// </remarks>
internal sealed class DurableWebPairingInviteBindingStore : IWebPairingInviteBindingStore
{
    private static readonly System.Text.Json.JsonSerializerOptions MembershipJsonOptions = new();

    private readonly object _gate = new();
    private readonly IDbContextFactory<NodeLocalAdmissionDbContext> _factory;

    /// <summary>Construct over the SQLCipher-keyed admission DbContext factory (registered by
    /// <c>AddLocalNodeSqlCipherStore</c>) — the SAME context + encrypted file as the durable token store.</summary>
    public DurableWebPairingInviteBindingStore(IDbContextFactory<NodeLocalAdmissionDbContext> factory)
    {
        _factory = factory ?? throw new System.ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public void Bind(WebPairingInviteBinding binding)
    {
        System.ArgumentNullException.ThrowIfNull(binding);
        System.ArgumentException.ThrowIfNullOrWhiteSpace(binding.TokenId);
        System.ArgumentNullException.ThrowIfNull(binding.Membership);

        var membershipJson = System.Text.Json.JsonSerializer.Serialize(binding.Membership, MembershipJsonOptions);

        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            // A freshly-minted token id is a new GUID — it will not collide. Upsert-by-overwrite is the safe shape
            // if a (vanishingly unlikely) id were re-bound: replace the prior row.
            var existing = ctx.PairingInviteBindings.Find(binding.TokenId);
            if (existing is null)
            {
                ctx.PairingInviteBindings.Add(new NodePairingInviteBindingRecord
                {
                    TokenId = binding.TokenId,
                    BoundPartyId = binding.BoundPartyId,
                    SessionCorrelationId = binding.SessionCorrelationId ?? string.Empty,
                    MembershipJson = membershipJson,
                });
            }
            else
            {
                existing.BoundPartyId = binding.BoundPartyId;
                existing.SessionCorrelationId = binding.SessionCorrelationId ?? string.Empty;
                existing.MembershipJson = membershipJson;
            }
            ctx.SaveChanges();
        }
    }

    /// <inheritdoc />
    public WebPairingInviteBinding? Lookup(string tokenId)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
        {
            return null;
        }

        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var row = ctx.PairingInviteBindings.Find(tokenId);
            var token = ctx.AdmissionTokens.AsNoTracking().SingleOrDefault(candidate => candidate.TokenId == tokenId);
            if (row is null || token is null)
            {
                return null;
            }

            TenantMembershipSnapshot? membership;
            try
            {
                membership = System.Text.Json.JsonSerializer.Deserialize<TenantMembershipSnapshot>(
                    row.MembershipJson, MembershipJsonOptions);
            }
            catch (System.Text.Json.JsonException)
            {
                // A corrupt/at-rest-tampered membership blob fails closed — no binding, so the redemption refuses
                // (nothing is admitted from an unreadable pin set).
                return null;
            }
            if (membership is null)
            {
                return null;
            }

            return new WebPairingInviteBinding(
                row.TokenId,
                membership,
                row.BoundPartyId,
                new Harborline.Api.Foundation.IdentityAtlas.Enrollment.TeamTrustAnchor(
                    token.AnchorTeamId,
                    token.AnchorGenesisPartyId,
                    token.AnchorGenesisPublicKey),
                row.SessionCorrelationId ?? string.Empty);
        }
    }
}
