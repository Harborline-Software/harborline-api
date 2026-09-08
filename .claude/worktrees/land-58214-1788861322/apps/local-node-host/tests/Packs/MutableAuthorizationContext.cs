using System;
using System.Collections.Generic;

using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// A mutable test <see cref="IAuthorizationContext"/> for gated-route tests that flip permissions
/// mid-flight (where <see cref="TestAuthorizationContext"/> is immutable). Allow-all by default;
/// <see cref="Allow"/> narrows to exactly the named permissions; <see cref="DenyAll"/> refuses everything.
/// </summary>
internal sealed class MutableAuthorizationContext : IAuthorizationContext
{
    private readonly HashSet<string> _permissions = new(StringComparer.Ordinal);
    private bool _allowAll = true;

    public bool HasPermission(string permission) => _allowAll || _permissions.Contains(permission);

    public void Allow(params string[] permissions)
    {
        _allowAll = false;
        _permissions.Clear();
        _permissions.UnionWith(permissions);
    }

    public void DenyAll()
    {
        _allowAll = false;
        _permissions.Clear();
    }
}
