using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Governance.Resolution;

/// <summary>Six independently inherited governance defaults (ADR 0087).</summary>
public sealed record CascadeValues(
    IReadOnlyList<Tag>? Classification = null,
    bool? PersonalData = null,
    CascadeMasking? Masking = null,
    RetentionRequirement? Retention = null,
    string? ConflictPolicy = null,
    bool? TrackChanges = null);

public sealed record CascadeMasking(int RevealLast);
public sealed record CascadeDeclaration(string? RecordType, string? Field, CascadeValues Values);
public sealed record CascadeDefaults(int SchemaVersion, string Title, IReadOnlyList<CascadeDeclaration> Defaults);
public sealed record CascadeSource(TenantId Tenant, string PackId, string PackVersion, string ContentKey,
    string ContentVersion, bool TenantOverride);
public sealed record ResolvedCascadeDefaults(CascadeValues Values, IReadOnlyDictionary<string, CascadeSource> Sources);

/// <summary>Reads only defaults admitted into the active runtime projection.</summary>
public interface ICascadeDefaultsProjection
{
    ResolvedCascadeDefaults Resolve(TenantId tenant, string package, string recordType, string field);
}
