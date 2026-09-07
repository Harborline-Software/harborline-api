using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Foundation.Authorization;

internal enum DefinitionAuthorityKind
{
    Tenant,
    PlatformBootstrap,
    VendorPackage,
}

internal sealed record DefinitionAuthorityClassification(
    CascadeLayer Layer,
    RoleGatedDefinitionOwner Owner);

/// <summary>The single authority-to-owner classification used by every role-gated definition writer.</summary>
internal static class DefinitionAuthorityClassifier
{
    internal const string LayerMismatchCode = "authorization.definition_provenance.layer_mismatch";

    internal static DefinitionAuthorityClassification Classify(
        DefinitionAuthorityKind authorityKind,
        TenantId tenant,
        CascadeLayer claimedLayer,
        string? packageId = null)
    {
        var classification = authorityKind switch
        {
            DefinitionAuthorityKind.PlatformBootstrap => new DefinitionAuthorityClassification(
                CascadeLayer.Base,
                new RoleGatedDefinitionOwner(RoleGatedDefinitionOwnerKind.Platform, tenant)),
            DefinitionAuthorityKind.VendorPackage => new DefinitionAuthorityClassification(
                CascadeLayer.Pack,
                new RoleGatedDefinitionOwner(
                    RoleGatedDefinitionOwnerKind.VendorPackage,
                    tenant,
                    RequiredPackageId(packageId))),
            _ => new DefinitionAuthorityClassification(
                CascadeLayer.Tenant,
                new RoleGatedDefinitionOwner(RoleGatedDefinitionOwnerKind.Tenant, tenant)),
        };

        // Tenant is the authored-model default. A trusted authority replaces that default; any
        // affirmative, conflicting layer is a provenance claim and is refused before persistence.
        if (claimedLayer != CascadeLayer.Tenant && claimedLayer != classification.Layer)
        {
            throw new DefinitionProvenanceException(
                LayerMismatchCode,
                classification.Layer,
                claimedLayer,
                packageId);
        }

        return classification;
    }

    internal static DefinitionAuthorityClassification FromStored(
        TenantId tenant,
        CascadeLayer layer,
        string? packageId = null) => layer switch
        {
            CascadeLayer.Base => Classify(DefinitionAuthorityKind.PlatformBootstrap, tenant, layer),
            CascadeLayer.Pack => Classify(DefinitionAuthorityKind.VendorPackage, tenant, layer, packageId),
            _ => Classify(DefinitionAuthorityKind.Tenant, tenant, layer),
        };

    private static string RequiredPackageId(string? packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        return packageId;
    }
}

/// <summary>Raised when authored definition metadata contradicts its writer authority.</summary>
public sealed class DefinitionProvenanceException : InvalidOperationException
{
    internal DefinitionProvenanceException(
        string code,
        CascadeLayer expected,
        CascadeLayer actual,
        string? packageId)
        : base(
            $"Definition provenance refused: {code}; expected={expected}; actual={actual}"
            + (packageId is null ? "." : $"; package={packageId}."))
    {
        Code = code;
        Expected = expected;
        Actual = actual;
        PackageId = packageId;
    }

    public string Code { get; }
    public CascadeLayer Expected { get; }
    public CascadeLayer Actual { get; }
    public string? PackageId { get; }
}
