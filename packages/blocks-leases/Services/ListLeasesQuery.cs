
using Harborline.Api.Blocks.Leases.Models;

namespace Harborline.Api.Blocks.Leases.Services;

/// <summary>
/// Optional filter parameters for <see cref="ILeaseService.ListAsync"/>.
/// All filters are additive (AND). A <see langword="null"/> value means "no filter on that field".
/// </summary>
public sealed record ListLeasesQuery
{
    /// <summary>
    /// When set, only leases in this phase are returned.
    /// </summary>
    public LeasePhase? Phase { get; init; }

    /// <summary>
    /// When set, only leases owned by this <see cref="Harborline.Api.Foundation.Assets.Common.TenantId"/>
    /// are returned. Pair with <see cref="Harborline.Api.Foundation.Authorization.ITenantContext.TenantId"/>
    /// to enforce tenant isolation at the query level (W#74 PR 2 A1 amendment).
    /// </summary>
    public Harborline.Api.Foundation.Assets.Common.TenantId? TenantId { get; init; }

    /// <summary>
    /// When set, only leases that include this tenant party are returned.
    /// Previously named <c>TenantId</c>; renamed to <c>TenantParty</c> on 2026-05-17 to free
    /// the <c>TenantId</c> identifier for the multi-tenancy scoping filter above.
    /// </summary>
    public Harborline.Api.Blocks.People.Foundation.Models.PartyId? TenantParty { get; init; }

    /// <summary>
    /// Shared empty query that applies no filters.
    /// </summary>
    public static ListLeasesQuery Empty { get; } = new();
}
