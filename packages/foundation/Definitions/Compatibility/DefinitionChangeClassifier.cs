namespace Harborline.Api.Foundation.Definitions.Compatibility;

/// <summary>The policy class assigned to a change between definition versions.</summary>
public enum DefinitionChangeKind
{
    /// <summary>The candidate accepts every value accepted by the previous version.</summary>
    Widening = 0,

    /// <summary>The candidate rejects some values accepted by the previous version.</summary>
    Narrowing = 1,

    /// <summary>A stable field key has been assigned a different meaning.</summary>
    Incompatible = 2,

    /// <summary>The change cannot be classified safely and must park.</summary>
    Parked = 3,
}

/// <summary>A value domain whose inclusion relationship is known to the classifier.</summary>
public enum DefinitionValueKind
{
    /// <summary>Unformatted text.</summary>
    Text = 0,

    /// <summary>Text constrained to an email-address format.</summary>
    Email = 1,
}

/// <summary>The compatibility-relevant shape of one definition field.</summary>
/// <param name="Key">The stable field key.</param>
/// <param name="Meaning">The stable semantic identity of the field.</param>
/// <param name="ValueKind">The accepted value domain.</param>
public sealed record DefinitionFieldShape(string Key, string Meaning, DefinitionValueKind ValueKind);

/// <summary>The compatibility-relevant field set of one definition version.</summary>
/// <param name="Fields">Fields keyed by their stable definition key.</param>
public sealed record DefinitionShape(IReadOnlyList<DefinitionFieldShape> Fields);

/// <summary>The classification assigned to one changed field.</summary>
/// <param name="Field">The stable field key.</param>
/// <param name="Kind">The field-level change class.</param>
public sealed record DefinitionFieldChange(string Field, DefinitionChangeKind Kind);

/// <summary>The aggregate classification and its field-level evidence.</summary>
/// <param name="Kind">The policy class governing the complete version change.</param>
/// <param name="Fields">The changed fields and their individual classes.</param>
public sealed record DefinitionChangeClassification(
    DefinitionChangeKind Kind,
    IReadOnlyList<DefinitionFieldChange> Fields);

/// <summary>Classifies a change between two definition versions through one policy-neutral seam.</summary>
public interface IDefinitionChangeClassifier
{
    /// <summary>Classifies <paramref name="candidate"/> relative to <paramref name="previous"/>.</summary>
    DefinitionChangeClassification Classify(DefinitionShape previous, DefinitionShape candidate);
}

/// <summary>Default fail-closed definition change classifier.</summary>
public sealed class DefinitionChangeClassifier : IDefinitionChangeClassifier
{
    /// <inheritdoc />
    public DefinitionChangeClassification Classify(DefinitionShape previous, DefinitionShape candidate)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(candidate);

        var previousByKey = Index(previous.Fields);
        var candidateByKey = Index(candidate.Fields);
        if (previousByKey is null || candidateByKey is null)
        {
            return new DefinitionChangeClassification(
                DefinitionChangeKind.Parked,
                Array.Empty<DefinitionFieldChange>());
        }

        var changes = new List<DefinitionFieldChange>();
        foreach (var key in previousByKey.Keys
            .Union(candidateByKey.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!previousByKey.TryGetValue(key, out var oldField) ||
                !candidateByKey.TryGetValue(key, out var newField))
            {
                changes.Add(new DefinitionFieldChange(key, DefinitionChangeKind.Parked));
                continue;
            }

            var kind = ClassifyField(oldField, newField);
            if (kind is { } changed)
            {
                changes.Add(new DefinitionFieldChange(key, changed));
            }
        }

        var aggregate = changes.Select(change => change.Kind).OrderByDescending(Precedence).FirstOrDefault();
        return new DefinitionChangeClassification(aggregate, changes);
    }

    private static Dictionary<string, DefinitionFieldShape>? Index(IReadOnlyList<DefinitionFieldShape>? fields)
    {
        if (fields is null)
        {
            return null;
        }

        var result = new Dictionary<string, DefinitionFieldShape>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Key) || !result.TryAdd(field.Key, field))
            {
                return null;
            }
        }

        return result;
    }

    private static DefinitionChangeKind? ClassifyField(
        DefinitionFieldShape previous,
        DefinitionFieldShape candidate)
    {
        if (string.IsNullOrWhiteSpace(previous.Meaning) ||
            string.IsNullOrWhiteSpace(candidate.Meaning) ||
            !Enum.IsDefined(previous.ValueKind) ||
            !Enum.IsDefined(candidate.ValueKind))
        {
            return DefinitionChangeKind.Parked;
        }

        if (!StringComparer.Ordinal.Equals(previous.Meaning, candidate.Meaning))
        {
            return DefinitionChangeKind.Incompatible;
        }

        if (previous.ValueKind == candidate.ValueKind)
        {
            return null;
        }

        return (previous.ValueKind, candidate.ValueKind) switch
        {
            (DefinitionValueKind.Email, DefinitionValueKind.Text) => DefinitionChangeKind.Widening,
            (DefinitionValueKind.Text, DefinitionValueKind.Email) => DefinitionChangeKind.Narrowing,
            _ => DefinitionChangeKind.Parked,
        };
    }

    private static int Precedence(DefinitionChangeKind kind) => kind switch
    {
        DefinitionChangeKind.Parked => 4,
        DefinitionChangeKind.Incompatible => 3,
        DefinitionChangeKind.Narrowing => 2,
        _ => 1,
    };
}
