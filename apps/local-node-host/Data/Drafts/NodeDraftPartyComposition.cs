using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Data.Drafts;

/// <summary>
/// Wires the ADR-0102 party-resolution seam for the single-operator node so the D2
/// submission-draft service can key drafts by the fail-closed <c>(TenantId, case, PartyId)</c>
/// tuple (ADR 0135 amendment 2026-07-01).
/// </summary>
/// <remarks>
/// <para>
/// The node has no single <see cref="ITenantContext"/> sum-interface object — it registers
/// <c>ActiveTeamAuthorizationContext</c> (<see cref="ICurrentUser"/>) and <c>ActiveTeamTenantContext</c>
/// (<see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext"/>) separately.
/// <see cref="NodeAuthorizationTenantContext"/> composes those into the sum-interface
/// <see cref="PartyContext"/> needs, so party derivation reads the UserId + TenantId off the same
/// active-team-bound principal (same-token derivation preserved). Its third member,
/// <c>HasPermission</c>, refuses: party derivation asks no authorization question, and ticket 205 does
/// not leave a record-less permission string resolvable through a plumbing adapter.
/// </para>
/// <para>
/// <b>Single-operator resolver.</b> <see cref="NodeOperatorPartyResolver"/> maps the local operator
/// (<see cref="ActiveTeamAuthorizationContext.LocalUserId"/>) to a STABLE PartyId derived from the roster's
/// genesis party id, and returns null (⇒ fail-closed block) for any other principal. Tenant isolation
/// is preserved by the tenant element of the draft key, not the party. When a roster-backed
/// per-org resolver lands, only this registration changes (the ADR-0102 D4 swap seam).
/// </para>
/// </remarks>
public static class NodeDraftPartyComposition
{
    /// <summary>
    /// Registers the node party seam: the sum-interface adapter, the single-operator resolver, and the
    /// <see cref="PartyContext"/> facade. Idempotent (<c>TryAdd</c>) so it never clobbers a host that
    /// already wired a party seam.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="genesisPartyId">The roster genesis party id — the operator's stable party identity.</param>
    public static IServiceCollection AddNodeDraftPartyContext(this IServiceCollection services, string genesisPartyId)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<ITenantContext, NodeAuthorizationTenantContext>();
        services.TryAddSingleton<IPrincipalPartyResolver>(_ => new NodeOperatorPartyResolver(genesisPartyId));
        services.TryAddScoped<IPartyContext, PartyContext>();
        return services;
    }
}

/// <summary>
/// Composes the node's separately-registered <see cref="ICurrentUser"/>,
/// and <see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext"/> into the
/// <see cref="ITenantContext"/> sum-interface <see cref="PartyContext"/> depends on.
/// </summary>
public sealed class NodeAuthorizationTenantContext : ITenantContext
{
    private readonly ICurrentUser _user;
    private readonly Harborline.Api.Foundation.MultiTenancy.ITenantContext _tenant;

    /// <summary>Constructs the adapter over the node's identity and tenant contexts.</summary>
    public NodeAuthorizationTenantContext(
        ICurrentUser user,
        Harborline.Api.Foundation.MultiTenancy.ITenantContext tenant)
    {
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
    }

    /// <inheritdoc />
    public string UserId => _user.UserId;

    /// <inheritdoc />
    public System.Collections.Generic.IReadOnlyList<string> Roles => _user.Roles;

    /// <summary>
    /// Refuses. This adapter exists to give <see cref="PartyContext"/> the UserId + TenantId it derives a
    /// party from; it adapts NO act, so it has no record, no act instant and no principal to decide about,
    /// and it never had a caller — <see cref="PartyContext"/> reads only <see cref="UserId"/> and
    /// <see cref="Tenant"/>. Ticket 205 (ledger L592/L600/L671): rather than pass the question down to the
    /// ambient string-only context — where a bare permission name resolves against whoever the container
    /// last bound — the adapter says it cannot answer. An act that needs a verdict resolves one
    /// <c>AuthorizationGate.DecideAsync</c> decision at its own point of use, naming the record it
    /// addresses.
    /// </summary>
    /// <param name="permission">Ignored; no permission is resolvable here.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public bool HasPermission(string permission) =>
        throw new NotSupportedException(
            $"NodeAuthorizationTenantContext cannot decide '{permission}': it adapts identity and tenancy "
            + "for party derivation, not authorization. Resolve the act through "
            + "Harborline.Api.Foundation.Authorization.AuthorizationGate.DecideAsync at its point of use, "
            + "naming the record it addresses (ticket 205).");

    /// <inheritdoc />
    public Harborline.Api.Foundation.MultiTenancy.TenantMetadata? Tenant => _tenant.Tenant;
}

/// <summary>
/// The single-operator node's <see cref="IPrincipalPartyResolver"/>: maps the local operator to a stable
/// PartyId derived from the roster genesis party id; returns null (fail-closed) for anyone else.
/// </summary>
public sealed class NodeOperatorPartyResolver : IPrincipalPartyResolver
{
    private readonly Guid _operatorPartyId;

    /// <summary>Constructs the resolver, deriving the operator's stable PartyId from the genesis party id.</summary>
    public NodeOperatorPartyResolver(string genesisPartyId)
    {
        _operatorPartyId = DeriveOperatorPartyId(genesisPartyId);
    }

    /// <inheritdoc />
    public ValueTask<Guid?> ResolveAsync(string userId, TenantId tenantId, CancellationToken ct = default)
    {
        // Fail-closed for any principal other than the local operator.
        var partyId = string.Equals(userId, ActiveTeamAuthorizationContext.LocalUserId, StringComparison.Ordinal)
            ? (Guid?)_operatorPartyId
            : null;
        return ValueTask.FromResult(partyId);
    }

    /// <summary>
    /// Derives a stable operator PartyId. Uses the genesis party id verbatim when it is already a GUID;
    /// otherwise deterministically maps the roster string to a GUID (SHA-256 → 16 bytes) so it is stable
    /// across restarts and process instances (the cross-device-resume invariant).
    /// </summary>
    private static Guid DeriveOperatorPartyId(string genesisPartyId)
    {
        if (!string.IsNullOrWhiteSpace(genesisPartyId) && Guid.TryParse(genesisPartyId, out var parsed))
        {
            return parsed;
        }
        var seed = string.IsNullOrWhiteSpace(genesisPartyId) ? ActiveTeamAuthorizationContext.LocalUserId : genesisPartyId;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("node-operator-party:" + seed));
        return new Guid(hash.AsSpan(0, 16));
    }
}
