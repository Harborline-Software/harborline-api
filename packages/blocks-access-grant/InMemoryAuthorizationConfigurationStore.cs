using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// Thread-safe in-memory authorization configuration store and production fallback reader.
/// </summary>
public sealed class InMemoryAuthorizationConfigurationStore :
    AuthorizationConfigurationStateReader,
    IAuthorizationConfigurationStore,
    IAuthorizationDefinitionReader,
    IAuthorizationDefinitionCatalogueReader,
    IHistoricalAuthorizationConfigurationReader,
    IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _bootstrapGate;
    private readonly InMemoryAuthorizationBootstrapFence _bootstrapFence;
    private readonly Dictionary<AuthorizationCapabilityDefinitionId, List<EffectiveDefinitionRevision>> _definitions = [];
    private readonly Dictionary<(TenantId TenantId, AuthorizationCapabilityDefinitionId DefinitionId), List<CapabilityRoleBindingRevision>>
        _bindings = [];

    internal InMemoryAuthorizationConfigurationStore(InMemoryAuthorizationBootstrapFence bootstrapFence)
    {
        ArgumentNullException.ThrowIfNull(bootstrapFence);
        _bootstrapGate = bootstrapFence.Gate;
        _bootstrapFence = bootstrapFence;
    }

    /// <summary>Releases the mutex protecting in-memory configuration state.</summary>
    public void Dispose() => _mutex.Dispose();

    /// <inheritdoc />
    public override async ValueTask<AuthorizationConfigurationState> ReadStateAsync(
        AuthorizationCapabilityDefinitionId definitionId,
        TenantId? tenantId = null,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var definition = _definitions.TryGetValue(definitionId, out var definitionRevisions)
                && IsVisibleTo(tenantId, definitionRevisions[^1])
                ? definitionRevisions[^1].Definition
                : null;
            CapabilityRoleBindingRevision? binding = null;
            if (tenantId is { } tenant)
            {
                binding = _bindings.TryGetValue((tenant, definitionId), out var bindingRevisions)
                    ? bindingRevisions[^1]
                    : null;
            }

            var effective = definition is null
                ? RoleBindingSet.Empty
                : binding is null
                    ? definition.OfferedRoles
                    : definition.OfferedRoles.Intersect(binding.SelectedRoles);
            return new AuthorizationConfigurationState(
                definition,
                effective,
                binding?.Revision ?? 0);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask CommitAsync(
        ValidatedAuthorizationConfigurationWrite write,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            switch (write.Kind)
            {
                case AuthorizationConfigurationWriteKind.InstallDefinition:
                    CommitInstall(write);
                    break;
                case AuthorizationConfigurationWriteKind.ReplaceDefinition:
                    CommitReplacement(write);
                    break;
                case AuthorizationConfigurationWriteKind.NarrowBinding:
                    CommitBinding(write);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported write kind '{write.Kind}'.");
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public ValueTask CommitBootstrapAsync(
        ValidatedAuthorizationConfigurationWrite write,
        TenantId tenant,
        IGrantStore grants,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(grants);
        if (grants is not InMemoryGrantStore memoryGrants
            || !ReferenceEquals(memoryGrants.BootstrapFence, _bootstrapFence))
        {
            throw new InvalidOperationException(BootstrapFenceMismatchMessage);
        }
        _ = tenant;
        ct.ThrowIfCancellationRequested();
        lock (_bootstrapGate)
        {
            _mutex.Wait(ct);
            try
            {
                if (grants.HasAdministratorGrantEverAsync(ct).GetAwaiter().GetResult())
                    throw new InvalidOperationException("The authorization-definition bootstrap path is permanently sealed.");
                // Keep the final proof adjacent to the commit. This also fails closed for instrumented
                // stores that expose an Administrator append after their first observation.
                if (grants.HasAdministratorGrantEverAsync(ct).GetAwaiter().GetResult())
                    throw new InvalidOperationException("The authorization-definition bootstrap path is permanently sealed.");
                switch (write.Kind)
                {
                    case AuthorizationConfigurationWriteKind.InstallDefinition: CommitInstall(write); break;
                    case AuthorizationConfigurationWriteKind.ReplaceDefinition: CommitReplacement(write); break;
                    case AuthorizationConfigurationWriteKind.NarrowBinding: CommitBinding(write); break;
                    default: throw new InvalidOperationException($"Unsupported write kind '{write.Kind}'.");
                }
            }
            finally
            {
                _mutex.Release();
            }
        }
        return ValueTask.CompletedTask;
    }

    internal const string BootstrapFenceMismatchMessage =
        "The in-memory authorization stores must share one bootstrap fence.";

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AuthorizationCapabilityDefinition>> DefinitionsForRoleAsync(
        TenantId tenantId,
        RoleReference role,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _definitions.Values
                .Select(revisions => revisions[^1])
                .Where(revision => IsVisibleTo(tenantId, revision))
                .Select(revision => revision.Definition)
                .Where(definition => EffectiveBinding(tenantId, definition).Roles.Contains(role))
                .OrderBy(definition => definition.PublisherPackageId, StringComparer.Ordinal)
                .ThenBy(definition => definition.DefinitionId.Value)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<AuthorizationCapabilityDefinition>> DefinitionsForRoleAtAsync(
        TenantId tenantId,
        RoleReference role,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _definitions.Values
                .Select(revisions => revisions
                    .Where(revision => revision.EffectiveAt <= at)
                    .OrderByDescending(revision => revision.EffectiveAt)
                    .ThenByDescending(revision => revision.Definition.Revision)
                    .FirstOrDefault())
                .Where(revision => revision is not null)
                .Where(revision => IsVisibleTo(tenantId, revision!))
                .Where(revision => EffectiveBindingAt(tenantId, revision!.Definition, at).Roles.Contains(role))
                .Select(revision => revision!.Definition)
                .OrderBy(definition => definition.PublisherPackageId, StringComparer.Ordinal)
                .ThenBy(definition => definition.DefinitionId.Value)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RoleBindingSet> EffectiveBindingAsync(
        TenantId tenantId,
        AuthorizationCapabilityDefinitionId definitionId,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _definitions.TryGetValue(definitionId, out var revisions)
                && IsVisibleTo(tenantId, revisions[^1])
                ? EffectiveBinding(tenantId, revisions[^1].Definition)
                : RoleBindingSet.Empty;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<AuthorizationDefinitionBindingView>> ListAsync(
        TenantId tenantId,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _definitions.Values
                .Select(revisions => revisions[^1])
                .Where(revision => IsVisibleTo(tenantId, revision))
                .Select(revision => ToCatalogueView(tenantId, revision.Definition))
                .OrderBy(view => view.Definition.DefinitionId.Value)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<AuthorizationDefinitionBindingView?> FindAsync(
        TenantId tenantId,
        AuthorizationCapabilityDefinitionId definitionId,
        CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _definitions.TryGetValue(definitionId, out var revisions)
                && IsVisibleTo(tenantId, revisions[^1])
                ? ToCatalogueView(tenantId, revisions[^1].Definition)
                : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private void CommitInstall(ValidatedAuthorizationConfigurationWrite write)
    {
        var definition = write.Definition
            ?? throw new InvalidOperationException("An install write requires a definition.");
        if (_definitions.ContainsKey(definition.DefinitionId) || write.ExpectedDefinitionRevision != 0)
        {
            throw new InvalidOperationException("The authorization definition was installed concurrently.");
        }

        _definitions.Add(definition.DefinitionId, [new EffectiveDefinitionRevision(
            definition,
            write.DefinitionEffectiveAt
                ?? throw new InvalidOperationException("A definition write requires an effective instant."),
            write.DeclaringTenantId)]);
    }

    private void CommitReplacement(ValidatedAuthorizationConfigurationWrite write)
    {
        var definition = write.Definition
            ?? throw new InvalidOperationException("A replacement write requires a definition.");
        if (!_definitions.TryGetValue(definition.DefinitionId, out var revisions)
            || revisions.Count == 0
            || revisions[^1].Definition is not { } current
            || current.Revision != write.ExpectedDefinitionRevision
            || revisions[^1].DeclaringTenantId != write.DeclaringTenantId)
        {
            throw new InvalidOperationException("The authorization definition changed after the bind stage.");
        }

        revisions.Add(new EffectiveDefinitionRevision(
            definition,
            write.DefinitionEffectiveAt
                ?? throw new InvalidOperationException("A definition write requires an effective instant."),
            write.DeclaringTenantId));
    }

    private void CommitBinding(ValidatedAuthorizationConfigurationWrite write)
    {
        var binding = write.BindingRevision
            ?? throw new InvalidOperationException("A binding write requires a binding revision.");
        if (!_definitions.TryGetValue(binding.DefinitionId, out var definitionRevisions)
            || definitionRevisions.Count == 0
            || definitionRevisions[^1].Definition is not { } definition
            || definition.Revision != write.ExpectedDefinitionRevision)
        {
            throw new InvalidOperationException("The publisher definition changed after the bind stage.");
        }

        var key = (binding.TenantId, binding.DefinitionId);
        var currentRevision = _bindings.TryGetValue(key, out var revisions) && revisions.Count > 0
            ? revisions[^1].Revision
            : 0;
        if (currentRevision != write.ExpectedBindingRevision)
        {
            throw new InvalidOperationException("The tenant binding changed after the bind stage.");
        }

        if (revisions is null)
        {
            _bindings.Add(key, [binding]);
        }
        else
        {
            revisions.Add(binding);
        }
    }

    private RoleBindingSet EffectiveBinding(
        TenantId tenantId,
        AuthorizationCapabilityDefinition definition) =>
        _bindings.TryGetValue((tenantId, definition.DefinitionId), out var revisions)
            && revisions.Count > 0
            ? definition.OfferedRoles.Intersect(revisions[^1].SelectedRoles)
            : definition.OfferedRoles;

    private AuthorizationDefinitionBindingView ToCatalogueView(
        TenantId tenantId,
        AuthorizationCapabilityDefinition definition)
    {
        var hasBinding = _bindings.TryGetValue((tenantId, definition.DefinitionId), out var revisions)
            && revisions.Count > 0;
        var revision = hasBinding ? revisions![^1] : null;
        var effective = revision is null
            ? definition.OfferedRoles
            : definition.OfferedRoles.Intersect(revision.SelectedRoles);
        return new AuthorizationDefinitionBindingView(
            definition,
            revision?.Revision ?? 0,
            effective,
            revision is not null && effective.Equals(RoleBindingSet.Empty)
                ? BindingWarningCode.EmptyBinding
                : null);
    }

    private RoleBindingSet EffectiveBindingAt(
        TenantId tenantId,
        AuthorizationCapabilityDefinition definition,
        DateTimeOffset at)
    {
        if (!_bindings.TryGetValue((tenantId, definition.DefinitionId), out var revisions))
            return definition.OfferedRoles;
        var binding = revisions
            .Where(revision => revision.ChangedAt <= at)
            .OrderByDescending(revision => revision.ChangedAt)
            .ThenByDescending(revision => revision.Revision)
            .FirstOrDefault();
        return binding is null
            ? definition.OfferedRoles
            : definition.OfferedRoles.Intersect(binding.SelectedRoles);
    }

    private static bool IsVisibleTo(TenantId? tenantId, EffectiveDefinitionRevision revision) =>
        revision.DeclaringTenantId is null || revision.DeclaringTenantId == tenantId;

    private sealed record EffectiveDefinitionRevision(
        AuthorizationCapabilityDefinition Definition,
        DateTimeOffset EffectiveAt,
        TenantId? DeclaringTenantId);
}
