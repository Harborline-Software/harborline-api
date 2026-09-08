namespace Harborline.Api.Foundation.Definitions.Compatibility;

using System.Net.Mail;
using System.Text.Json;

/// <summary>A versioned definition body exposed to the publication policy.</summary>
/// <param name="Definition">Stable definition identity.</param>
/// <param name="Version">Candidate or published version.</param>
/// <param name="Shape">Compatibility-relevant definition shape.</param>
public sealed record DefinitionVersion(string Definition, string Version, DefinitionShape Shape);

/// <summary>The lifecycle store used after publication policy admits a version.</summary>
public interface IDefinitionVersionStore
{
    /// <summary>Loads the current published version, or <see langword="null"/> for a new definition.</summary>
    ValueTask<DefinitionVersion?> GetCurrentPublishedAsync(
        string definition,
        CancellationToken cancellationToken = default);

    /// <summary>Loads a specific candidate version.</summary>
    ValueTask<DefinitionVersion> GetAsync(
        string definition,
        string version,
        CancellationToken cancellationToken = default);

    /// <summary>Performs the admitted lifecycle transition.</summary>
    ValueTask PublishAsync(
        string definition,
        string version,
        CancellationToken cancellationToken = default);
}

/// <summary>A stored field value preserved exactly as JSON.</summary>
/// <param name="RawJson">The original JSON representation.</param>
public sealed record DefinitionStoredValue(string RawJson);

/// <summary>A record written under a definition.</summary>
/// <param name="Record">Stable record identity.</param>
/// <param name="Values">Stored values keyed by stable definition field key.</param>
public sealed record DefinitionRecord(
    string Record,
    IReadOnlyDictionary<string, DefinitionStoredValue> Values);

/// <summary>Reads records bound to a definition when a narrowing needs impact analysis.</summary>
public interface IDefinitionRecordSource
{
    /// <summary>Streams records written under any version of <paramref name="definition"/>.</summary>
    IAsyncEnumerable<DefinitionRecord> ReadAsync(
        string definition,
        CancellationToken cancellationToken = default);
}

/// <summary>The author's declared handling for data affected by a narrowing.</summary>
public enum DefinitionDataDispositionKind
{
    /// <summary>Transform values deterministically.</summary>
    Migrate = 0,

    /// <summary>Keep and flag affected values for explicit handling.</summary>
    Flag = 1,

    /// <summary>Explicitly accept affected values as non-conforming.</summary>
    Accept = 2,
}

/// <summary>A supplied narrowing disposition.</summary>
/// <param name="Kind">The declared handling category.</param>
public sealed record DefinitionDataDisposition(DefinitionDataDispositionKind Kind);

/// <summary>The conformance status of a stored value against a candidate definition.</summary>
public enum DefinitionValueConformance
{
    /// <summary>The candidate definition accepts the stored value.</summary>
    Conforming = 0,

    /// <summary>The candidate definition does not accept the stored value.</summary>
    NonConforming = 1,
}

/// <summary>A stored value that the candidate definition does not accept.</summary>
/// <param name="Record">Stable record identity.</param>
/// <param name="Field">Stable field key.</param>
/// <param name="Value">The exact stored value; it is never coerced.</param>
/// <param name="Conformance">The explicit candidate-version conformance state.</param>
public sealed record NonConformingDefinitionValue(
    string Record,
    string Field,
    DefinitionStoredValue Value,
    DefinitionValueConformance Conformance);

/// <summary>The count of records a narrowing leaves non-conforming for one field.</summary>
/// <param name="Field">Stable field key.</param>
/// <param name="NonConformingRecordCount">Number of affected records.</param>
public sealed record DefinitionNarrowingImpact(string Field, int NonConformingRecordCount);

