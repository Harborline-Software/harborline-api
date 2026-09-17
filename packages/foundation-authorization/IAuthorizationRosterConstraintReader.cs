using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>Reads the gate-owned roster facts for one principal and tenant.</summary>
public interface IAuthorizationRosterConstraintReader
{
    /// <summary>Returns verified roster facts, or null when the roster or principal binding cannot be verified.</summary>
    ValueTask<AuthorizationRosterInputs?> ReadAsync(
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);
}

/// <summary>The fail-closed floor for compositions that have no roster authority.</summary>
public sealed class RefusingAuthorizationRosterConstraintReader : IAuthorizationRosterConstraintReader
{
    /// <summary>The shared stateless instance.</summary>
    public static RefusingAuthorizationRosterConstraintReader Shared { get; } = new();

    /// <inheritdoc />
    public ValueTask<AuthorizationRosterInputs?> ReadAsync(
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AuthorizationRosterInputs?>(null);
}

internal sealed class TestMemberAuthorizationRosterConstraintReader : IAuthorizationRosterConstraintReader
{
    internal static TestMemberAuthorizationRosterConstraintReader Shared { get; } = new();

    public ValueTask<AuthorizationRosterInputs?> ReadAsync(
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AuthorizationRosterInputs?>(new(principal.Value, Member: true, Ejected: false));
}
