using System;

using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// An opaque, client-minted session token identifying an <b>unidentified</b> capture
/// session — the key of the short-TTL pre-auth capture buffer (ADR 0135 amendment
/// 2026-07-01 — D2). It is NOT a party or tenant; it exists only so an anonymous
/// in-progress capture can be promoted to a keyed draft on sign-in, or purged.
/// </summary>
public readonly record struct CaptureSessionId
{
    /// <summary>The opaque, non-empty session value.</summary>
    public string Value { get; }

    /// <summary>Constructs a capture-session id. The value must be non-empty.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null/empty/whitespace.</exception>
    public CaptureSessionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A capture-session id must be a non-empty value.", nameof(value));
        }
        Value = value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// One buffered pre-auth capture — an unidentified actor's in-progress form values,
/// held briefly (<see cref="ExpiresAt"/>) until identification promotes it to a keyed
/// draft or a sweep purges it. Deliberately ephemeral: unidentified PII must never
/// linger in durable, ungoverned storage.
/// </summary>
/// <param name="Session">The anonymous capture session this entry belongs to.</param>
/// <param name="FormId">The form definition being filled.</param>
/// <param name="Case">The client-mintable case id the promoted draft will key by.</param>
/// <param name="Provenance">The schema / definition / engine / locale binding in force at capture.</param>
/// <param name="Body">The serialized partial candidate values (UTF-8 JSON object bytes).</param>
/// <param name="SubjectId">The optional data subject the values pertain to.</param>
/// <param name="CapturedAt">When the entry was buffered.</param>
/// <param name="ExpiresAt">When the entry expires and a sweep must purge it (no lingering PII).</param>
public sealed record PreAuthCapture(
    CaptureSessionId Session,
    FormDefinitionId FormId,
    DraftCaseId Case,
    SubmissionDraftProvenance Provenance,
    ReadOnlyMemory<byte> Body,
    string? SubjectId,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt);
