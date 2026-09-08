namespace Harborline.Api.Blocks.Leases.Models;

/// <summary>
/// The role a <see cref="Party"/> plays in a lease transaction.
/// </summary>
/// <remarks>
/// <b>DEPRECATED — use the canonical <c>PartyRole</c> string-code
/// registry from <c>blocks-people-foundation</c> instead.</b> This
/// enum is deleted once the last consumer has moved.
/// <see cref="LeaseHolderRole"/> is NOT deprecated — it answers the
/// per-lease assignment question (PrimaryLeaseholder / CoLeaseholder
/// / Occupant / Guarantor) which has no analogue in the cross-cluster
/// role registry.
/// </remarks>
[Obsolete("Use the canonical PartyRole string-code registry from Harborline.Api.Blocks.People.Foundation instead. Do not add new references: this enum is deleted once the last consumer has moved. LeaseHolderRole is unaffected.")]
public enum PartyKind
{
    /// <summary>A person or entity renting the unit.</summary>
    Tenant,

    /// <summary>The property owner or their agent.</summary>
    Landlord,

    /// <summary>A property manager acting on behalf of a landlord.</summary>
    Manager,

    /// <summary>A guarantor responsible for rent obligations.</summary>
    Guarantor
}
