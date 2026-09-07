using System;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// Stable identifier for a <see cref="LegalHoldEntry"/>. A release
/// (<see cref="LegalHoldRelease"/>) references the hold it ends by this id — the
/// hold is never mutated or deleted (append-only, ADR 0142 §D1).
/// </summary>
public readonly record struct LegalHoldId
{
    /// <summary>The opaque, non-empty id value.</summary>
    public string Value { get; }

    /// <summary>Construct a hold id. The value must be non-empty.</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is null/empty/whitespace.</exception>
    public LegalHoldId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("LegalHoldId must be a non-empty value.", nameof(value));
        }
        Value = value;
    }

    /// <summary>Allocate a fresh, unique hold id.</summary>
    public static LegalHoldId New() => new(Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public override string ToString() => Value;
}
