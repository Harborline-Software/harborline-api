using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// Per-field rendering and authorization hints layered on top of a field's
/// JSON Schema definition (ADR 0055 §"Form rendering").
/// </summary>
/// <remarks>
/// <para>
/// The JSON Schema document carries field types and structural validation;
/// the overlay carries everything the form engine needs that JSON Schema
/// does not natively express:
/// </para>
/// <list type="bullet">
/// <item><description>UI control selection beyond what type alone implies
///   (a "date" field can render as a date picker, a relative-date selector,
///   or a recurrence-rule builder; the overlay disambiguates).</description></item>
/// <item><description>Per-field labels and help text in localized form.</description></item>
/// <item><description>Optional field-level read / write role gates as an
///   escape hatch for cases the section-level posture cannot express.</description></item>
/// <item><description>PII sensitivity classification — fields tagged here
///   are tenant-key-encrypted at rest by the entity store (composes
///   Foundation.Recovery per ADR 0046).</description></item>
/// </list>
/// <para>
/// <see cref="ControlHint"/> values are interpreted by the form engine.
/// Common values:
/// <c>"text"</c>, <c>"textarea"</c>, <c>"date"</c>, <c>"datetime"</c>,
/// <c>"address"</c>, <c>"geo-point"</c>, <c>"taxonomy-coding"</c>,
/// <c>"attachment"</c>, <c>"reference"</c>, <c>"variant"</c>,
/// <c>"recurrence-rule"</c>. The keystone substrate treats them as opaque
/// strings; the engine maps to concrete controls.
/// </para>
/// </remarks>
/// <param name="Label">Localized field label.</param>
/// <param name="HelpText">Optional localized help text shown beneath the
/// control.</param>
/// <param name="ControlHint">Optional opaque control-selection hint
/// interpreted by the form engine.</param>
/// <param name="PiiSensitivity">PII sensitivity classification (informs
/// tenant-key encryption at rest).</param>
/// <param name="FieldReadRoles">Optional field-level read-role override.
/// When non-empty, narrows the section's <see cref="SectionAccess.ReadRoles"/>
/// further for this specific field.</param>
/// <param name="FieldWriteRoles">Optional field-level write-role override.
/// When non-empty, narrows the section's <see cref="SectionAccess.WriteRoles"/>
/// further for this specific field.</param>
/// <param name="Aspects">Optional SPINE-2 (ADR 0140 D2) field-grain aspect overlay
/// (classification tags, access narrowing, lifecycle, discovery). Additive — absent
/// ⇒ byte-identical pre-SPINE-2 behaviour.</param>
/// <param name="Config">Optional per-control config the renderer reads (F-17 field
/// library — currency code, file accept / multiple). Additive — absent (<c>null</c>)
/// ⇒ byte-identical to a pre-F-17 definition.</param>
/// <param name="FieldReadStandings">Optional record-derived facts narrowing reads.</param>
/// <param name="FieldWriteStandings">Optional record-derived facts narrowing writes.</param>
public sealed record FieldOverlay(
    InternationalizedText Label,
    InternationalizedText? HelpText = null,
    string? ControlHint = null,
    PiiSensitivity PiiSensitivity = PiiSensitivity.None,
    IReadOnlyList<string>? FieldReadRoles = null,
    IReadOnlyList<string>? FieldWriteRoles = null,
    AspectOverlay? Aspects = null,
    FieldConfig? Config = null,
    IReadOnlyList<RecordStandingReference>? FieldReadStandings = null,
    IReadOnlyList<RecordStandingReference>? FieldWriteStandings = null);

/// <summary>
/// Per-control config forwarded to the renderer (F-17 field library) — the .NET
/// mirror of the contracts <c>FieldConfig</c>. A typed bag of the settings a
/// control needs beyond its <see cref="FieldOverlay.ControlHint"/>: a currency
/// field's ISO-4217 code, a file field's accept filter + multi-select flag.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strictly additive.</b> A field overlay that carries no <see cref="FieldConfig"/>
/// (<c>null</c>) behaves byte-identically to a pre-F-17 definition. The Harborline App's
/// field-type registry owns WHICH keys each field TYPE may carry and validates a
/// field's config against that schema fail-closed BEFORE persist; this record is the
/// persisted / round-tripped shape (the universe of config keys). Unknown JSON keys
/// deserialize away structurally here — arbitrary config never lands in the store.
/// </para>
/// </remarks>
/// <param name="CurrencyCode"><c>currency</c>: ISO-4217 code (e.g. <c>USD</c>,
/// <c>EUR</c>, <c>AED</c>).</param>
/// <param name="Accept"><c>file</c>: accept filter — a comma-separated MIME-type /
/// extension list.</param>
/// <param name="Multiple"><c>file</c>: whether the control accepts multiple files.</param>
public sealed record FieldConfig(
    string? CurrencyCode = null,
    string? Accept = null,
    bool? Multiple = null);

/// <summary>
/// Sensitivity classification for a field — informs whether the field is
/// stored under tenant-key encryption (ADR 0055 §"Trust impact"; composes
/// Foundation.Recovery per ADR 0046).
/// </summary>
public enum PiiSensitivity
{
    /// <summary>Not sensitive; stored in cleartext in the JSONB payload.</summary>
    None = 0,

    /// <summary>Sensitive (PII / financial / health); tenant-key-encrypted
    /// at rest. The entity-store extension intercepts read / write to
    /// transparently encrypt / decrypt.</summary>
    Sensitive = 1,
}