/// <summary>Raised at publish when a narrowing has no declared data disposition.</summary>
public sealed class DefinitionNarrowingRefusedException : InvalidOperationException
{
    /// <summary>Constructs the refusal from field-level impact counts.</summary>
    public DefinitionNarrowingRefusedException(IReadOnlyList<DefinitionNarrowingImpact> impacts)
        : base(BuildMessage(impacts))
    {
        Impacts = impacts ?? throw new ArgumentNullException(nameof(impacts));
    }

    /// <summary>The stable refusal code.</summary>
    public string ErrorCode { get; } = "definition.publish.narrowing_requires_disposition";

    /// <summary>Affected fields and their non-conforming record counts.</summary>
    public IReadOnlyList<DefinitionNarrowingImpact> Impacts { get; }

    private static string BuildMessage(IReadOnlyList<DefinitionNarrowingImpact>? impacts)
    {
        ArgumentNullException.ThrowIfNull(impacts);
        return "Definition narrowing refused without a data disposition: " +
            string.Join(", ", impacts.Select(impact =>
                $"field '{impact.Field}' has {impact.NonConformingRecordCount} non-conforming record(s)")) + ".";
    }
}

/// <summary>Raised when a version bump reuses a field key for a different meaning.</summary>
public sealed class DefinitionIncompatibleChangeException : InvalidOperationException
{
    /// <summary>Constructs the refusal and directs the author to mint new fields.</summary>
    public DefinitionIncompatibleChangeException(IReadOnlyList<string> fields)
        : base(BuildMessage(fields))
    {
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
    }

    /// <summary>The stable refusal code.</summary>
    public string ErrorCode { get; } = "definition.publish.incompatible_requires_new_field";

    /// <summary>The stable keys whose meanings changed.</summary>
    public IReadOnlyList<string> Fields { get; }

    private static string BuildMessage(IReadOnlyList<string>? fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return $"Incompatible definition version bump refused for field(s) " +
            $"[{string.Join(", ", fields)}]; create a new field for the new meaning.";
    }
}

/// <summary>Raised when publication parks because classification is not crisp.</summary>
public sealed class DefinitionChangeParkedException : InvalidOperationException
{
    /// <summary>Constructs the parked outcome with its complete classification evidence.</summary>
    public DefinitionChangeParkedException(DefinitionChangeClassification classification)
        : base("Definition publication parked because the change could not be classified safely.")
    {
        Classification = classification ?? throw new ArgumentNullException(nameof(classification));
    }

    /// <summary>The stable parked code.</summary>
    public string ErrorCode { get; } = "definition.publish.change_parked";

    /// <summary>The fail-closed classification and affected fields.</summary>
    public DefinitionChangeClassification Classification { get; }
}

/// <summary>The observable outcome of an admitted publish.</summary>
/// <param name="Classification">The classification that selected publication policy.</param>
/// <param name="NonConformingValues">Affected values surfaced without coercion.</param>
public sealed record DefinitionPublishResult(
    DefinitionChangeClassification Classification,
    IReadOnlyList<NonConformingDefinitionValue> NonConformingValues);

