
using Harborline.Api.Blocks.Leases.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Leases.Services;

/// <summary>
/// Payload for creating a new <see cref="Lease"/> via <see cref="ILeaseService.CreateAsync"/>.
/// </summary>
public sealed record CreateLeaseRequest
{
    /// <summary>Owning tenant for the new lease. Required by the
    /// <see cref="Harborline.Api.Foundation.MultiTenancy.IMustHaveTenant"/> contract on <see cref="Lease"/>.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The unit to be covered by the lease.</summary>
    public required EntityId UnitId { get; init; }

    /// <summary>All tenant parties on this lease (at least one required).</summary>
    public required IReadOnlyList<Harborline.Api.Blocks.People.Foundation.Models.PartyId> Tenants { get; init; }

    /// <summary>The landlord party for this lease.</summary>
    public required Harborline.Api.Blocks.People.Foundation.Models.PartyId Landlord { get; init; }

    /// <summary>Date the lease term begins.</summary>
    public required DateOnly StartDate { get; init; }

    /// <summary>Date the lease term ends.</summary>
    public required DateOnly EndDate { get; init; }

    /// <summary>Monthly rent amount in the base currency.</summary>
    public required decimal MonthlyRent { get; init; }
}
