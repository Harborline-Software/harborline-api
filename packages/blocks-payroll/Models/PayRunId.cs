namespace Harborline.Api.Blocks.Payroll.Models;

/// <summary>Opaque identifier for a <see cref="PayRun"/>.</summary>
public readonly record struct PayRunId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static PayRunId NewId() => new(Guid.NewGuid().ToString("N"));
}
