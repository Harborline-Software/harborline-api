using System.Collections.Immutable;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>One scoped delegation requirement and the exact closure snapshot the gate evaluated.</summary>
public sealed record AuthorizationGrantAtomEvidence(
    PermissionAtom Required,
    bool Covered,
    ImmutableArray<AuthorizationAtomDerivation> Bindings,
    ImmutableArray<AuthorizationExcludedBinding> Excluded);

/// <summary>Immutable evidence of every delegation requirement, including an explicitly empty role.</summary>
public sealed record AuthorizationGrantAttenuationEvidence(ImmutableArray<AuthorizationGrantAtomEvidence> Atoms);
