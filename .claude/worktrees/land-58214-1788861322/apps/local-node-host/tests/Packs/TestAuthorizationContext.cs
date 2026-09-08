using System;
using System.Collections.Generic;

using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// A test <see cref="IAuthorizationContext"/> for the <c>/packs/*</c> route-permission tests (council A-1).
/// <see cref="AllowAll"/> grants every permission (preserves the pre-gate route behaviour for the
/// happy-path tests); <see cref="Allowing"/> grants only the named permissions (exercises the fail-closed
/// 403 deny path).
/// </summary>
internal sealed class TestAuthorizationContext : IAuthorizationContext
{
    private readonly IReadOnlySet<string>? _granted;

    private TestAuthorizationContext(IReadOnlySet<string>? granted) => _granted = granted;

    /// <summary>Grants every permission (null grant-set = allow-all).</summary>
    public static TestAuthorizationContext AllowAll() => new(null);

    /// <summary>Grants only the named permissions; everything else is denied fail-closed.</summary>
    public static TestAuthorizationContext Allowing(params string[] permissions)
        => new(new HashSet<string>(permissions, StringComparer.Ordinal));

    /// <inheritdoc />
    public bool HasPermission(string permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return _granted is null || _granted.Contains(permission);
    }
}
