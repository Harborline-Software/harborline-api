using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.BackupRestore;

/// <summary>The exact coordinates and acts covered by the roster member's operation signature.</summary>
public sealed record RehostGrantPayload(string TenantId, string ReplacedNodeId, string ReplacementNodeId,
    string ReplacementPublicKey, string[] Acts, DateTimeOffset ExpiresAt);

/// <summary>Issues and redeems grants using the durable roster and its existing encrypted database.</summary>
public sealed class SignedRosterRehostGrantProvider(
    IDbContextFactory<NodeLocalRosterDbContext> contexts, IOperationSigner signer, IOperationVerifier verifier,
    AuthorizationGate gate, AuthorizationRefusalAudit audit, TimeProvider time) : IRosterRehostGrantProvider
{
    /// <summary>The holder convergence act.</summary>
    public const string ReadCanonical = "rehost:read-canonical";
    /// <summary>The home epoch promotion act.</summary>
    public const string PromoteHome = "rehost:promote-home";

    /// <inheritdoc />
    public async ValueTask<RosterSignedRehostGrant> ObtainAsync(string tenantId, string replacedNodeId,
        NodeIdentity replacementIdentity, IReadOnlyList<string> attestingTrusteeNodeIds, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var payload = new RehostGrantPayload(tenantId, replacedNodeId, replacementIdentity.NodeId,
            Convert.ToBase64String(replacementIdentity.PublicKey), [ReadCanonical, PromoteHome], now.AddMinutes(5));
        var signed = await signer.SignAsync(payload, now, Guid.NewGuid());
        var grant = new RosterSignedRehostGrant(JsonSerializer.Serialize(signed));
        return grant;
    }

    /// <summary>Verify and burn once; consumers must perform only the requested acts after this returns.</summary>
    public async ValueTask<AuthorizationDecision> RedeemAsync(RosterSignedRehostGrant? grant, string tenantId,
        string replacedNodeId, NodeIdentity replacement, IReadOnlyList<string> requiredActs, ActorId caller,
        CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var tenant = TenantId.FromString(tenantId);
        SignedOperation<RehostGrantPayload>? signed = null;
        string? reason = null;
        try { signed = JsonSerializer.Deserialize<SignedOperation<RehostGrantPayload>>(grant?.SerializedGrant ?? string.Empty); }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        { reason = "rehost.malformed"; }
        if (signed?.Payload is not { Acts: not null } payload || signed.Nonce == Guid.Empty)
            reason = "rehost.malformed";
        else
        {
            if (!verifier.Verify(signed)) reason = "rehost.invalid_signature";
            else if (payload.TenantId != tenantId) reason = "rehost.wrong_tenant";
            else if (payload.ReplacedNodeId != replacedNodeId) reason = "rehost.wrong_replaced_node";
            else if (payload.ReplacementNodeId != replacement.NodeId ||
                payload.ReplacementPublicKey != Convert.ToBase64String(replacement.PublicKey)) reason = "rehost.wrong_replacement";
            else if (requiredActs.Count == 0 || requiredActs.Any(act => !payload.Acts.Contains(act, StringComparer.Ordinal)) ||
                payload.Acts.Any(act => act is not (ReadCanonical or PromoteHome))) reason = "rehost.out_of_scope";
            else if (signed.IssuedAt > now) reason = "rehost.not_yet_valid";
            else if (payload.ExpiresAt <= now || payload.ExpiresAt <= signed.IssuedAt) reason = "rehost.expired";
        }
        await using var db = await contexts.CreateDbContextAsync(ct);
        // Single use rests on the receipt's composite primary key. The transaction rolls back denied burns.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (reason is null)
        {
            try
            {
                var reader = new VerifiedTenantRosterReader(contexts, verifier);
                var issued = await reader.ReadAtAsync(tenant, signed!.IssuedAt, ct);
                var current = await reader.ReadAtAsync(tenant, now, ct);
                if (!issued.Members.Any(member => member.PublicKey.Equals(signed.IssuerId)))
                    reason = issued.EnumerateAdmissions().Any(member => member.PublicKey.Equals(signed.IssuerId))
                        ? "rehost.issuer_revoked_at_issue" : "rehost.issuer_unadmitted";
                else if (!current.Members.Any(member => member.PublicKey.Equals(signed.IssuerId)))
                    reason = "rehost.issuer_revoked_at_redemption";
            }
            catch (VerifiedTenantRosterRefusedException) { reason = "rehost.invalid_roster"; }
        }
        if (reason is null)
        {
            var issuer = signed!.IssuerId.ToBase64Url();
            var nonce = signed.Nonce.ToString("D");
            var inserted = await db.Database.ExecuteSqlAsync(
                $"INSERT OR IGNORE INTO rehost_grant_burns (tenant, issuer, nonce) VALUES ({tenantId}, {issuer}, {nonce})", ct);
            if (inserted == 0) reason = "rehost.already_redeemed";
        }
        var request = new AuthorizationWriteContext(caller, tenant, now).Request(
            AuthorizationOperation.Parse("members:admit"), "members", replacement.NodeId) with { GrantRefusal = reason };
        var decision = await gate.DecideAsync(request, ct);
        if (decision.Verdict == AuthorizationVerdict.Denied)
        {
            await transaction.RollbackAsync(ct);
            var refusal = await AuthorizationRefusalRenderer.RenderAsync(decision, [], null, ct);
            await audit.RecordAsync(refusal, request.Act.Operation.Value, caller, tenant, now, decision, ct);
            decision.RequireAllowed();
        }
        await transaction.CommitAsync(ct);
        return decision;
    }
}
