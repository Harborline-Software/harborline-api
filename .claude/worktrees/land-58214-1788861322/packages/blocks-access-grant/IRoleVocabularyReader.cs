using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>Read access to the installed role vocabulary.</summary>
public interface IRoleVocabularyReader
{
    /// <summary>Resolves a qualified role reference, or returns null when absent.</summary>
    ValueTask<RoleDefinition?> ResolveAsync(
        RoleReference role,
        CancellationToken ct = default);

    /// <summary>Lists the installed role vocabulary.</summary>
    ValueTask<IReadOnlyList<RoleDefinition>> ListAsync(CancellationToken ct = default);
}

/// <summary>
/// (L675) Install and withdraw access to the role vocabulary, for the ONE writer that has any:
/// the pack seed projector. A pack ships default role NAMES; the platform seed's own entries are
/// sealed and this seam refuses to touch them.
/// </summary>
public interface IRoleVocabularyStore : IRoleVocabularyReader
{
    /// <summary>
    /// Installs a package- or tenant-owned vocabulary entry. Installing an entry identical to the
    /// resolved one is a no-op, so re-projecting the same pack on every boot is idempotent.
    /// </summary>
    /// <exception cref="InvalidOperationException">The name is already installed differently, or is
    /// a sealed platform entry.</exception>
    ValueTask InstallAsync(RoleDefinition definition, CancellationToken ct = default);

    /// <summary>Removes a package- or tenant-owned vocabulary entry; returns whether one went.</summary>
    /// <exception cref="InvalidOperationException">The entry is a sealed platform one.</exception>
    ValueTask<bool> RemoveAsync(RoleReference role, CancellationToken ct = default);
}
