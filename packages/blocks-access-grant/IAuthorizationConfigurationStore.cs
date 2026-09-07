using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>The write kind carried by an opaque validated configuration value.</summary>
public enum AuthorizationConfigurationWriteKind
{
    /// <summary>Install a new definition.</summary>
    InstallDefinition = 0,

    /// <summary>Replace a definition with its next revision.</summary>
    ReplaceDefinition = 1,

    /// <summary>Append a tenant binding revision.</summary>
    NarrowBinding = 2,
}

/// <summary>Current store state read by the bind stage for validate-stage admission.</summary>
public sealed record AuthorizationConfigurationState(
    AuthorizationCapabilityDefinition? Definition,
    RoleBindingSet EffectiveBinding,
    long BindingRevision);

/// <summary>Bind-stage state access implemented by each configuration store.</summary>
public abstract class AuthorizationConfigurationStateReader
{
    /// <summary>Reads current state without mutating configuration.</summary>
    public abstract ValueTask<AuthorizationConfigurationState> ReadStateAsync(
        AuthorizationCapabilityDefinitionId definitionId,
        TenantId? tenantId = null,
        CancellationToken ct = default);
}

/// <summary>
/// An opaque authorization configuration mutation. Only the writer's validate step constructs it.
/// </summary>
public sealed class ValidatedAuthorizationConfigurationWrite
{
    internal ValidatedAuthorizationConfigurationWrite(
        AuthorizationConfigurationWriteKind kind,
        AuthorizationCapabilityDefinition? definition,
        CapabilityRoleBindingRevision? bindingRevision,
        long expectedDefinitionRevision,
        long expectedBindingRevision,
        DateTimeOffset? definitionEffectiveAt,
        TenantId? declaringTenantId)
    {
        Kind = kind;
        Definition = definition;
        BindingRevision = bindingRevision;
        ExpectedDefinitionRevision = expectedDefinitionRevision;
        ExpectedBindingRevision = expectedBindingRevision;
        DefinitionEffectiveAt = definitionEffectiveAt;
        DeclaringTenantId = declaringTenantId;
    }

    /// <summary>The admitted mutation kind.</summary>
    public AuthorizationConfigurationWriteKind Kind { get; }

    /// <summary>The admitted definition mutation, when present.</summary>
    public AuthorizationCapabilityDefinition? Definition { get; }

    /// <summary>The admitted tenant binding revision, when present.</summary>
    public CapabilityRoleBindingRevision? BindingRevision { get; }

    /// <summary>The definition revision observed by the bind stage.</summary>
    public long ExpectedDefinitionRevision { get; }

    /// <summary>The binding revision observed by the bind stage.</summary>
    public long ExpectedBindingRevision { get; }

    /// <summary>The store-stamped effective instant for a definition revision.</summary>
    public DateTimeOffset? DefinitionEffectiveAt { get; }

    /// <summary>The tenant that declared the definition, or null for package/platform definitions.</summary>
    public TenantId? DeclaringTenantId { get; }
}

/// <summary>Package boundary for validated authorization configuration persistence.</summary>
public interface IAuthorizationConfigurationStore
{
    /// <summary>Commits only an opaque value produced by validate-stage admission.</summary>
    ValueTask CommitAsync(
        ValidatedAuthorizationConfigurationWrite write,
        CancellationToken ct = default);

    /// <summary>
    /// Commits an installer bootstrap definition only while durable grant history proves that no
    /// Administrator grant has ever existed. Implementations perform the proof inside their write fence.
    /// </summary>
    ValueTask CommitBootstrapAsync(
        ValidatedAuthorizationConfigurationWrite write,
        TenantId tenant,
        IGrantStore grants,
        CancellationToken ct = default);
}
