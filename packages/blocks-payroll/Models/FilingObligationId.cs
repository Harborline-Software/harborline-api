namespace Harborline.Api.Blocks.Payroll.Models;

/// <summary>Opaque identifier for a <see cref="FilingObligation"/>.</summary>
public readonly record struct FilingObligationId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static FilingObligationId NewId() => new(Guid.NewGuid().ToString("N"));
}