/// <summary>Publishes a definition only after its classified-change policy admits it.</summary>
public interface IDefinitionPublisher
{
    /// <summary>Attempts to publish a candidate version with an optional narrowing disposition.</summary>
    ValueTask<DefinitionPublishResult> PublishAsync(
        string definition,
        string candidateVersion,
        DefinitionDataDisposition? disposition = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Default fail-closed definition publisher.</summary>
public sealed class DefinitionPublisher : IDefinitionPublisher
{
    private readonly IDefinitionVersionStore _versions;
    private readonly IDefinitionRecordSource _records;
    private readonly IDefinitionChangeClassifier _classifier;

    /// <summary>Constructs the publisher over explicit version, record, and classification seams.</summary>
    public DefinitionPublisher(
        IDefinitionVersionStore versions,
        IDefinitionRecordSource records,
        IDefinitionChangeClassifier classifier)
    {
        _versions = versions ?? throw new ArgumentNullException(nameof(versions));
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
    }

    /// <inheritdoc />
    public async ValueTask<DefinitionPublishResult> PublishAsync(
        string definition,
        string candidateVersion,
        DefinitionDataDisposition? disposition = null,
        CancellationToken cancellationToken = default)
    {
        var previous = await _versions.GetCurrentPublishedAsync(definition, cancellationToken).ConfigureAwait(false);
        var candidate = await _versions.GetAsync(definition, candidateVersion, cancellationToken).ConfigureAwait(false);
        var classification = previous is null
            ? new DefinitionChangeClassification(DefinitionChangeKind.Widening, Array.Empty<DefinitionFieldChange>())
            : _classifier.Classify(previous.Shape, candidate.Shape);

        if (classification.Kind == DefinitionChangeKind.Narrowing)
        {
            var nonConforming = await FindNonConformingAsync(
                definition,
                candidate.Shape,
                classification,
                cancellationToken).ConfigureAwait(false);
            if (disposition is null)
            {
                var impacts = classification.Fields
                    .Where(field => field.Kind == DefinitionChangeKind.Narrowing)
                    .Select(field => new DefinitionNarrowingImpact(
                        field.Field,
                        nonConforming.Count(value => StringComparer.Ordinal.Equals(value.Field, field.Field))))
                    .ToArray();
                throw new DefinitionNarrowingRefusedException(impacts);
            }

            await _versions.PublishAsync(definition, candidateVersion, cancellationToken).ConfigureAwait(false);
            return new DefinitionPublishResult(classification, nonConforming);
        }

        if (classification.Kind == DefinitionChangeKind.Incompatible)
        {
            throw new DefinitionIncompatibleChangeException(
                classification.Fields
                    .Where(field => field.Kind == DefinitionChangeKind.Incompatible)
                    .Select(field => field.Field)
                    .ToArray());
        }

        if (classification.Kind == DefinitionChangeKind.Parked)
        {
            throw new DefinitionChangeParkedException(classification);
        }

        await _versions.PublishAsync(definition, candidateVersion, cancellationToken).ConfigureAwait(false);
        return new DefinitionPublishResult(classification, Array.Empty<NonConformingDefinitionValue>());
    }

    private async ValueTask<IReadOnlyList<NonConformingDefinitionValue>> FindNonConformingAsync(
        string definition,
        DefinitionShape candidate,
        DefinitionChangeClassification classification,
        CancellationToken cancellationToken)
    {
        var narrowed = classification.Fields
            .Where(field => field.Kind == DefinitionChangeKind.Narrowing)
            .ToDictionary(
                field => field.Field,
                field => candidate.Fields.Single(candidateField =>
                    StringComparer.Ordinal.Equals(candidateField.Key, field.Field)),
                StringComparer.Ordinal);
        var result = new List<NonConformingDefinitionValue>();

        await foreach (var record in _records.ReadAsync(definition, cancellationToken).ConfigureAwait(false))
        {
            foreach (var (field, shape) in narrowed)
            {
                if (record.Values.TryGetValue(field, out var value) && !Conforms(value, shape.ValueKind))
                {
                    result.Add(new NonConformingDefinitionValue(
                        record.Record,
                        field,
                        value,
                        DefinitionValueConformance.NonConforming));
                }
            }
        }

        return result;
    }

    private static bool Conforms(DefinitionStoredValue value, DefinitionValueKind kind)
    {
        try
        {
            using var document = JsonDocument.Parse(value.RawJson);
            return kind switch
            {
                DefinitionValueKind.Text => document.RootElement.ValueKind == JsonValueKind.String,
                DefinitionValueKind.Email =>
                    document.RootElement.ValueKind == JsonValueKind.String &&
                    IsEmail(document.RootElement.GetString()),
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsEmail(string? value)
        => value is not null &&
            MailAddress.TryCreate(value, out var address) &&
            StringComparer.Ordinal.Equals(address.Address, value);
}
