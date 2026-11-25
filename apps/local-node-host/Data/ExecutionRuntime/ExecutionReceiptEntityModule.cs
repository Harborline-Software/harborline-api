using Harborline.Api.Foundation.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.ExecutionRuntime;

/// <summary>The current receipt for a tenant's kind-qualified run.</summary>
public sealed class ExecutionReceiptRow
{
    /// <summary>The owning tenant.</summary>
    public required string TenantId { get; init; }
    /// <summary>The registered run kind.</summary>
    public required string RunKind { get; init; }
    /// <summary>The run's opaque value.</summary>
    public Guid RunValue { get; init; }
    /// <summary>The platform status wire name.</summary>
    public required string Status { get; set; }
    /// <summary>The fixed, opaque Access decision identity, when applicable.</summary>
    public string? AccessDecisionId { get; init; }
}

/// <summary>One immutable step in a tenant's kind-qualified run trace.</summary>
public sealed class ExecutionTraceStepRow
{
    /// <summary>The owning tenant.</summary>
    public required string TenantId { get; init; }
    /// <summary>The registered run kind.</summary>
    public required string RunKind { get; init; }
    /// <summary>The run's opaque value.</summary>
    public Guid RunValue { get; init; }
    /// <summary>The consecutive 1-based step number.</summary>
    public int Ordinal { get; init; }
    /// <summary>The engine's phase, verbatim.</summary>
    public required string Phase { get; init; }
    /// <summary>The engine's phase status, verbatim.</summary>
    public required string Status { get; init; }
}

/// <summary>The execution runtime's durable evidence in the node's shared database model.</summary>
public sealed class ExecutionReceiptEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.local-node.execution-runtime";

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ExecutionReceiptRow>(entity =>
        {
            entity.ToTable("execution_receipts");
            entity.HasKey(row => new { row.TenantId, row.RunKind, row.RunValue });
        });
        modelBuilder.Entity<ExecutionTraceStepRow>(entity =>
        {
            entity.ToTable("execution_trace_steps");
            entity.HasKey(row => new { row.TenantId, row.RunKind, row.RunValue, row.Ordinal });
        });
    }
}
