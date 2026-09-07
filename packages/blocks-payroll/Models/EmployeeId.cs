namespace Harborline.Api.Blocks.Payroll.Models;

/// <summary>Opaque identifier for an <see cref="Employee"/>.</summary>
public readonly record struct EmployeeId(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Generates a new unique id backed by <see cref="Guid"/>.</summary>
    public static EmployeeId NewId() => new(Guid.NewGuid().ToString("N"));
}
