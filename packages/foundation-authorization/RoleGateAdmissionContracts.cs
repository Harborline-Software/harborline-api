using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>A record-derived standing named by a declarative content gate. Standings are facts, not roles.</summary>
public readonly record struct RecordStandingReference
{
    [JsonConstructor]
    public RecordStandingReference(string name)
        : this(name, "requiredStandings")
    {
    }

    private RecordStandingReference(string name, string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!BareIdentifier.IsMatch(name))
            throw new GateReferenceShapeException(
                GateReferenceShapeCodes.InvalidStanding,
                field,
                name);
        Name = name;
    }

    public string Name { get; }

    public override string ToString() => Name;

    public static RecordStandingReference Parse(string name, string field) => new(name, field);

    private static readonly Regex BareIdentifier = new(
        "^[A-Za-z][A-Za-z0-9._-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}

/// <summary>The ownership regime of a declarative definition.</summary>
public enum RoleGatedDefinitionOwnerKind
{
    Platform = 0,
    VendorPackage = 1,
    Tenant = 2,
}

/// <summary>Ownership and provenance used to admit a role-gated definition.</summary>
public sealed record RoleGatedDefinitionOwner(
    RoleGatedDefinitionOwnerKind Kind,
    TenantId Tenant,
    string? PackageId = null);

/// <summary>One role or standing referenced by a named gate within a definition.</summary>
public sealed record DeclarativeGateReference
{
    private DeclarativeGateReference(
        string gate,
        string subject,
        RoleReference? role,
        RecordStandingReference? standing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        Gate = gate;
        Subject = subject;
        Role = role;
        Standing = standing;
    }

    public string Gate { get; }
    public string Subject { get; }
    public RoleReference? Role { get; }
    public RecordStandingReference? Standing { get; }

    public static DeclarativeGateReference ForRole(string gate, string role) =>
        new(gate, role, ParseRole(role, gate), null);

    public static DeclarativeGateReference ForStanding(string gate, RecordStandingReference standing) =>
        new(gate, standing.Name, null, standing);

    private static RoleReference ParseRole(string value, string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf('/', StringComparison.Ordinal);
        if (separator > 0
            && separator == value.LastIndexOf('/', StringComparison.Ordinal)
            && separator < value.Length - 1)
            return new RoleReference(value[..separator], value[(separator + 1)..]);

        // Compatibility spellings already persisted by the forms surface. They resolve into the one
        // platform vocabulary; this is a wire alias, not a second role catalogue.
        if (value is "Admin" or "Member" or "node:operator") return RoleReference.Administrator;
        if (value is "Viewer") return RoleReference.Auditor;
        throw new GateReferenceShapeException(
            GateReferenceShapeCodes.InvalidRole,
            field,
            value);
    }
}

public static class GateReferenceShapeCodes
{
    public const string InvalidStanding = "authorization.gate_reference.required_standings_invalid";
    public const string InvalidRole = "authorization.gate_reference.required_roles_invalid";
}

public sealed class GateReferenceShapeException(string code, string field, string value)
    : ArgumentException($"Gate reference refused: {code}; field={field}; value={value}.", field)
{
    public string Code { get; } = code;
    public string Field { get; } = field;
    public string Value { get; } = value;
}

/// <summary>The complete validate-stage input for one declarative definition.</summary>
public sealed record RoleGatedDefinition(
    string DefinitionKind,
    string DefinitionId,
    string Version,
    RoleGatedDefinitionOwner Owner,
    IReadOnlyList<DeclarativeGateReference> Gates);

/// <summary>A stable role-gate ownership rule.</summary>
public static class RoleGateAdmissionRules
{
    public const string UnresolvedRole = "unresolved_role";
    public const string PlatformRoleOrStandingOnly = "platform_role_or_standing_only";
    public const string VendorOwnOrTenantRoleOnly = "vendor_own_or_tenant_role_only";
    public const string TenantOrPlatformRoleOnly = "tenant_or_platform_role_only";
    public const string StandingPlatformOnly = "standing_platform_only";
}

/// <summary>One machine-readable authorization-catalogue defect.</summary>
public sealed record RoleGateFinding(
    string Code,
    string DefinitionKind,
    string DefinitionId,
    string Version,
    string Gate,
    string Subject,
    string Rule,
    RoleGatedDefinitionOwnerKind DefinitionOwnerKind,
    string? PackageId,
    string TenantId);

/// <summary>Structural validate-stage admission shared by every declarative-definition writer.</summary>
public interface IRoleGateAdmission
{
    ValueTask AdmitAsync(RoleGatedDefinition definition, CancellationToken ct = default);
    ValueTask<IReadOnlyList<RoleGateFinding>> InspectActiveAsync(CancellationToken ct = default);
}

/// <summary>Raised before publication when a declarative gate violates role ownership.</summary>
public sealed class RoleGateAdmissionException(RoleGateFinding finding)
    : InvalidOperationException(
        $"Role-gated definition refused: {finding.Code}; definition={finding.DefinitionKind}/{finding.DefinitionId}@{finding.Version}; "
        + $"gate={finding.Gate}; role={finding.Subject}; rule={finding.Rule}.")
{
    public RoleGateFinding Finding { get; } = finding;
    public string Code { get; } = finding.Code;
}
