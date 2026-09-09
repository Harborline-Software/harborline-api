using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>
/// Reads one tenant's durable trust roster and rebuilds its signed genesis chain without consulting
/// installation-global or active-team state.
/// </summary>
public interface IVerifiedTenantRosterReader
{
    /// <summary>
    /// Rebuild and cryptographically verify the roster for the explicitly supplied tenant.
    /// </summary>
    /// <exception cref="VerifiedTenantRosterRefusedException">
    /// The durable rows do not form one complete, authentic, genesis-rooted roster for the requested tenant.
    /// </exception>
    Task<MemberRoster> ReadAsync(TenantId team, CancellationToken ct);
}

/// <summary>Stable fail-closed reasons emitted by <see cref="IVerifiedTenantRosterReader"/>.</summary>
public enum VerifiedTenantRosterRefusal
{
    /// <summary>The requested tenant is not the tenant represented by the durable roster.</summary>
    WrongTenant,

    /// <summary>No genesis admission exists for the requested tenant.</summary>
    MissingGenesis,

    /// <summary>More than one genesis admission exists for the requested tenant.</summary>
    MultipleGenesis,

    /// <summary>A durable row is malformed or its signature does not verify.</summary>
    Tampered,

    /// <summary>A durable row predates the current signed receive-attestation wire format.</summary>
    WireVersionUnsupported,

    /// <summary>A cryptographically valid admission is not reachable from the unique genesis.</summary>
    Orphan,
}

/// <summary>A classified refusal to treat durable roster rows as trust authority.</summary>
public sealed class VerifiedTenantRosterRefusedException : Exception
{
    /// <summary>Construct a classified refusal.</summary>
    public VerifiedTenantRosterRefusedException(VerifiedTenantRosterRefusal refusal, string message)
        : base(message)
    {
        Refusal = refusal;
    }

    /// <summary>The stable refusal category.</summary>
    public VerifiedTenantRosterRefusal Refusal { get; }
}
