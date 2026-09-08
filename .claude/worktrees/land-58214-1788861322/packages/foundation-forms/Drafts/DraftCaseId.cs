using System;

namespace Harborline.Api.Foundation.Forms.Drafts;

/// <summary>
/// A <b>client-mintable</b> case identifier — the middle element of the D2
/// submission-draft keying tuple <c>(TenantId, case/subject id, PartyId)</c>
/// (ADR 0135 amendment 2026-07-01). The client mints this locally (a GUID or
/// ULID) so <em>starting</em> a save-and-resume draft needs NO server
/// round-trip — the local-first requirement. The id is reconciled with the
/// canonical case/instance later (on submit / sync); until then it is the
/// stable handle a draft resumes by.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not server-minted.</b> A server-allocated id would force a blocking
/// call before the user can begin — smuggling a network round-trip into "open
/// a form". A client-minted GUID/ULID is globally unique with negligible
/// collision risk, so the draft is keyed the instant the form opens, offline.
/// </para>
/// <para>
/// <b>Accepted shapes.</b> A canonical <see cref="Guid"/> (any of the standard
/// formats) OR a 26-character Crockford base-32 ULID. Both are compact, stable,
/// and collision-resistant. A value that is neither is rejected by
/// <see cref="Create"/> — a draft key must never carry a free-form / mutable
/// natural string that could collide or drift.
/// </para>
/// </remarks>
public readonly record struct DraftCaseId
{
    /// <summary>The maximum accepted length (a formatted GUID with braces is 38).</summary>
    private const int MaxLength = 40;

    /// <summary>The canonical opaque value (never null/empty once constructed via the factory).</summary>
    public string Value { get; }

    private DraftCaseId(string value) => Value = value;

    /// <summary>
    /// Validates and constructs a case id from a client-minted string. Accepts a
    /// canonical <see cref="Guid"/> or a 26-char Crockford base-32 ULID.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is null/empty/whitespace, too long, or is neither a
    /// GUID nor a ULID.
    /// </exception>
    public static DraftCaseId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A draft case id must be a non-empty client-minted GUID or ULID.", nameof(value));
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException(
                $"A draft case id must be at most {MaxLength} characters (a GUID or ULID); was {trimmed.Length}.",
                nameof(value));
        }

        if (!Guid.TryParse(trimmed, out _) && !IsUlid(trimmed))
        {
            throw new ArgumentException(
                "A draft case id must be a canonical GUID or a 26-character Crockford base-32 ULID; " +
                "a free-form string is rejected so a draft key cannot collide or drift.",
                nameof(value));
        }

        return new DraftCaseId(trimmed);
    }

    /// <summary>Attempts to construct a case id; returns false instead of throwing on an invalid value.</summary>
    public static bool TryCreate(string? value, out DraftCaseId caseId)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var trimmed = value.Trim();
            if (trimmed.Length <= MaxLength && (Guid.TryParse(trimmed, out _) || IsUlid(trimmed)))
            {
                caseId = new DraftCaseId(trimmed);
                return true;
            }
        }
        caseId = default;
        return false;
    }

    /// <summary>Mints a fresh case id server-side (a GUID) for callers that did not supply one.</summary>
    public static DraftCaseId NewId() => new(Guid.NewGuid().ToString("D"));

    /// <summary>True when <paramref name="value"/> is a 26-char Crockford base-32 ULID (no I, L, O, U).</summary>
    private static bool IsUlid(string value)
    {
        if (value.Length != 26)
        {
            return false;
        }
        foreach (var ch in value)
        {
            var c = char.ToUpperInvariant(ch);
            var ok = (c >= '0' && c <= '9')
                || (c >= 'A' && c <= 'Z' && c != 'I' && c != 'L' && c != 'O' && c != 'U');
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
