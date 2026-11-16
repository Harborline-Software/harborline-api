using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>Validate-stage admission for authorization capability definitions.</summary>
public sealed class AuthorizationDefinitionAdmission(IRoleVocabularyReader roles)
{
    /// <summary>The stable refusal code for a pack binding that widens the platform seed's reviewed offer.</summary>
    public const string SeedCeilingExceededCode = "authorization.definition.pack_exceeds_seed_ceiling";

    /// <summary>
    /// (L675) Whether the definition offers a role the platform seed's REVIEWED offer for that operation
    /// does not. The seed's table is the hand-reviewed ceiling for every platform operation, and a pack's
    /// content is not reviewed by anyone, so a pack may narrow a platform operation's offer and never
    /// widen it — otherwise a signed pack could confer <c>grant:permissions</c> on a role it invented and
    /// mint its own authority. When the seed defines no offer the operation is the pack's OWN, and the
    /// pack is then its own ceiling (false here); the platform vocabulary does not yet admit a pack-owned
    /// operation, so that branch is the rule waiting for it rather than a live path.
    /// </summary>
    internal static bool ExceedsReviewedCeiling(AuthorizationCapabilityDefinition definition) =>
        AccessGrantAuthorizationSeed.ReviewedOfferFor(definition.Operation) is { } reviewed
        && !definition.OfferedRoles.IsSubsetOf(reviewed);

    /// <summary>
    /// Validates code-operation, atom, role ownership, replacement, and the Auditor invariant. A write
    /// marked <c>packPublished</c> is a pack projection, and is additionally held to the platform seed's
    /// reviewed ceiling.
    /// </summary>
    public async ValueTask AdmitAsync(
        AuthorizationCapabilityDefinition definition,
        TenantId? declaringTenantId,
        AuthorizationCapabilityDefinition? previous,
        bool packPublished = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.PublisherPackageId);
        ArgumentNullException.ThrowIfNull(definition.OfferedRoles);

        if (!PermissionVocabulary.Contains(definition.Operation))
        {
            throw new InvalidOperationException(
                $"Operation '{definition.Operation}' is not implemented by the code operation catalogue.");
        }

        if (definition.Atom.Operation != definition.Operation)
        {
            throw new InvalidOperationException("The declared atom operation must equal the definition operation.");
        }

        if (definition.Revision < 1)
        {
            throw new InvalidOperationException("Authorization definition revisions start at one.");
        }

        if (previous is null)
        {
            if (definition.Revision != 1)
            {
                throw new InvalidOperationException("A new authorization definition must start at revision one.");
            }
        }
        else
        {
            if (definition.DefinitionId != previous.DefinitionId
                || definition.PublisherPackageId != previous.PublisherPackageId
                || definition.Revision != previous.Revision + 1)
            {
                throw new InvalidOperationException(
                    "A replacement must retain definition identity and publisher and advance one revision.");
            }

            if (!definition.OfferedRoles.IsSubsetOf(previous.OfferedRoles))
            {
                throw new InvalidOperationException("A publisher replacement cannot add an offered role.");
            }
        }

        foreach (var roleReference in definition.OfferedRoles.Roles)
        {
            var role = await roles.ResolveAsync(roleReference, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Offered role '{roleReference}' is not installed.");

            if (roleReference.Vocabulary == RoleVocabularies.Platform)
            {
                if (!role.IsSealed || role.Owner.Kind != RoleOwnerKind.Platform)
                {
                    throw new InvalidOperationException(
                        $"Platform role '{roleReference}' must be sealed and platform-owned.");
                }

                continue;
            }

            var packageOwned = role.Owner.Kind == RoleOwnerKind.Package
                && string.Equals(role.Owner.OwnerId, definition.PublisherPackageId, StringComparison.Ordinal);
            var tenantOwned = declaringTenantId is { } tenant
                && role.Owner.Kind == RoleOwnerKind.Tenant
                && string.Equals(role.Owner.OwnerId, tenant.Value, StringComparison.Ordinal);
            if (roleReference.Vocabulary != RoleVocabularies.Domain || (!packageOwned && !tenantOwned))
            {
                throw new InvalidOperationException(
                    $"Offered role '{roleReference}' is not owned by the declaring package or tenant.");
            }
        }

        // Ticket 217 / L628: an Auditor offer is admitted only for audit:read published by
        // harborline.platform. This clause checks the publisher and operation, not an id or count.
        // Dropping Auditor remains a permitted narrowing.
        if (definition.OfferedRoles.Roles.Contains(RoleReference.Auditor)
            && !(string.Equals(definition.Operation.Value, Permission.AuditRead, StringComparison.Ordinal)
                && string.Equals(
                    definition.PublisherPackageId,
                    "harborline.platform",
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The platform Auditor role is offered only by the platform '{Permission.AuditRead}' definition.");
        }

        // (L675) The general form of the clause above: the platform seed's reviewed offer is the ceiling
        // for every operation the platform defines, and a pack's content is reviewed by nobody. Without
        // this, a signed pack shipping {"operation":"grant:permissions","offeredRoles":["tax.roles/clerk"]}
        // plus its own `clerk` role would confer the authority to issue grants on a role it invented --
        // and the same shape reaches org:transfer-ownership.
        // A pack may still NARROW a platform offer, and it is its own ceiling for its own operations.
        if (packPublished && ExceedsReviewedCeiling(definition))
        {
            throw new InvalidOperationException(
                $"{SeedCeilingExceededCode}: a pack's offered roles for '{definition.Operation.Value}' must be a "
                + "subset of the platform seed's reviewed offer for that operation.");
        }
    }
}
