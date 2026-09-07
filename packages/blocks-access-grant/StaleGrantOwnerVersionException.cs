using System;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// Typed refusal raised when a grant mutation presents an owner version that is no longer current.
/// Callers must reread canonical grant authority before deciding whether to retry.
/// </summary>
public sealed class StaleGrantOwnerVersionException : InvalidOperationException
{
    /// <summary>Creates a stale-version refusal with the canonical conflict coordinates.</summary>
    public StaleGrantOwnerVersionException(
        TenantId tenantId,
        GrantId grantId,
        long expectedOwnerVersion,
        long actualOwnerVersion)
        : base(
            $"Grant mutation refused for tenant '{tenantId.Value}', grant '{grantId}': expected owner "
            + $"version {expectedOwnerVersion}, actual owner version {actualOwnerVersion}.")
    {
        TenantId = tenantId;
        GrantId = grantId;
        ExpectedOwnerVersion = expectedOwnerVersion;
        ActualOwnerVersion = actualOwnerVersion;
    }

    /// <summary>The grant's tenant authority boundary.</summary>
    public TenantId TenantId { get; }

    /// <summary>The grant whose mutation was refused.</summary>
    public GrantId GrantId { get; }

    /// <summary>The caller-supplied version.</summary>
    public long ExpectedOwnerVersion { get; }

    /// <summary>The canonical version observed while holding the mutation fence.</summary>
    public long ActualOwnerVersion { get; }
}
