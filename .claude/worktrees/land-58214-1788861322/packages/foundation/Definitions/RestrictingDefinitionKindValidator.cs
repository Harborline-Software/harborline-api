namespace Harborline.Api.Foundation.Definitions;

/// <summary>The restricting definition vocabularies governed by ADR 0038.</summary>
public enum RestrictingDefinitionKindFamily
{
    /// <summary>A rule action kind.</summary>
    RuleAction,

    /// <summary>A policy effect kind.</summary>
    Policy,

    /// <summary>A retention-policy kind.</summary>
    Retention,
}

/// <summary>One stable, visible refusal for an unknown restricting definition kind.</summary>
public sealed record RestrictingDefinitionKindRefusal(
    string Code,
    string DefinitionId,
    string UnknownKind,
    string Message,
    string? PackageKey = null,
    string? NestedDefinitionId = null);

/// <summary>Thrown by compiled writers when an unknown restricting kind refuses their write.</summary>
public sealed class UnknownRestrictingDefinitionKindException : Exception
{
    /// <summary>The stable refusal carried to an HTTP or install boundary.</summary>
    public RestrictingDefinitionKindRefusal Refusal { get; }

    /// <summary>Constructs the exception from the canonical refusal.</summary>
    public UnknownRestrictingDefinitionKindException(RestrictingDefinitionKindRefusal refusal)
        : base((refusal ?? throw new ArgumentNullException(nameof(refusal))).Message)
        => Refusal = refusal;
}

/// <summary>
/// The single validator for closed restricting-kind vocabularies. Presentation definitions are
/// intentionally absent: ADR 0038 permits an unknown permitting kind to remain inert.
/// </summary>
public interface IRestrictingDefinitionKindValidator
{
    /// <summary>Returns a refusal when <paramref name="kind"/> is outside the named closed vocabulary.</summary>
    RestrictingDefinitionKindRefusal? Validate(
        RestrictingDefinitionKindFamily family,
        string definitionId,
        string kind,
        string? packageKey = null,
        string? nestedDefinitionId = null);

    /// <summary>Throws <see cref="UnknownRestrictingDefinitionKindException"/> when the kind is unknown.</summary>
    void EnsureKnown(
        RestrictingDefinitionKindFamily family,
        string definitionId,
        string kind,
        string? packageKey = null,
        string? nestedDefinitionId = null);
}

/// <summary>Default closed-vocabulary implementation of <see cref="IRestrictingDefinitionKindValidator"/>.</summary>
public sealed class RestrictingDefinitionKindValidator : IRestrictingDefinitionKindValidator
{
    /// <summary>Stable machine code shared by route-write and pack-install refusals.</summary>
    public const string KindUnknownCode = "definition.restricting_kind_unknown";

    /// <summary>The process-wide default used by compiled writers and registered into DI by pack composition.</summary>
    public static RestrictingDefinitionKindValidator Shared { get; } = new();

    private static readonly IReadOnlyDictionary<RestrictingDefinitionKindFamily, HashSet<string>> Known =
        new Dictionary<RestrictingDefinitionKindFamily, HashSet<string>>
        {
            [RestrictingDefinitionKindFamily.RuleAction] = Set(
                "Visibility", "Required", "ReadOnly", "Validate", "Compute", "Presentation", "Options",
                "0", "1", "2", "3", "4", "5", "6"),
            [RestrictingDefinitionKindFamily.Policy] = Set(
                "Encrypt", "Redact", "Mask", "Audit", "Retain", "Erase", "Reside", "Consent",
                "0", "1", "2", "3", "4", "5", "6", "7"),
            [RestrictingDefinitionKindFamily.Retention] = Set(
                "Custom", "HipaaInformedDefault", "PciDssInformedDefault", "Soc2InformedDefault",
                "GdprInformedDefault", "EuAiActInformedDefault", "0", "1", "2", "3", "4", "5"),
        };

    /// <inheritdoc />
    public RestrictingDefinitionKindRefusal? Validate(
        RestrictingDefinitionKindFamily family,
        string definitionId,
        string kind,
        string? packageKey = null,
        string? nestedDefinitionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        kind ??= string.Empty;

        if (Known[family].Contains(kind))
        {
            return null;
        }

        var familyLabel = family switch
        {
            RestrictingDefinitionKindFamily.RuleAction => "rule action",
            RestrictingDefinitionKindFamily.Policy => "policy",
            RestrictingDefinitionKindFamily.Retention => "retention",
            _ => "restricting definition",
        };
        var packagePrefix = packageKey is null ? string.Empty : $"Package '{packageKey}' ";
        var nested = nestedDefinitionId is null ? string.Empty : $" (nested definition '{nestedDefinitionId}')";
        var message = $"{packagePrefix}definition '{definitionId}'{nested} has unknown restricting "
            + $"{familyLabel} kind '{kind}'; the write is refused.";

        return new RestrictingDefinitionKindRefusal(
            KindUnknownCode, definitionId, kind, message, packageKey, nestedDefinitionId);
    }

    /// <inheritdoc />
    public void EnsureKnown(
        RestrictingDefinitionKindFamily family,
        string definitionId,
        string kind,
        string? packageKey = null,
        string? nestedDefinitionId = null)
    {
        var refusal = Validate(family, definitionId, kind, packageKey, nestedDefinitionId);
        if (refusal is not null)
        {
            throw new UnknownRestrictingDefinitionKindException(refusal);
        }
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);
}
