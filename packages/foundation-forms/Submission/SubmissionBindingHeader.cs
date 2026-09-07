using System.Text.Json;

using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// The mandatory, near-free binding header stamped on every form submission
/// instance (ADR 0140 amendment 2026-07-01 — decision D3). The "prove what was
/// submitted, under which schema + rules + locale" record.
/// </summary>
/// <remarks>
/// <para>
/// <b>D3 doctrine.</b> The canonical immutable submission payload is
/// <em>final values only</em> (a Compute-action output is itself a final value,
/// so it is included). This header is the small, always-present provenance record
/// that binds those final values to the exact schema, definition revision, rule
/// engine, and locale chain in force at submit time. It is deliberately cheap: a
/// handful of identifiers and a timestamp — no field data, no PII.
/// </para>
/// <para>
/// <b>Co-committed and audited.</b> <c>FormEngine</c> persists this header with the
/// submission entity in the same mint operation, then repeats it in the
/// <c>Op.Mint</c> audit record it already emits on save (INV-S4). The record can
/// therefore resolve its own definition even when the audit append faults, while
/// the audit chain retains the independent provenance envelope.
/// </para>
/// <para>
/// <b>Immutable by construction.</b> The record is <c>sealed</c> with init-only
/// members and <see cref="Create"/> takes a defensive copy of the locale chain, so
/// a caller mutating the source list after construction cannot alter a stamped
/// header.
/// </para>
/// </remarks>
/// <param name="SchemaRef">The content-addressed CID of the JSON Schema the
/// submission validated against (<see cref="FormDefinition.SchemaRef"/>). Pins the
/// exact structural contract — content-addressed, so it is tamper-evident.</param>
/// <param name="DefinitionId">The form definition id (<see cref="FormDefinition.Id"/>).</param>
/// <param name="DefinitionVersion">The canonical <c>"{major}.{minor}.{patch}"</c>
/// version of the definition revision that produced this submission.</param>
/// <param name="EngineVersion">The rule/compute engine identifier in force
/// (<see cref="HarborlineJsonLogicV1"/> = <c>"shipyard-jsonlogic/v1"</c>). Pins which
/// evaluator produced any Compute-action final values, so a later replay resolves
/// against the same operator semantics.</param>
/// <param name="LocaleChain">The ordered actor locale-preference chain (RFC 5646
/// tags) in force at submit time. Locale is outcome-affecting (formatting,
/// collation, i18n resolution), so it is part of the "under which rules" record.</param>
/// <param name="SubmittedAt">The UTC instant the submission was persisted.</param>
public sealed record SubmissionBindingHeader(
    string SchemaRef,
    string DefinitionId,
    string DefinitionVersion,
    string EngineVersion,
    IReadOnlyList<string> LocaleChain,
    DateTimeOffset SubmittedAt)
{
    /// <summary>
    /// The canonical SPINE-1 rule/compute engine identifier (ADR 0140 D1 — the one
    /// <c>shipyard-jsonlogic/v1</c> engine that does both logic and compute). Mirrors
    /// the interpreter name in <c>Harborline.Api.Foundation.RuleEngine</c>; kept as a local
    /// constant so this keystone stays free of a rule-engine dependency.
    /// </summary>
    public const string HarborlineJsonLogicV1 = "shipyard-jsonlogic/v1";

    /// <summary>
    /// Builds a binding header from the resolved definition, the actor locale chain,
    /// and the submit instant. Takes a defensive copy of <paramref name="localeChain"/>
    /// so the header is immutable against later mutation of the source list.
    /// </summary>
    /// <param name="definition">The published definition the submission bound to.</param>
    /// <param name="localeChain">The ordered actor locale-preference chain.</param>
    /// <param name="submittedAt">The UTC submit instant.</param>
    /// <param name="engineVersion">The engine identifier; defaults to
    /// <see cref="HarborlineJsonLogicV1"/>.</param>
    public static SubmissionBindingHeader Create(
        FormDefinition definition,
        IReadOnlyList<string> localeChain,
        DateTimeOffset submittedAt,
        string engineVersion = HarborlineJsonLogicV1)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(localeChain);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineVersion);

        return new SubmissionBindingHeader(
            SchemaRef: definition.SchemaRef.Value,
            DefinitionId: definition.Id.Value,
            DefinitionVersion: definition.Version.ToString(),
            EngineVersion: engineVersion,
            // Defensive copy → immutability: a later mutation of the caller's list
            // cannot alter a stamped header.
            LocaleChain: localeChain.ToArray(),
            SubmittedAt: submittedAt);
    }

    /// <summary>
    /// Writes this header as a JSON object onto <paramref name="writer"/> (camelCase,
    /// ISO-8601 timestamp) so it can ride the audit envelope's payload document.
    /// </summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteString("schemaRef", SchemaRef);
        writer.WriteString("definitionId", DefinitionId);
        writer.WriteString("definitionVersion", DefinitionVersion);
        writer.WriteString("engineVersion", EngineVersion);
        writer.WriteStartArray("localeChain");
        foreach (var locale in LocaleChain)
        {
            writer.WriteStringValue(locale);
        }
        writer.WriteEndArray();
        writer.WriteString("submittedAt", SubmittedAt);
        writer.WriteEndObject();
    }
}
