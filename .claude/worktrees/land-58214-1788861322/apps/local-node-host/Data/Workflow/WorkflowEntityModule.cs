using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> mapping the three durable-process-engine tables (ADR 0135 D2) into
/// <see cref="LocalNodeDbContext"/> — <c>workflow_instances</c>, <c>workflow_events</c>,
/// <c>workflow_step_idempotency</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pattern A (shared context), not a separate DbContext.</b> Like <c>AuditEventEntityModule</c>, these
/// tables MUST share <see cref="LocalNodeDbContext"/>'s connection + transaction so a step's effect (a
/// posted JE on the financial tables) and the workflow advance (event + idempotency + position) co-commit
/// in ONE SQLite transaction (ADR 0135 build invariant #1; the same ADR-0126 shared-unit-of-work property
/// the audit enlister uses). A separate context could not join the JE unit-of-work.
/// </para>
/// <para>
/// <b>Host-local module.</b> The durable workflow store is node-resident (the recoverable local-first
/// engine per ADR 0135 D2). The Bridge has no workflow-instance schema, so the C2 both-provider parity
/// obligation does not attach — this is a node system-of-record, registered ONLY on the node.
/// </para>
/// <para>
/// <b>Append-only invariant (ADR 0135 D3).</b> <c>workflow_events</c> is a grow-only log; the
/// <c>(InstanceId, Seq)</c> UNIQUE index makes it totally-ordered per instance. The engine never updates
/// or deletes an event row. <c>workflow_step_idempotency</c> is keyed on the stable
/// <see cref="WorkflowStepKey.Value"/> string, so a duplicate advance is rejected by the PK.
/// </para>
/// <para>
/// <b>SQLite-stable timestamps.</b> <c>DateTimeOffset</c> columns convert to ISO-8601-UTC strings (the
/// same converter the audit module uses) — SQLite cannot <c>ORDER BY</c> a <c>DateTimeOffset</c>, and the
/// string form sorts lexicographically == chronologically and round-trips losslessly.
/// </para>
/// </remarks>
public sealed class WorkflowEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.local-node.workflow";

    private static readonly ValueConverter<DateTimeOffset, string> Iso8601UtcConverter =
        new(v => v.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            v => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal));

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigureInstances(modelBuilder);
        ConfigureEvents(modelBuilder);
        ConfigureIdempotency(modelBuilder);
    }

    private static void ConfigureInstances(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkflowInstanceRecord>(e =>
        {
            e.ToTable("workflow_instances");
            e.HasKey(r => r.Id);

            e.Property(r => r.Id).HasMaxLength(128).IsRequired();
            e.Property(r => r.TenantId).HasMaxLength(256).IsRequired();
            e.Property(r => r.DefinitionKey).HasMaxLength(256).IsRequired();
            // D7 — the definition version pinned at instantiation. Replay resolves against THIS value.
            e.Property(r => r.DefinitionVersion).HasMaxLength(128).IsRequired();
            e.Property(r => r.CurrentStep).HasMaxLength(256).IsRequired();
            // A0 — the instance's durable iteration counter (read back on resume, never recomputed). Bumped
            // atomically with a loop-back park (send-back). Defaults to 0 for a fresh instance + every existing
            // forward-only process; the additive migration backfills existing rows to 0.
            e.Property(r => r.Iteration).HasDefaultValue(0).IsRequired();
            e.Property(r => r.Status).HasConversion<int>().IsRequired();

            // Opaque working state. jsonb on Postgres → TEXT on SQLite (LocalNodeDbContext sweep).
            e.Property(r => r.StateJson)
                .HasColumnName("state_json")
                .HasColumnType("jsonb")
                .IsRequired();

            e.Property(r => r.CreatedAt).HasConversion(Iso8601UtcConverter).HasMaxLength(33).IsRequired();
            e.Property(r => r.UpdatedAt).HasConversion(Iso8601UtcConverter).HasMaxLength(33).IsRequired();

            // Tenant isolation index — the node's defence-in-depth boundary (ADR 0092).
            e.HasIndex(r => r.TenantId).HasDatabaseName("ix_workflow_instances_tenant_id");
            // Status filter (the schedule daemon scans Running/Parked instances).
            e.HasIndex(r => new { r.TenantId, r.Status })
                .HasDatabaseName("ix_workflow_instances_tenant_status");
        });
    }

    private static void ConfigureEvents(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkflowEventRecord>(e =>
        {
            e.ToTable("workflow_events");
            // Global physical autoincrement = the append order.
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedOnAdd();

            e.Property(r => r.InstanceId).HasMaxLength(128).IsRequired();
            e.Property(r => r.Seq).IsRequired();
            e.Property(r => r.Step).HasMaxLength(256).IsRequired();
            e.Property(r => r.EventType).HasMaxLength(256).IsRequired();

            e.Property(r => r.DataJson)
                .HasColumnName("data_json")
                .HasColumnType("jsonb")
                .IsRequired();

            e.Property(r => r.OccurredAt).HasConversion(Iso8601UtcConverter).HasMaxLength(33).IsRequired();

            // THE append-only invariant (ADR 0135 D2): (InstanceId, Seq) is UNIQUE — a per-instance
            // totally-ordered log. A duplicate Seq for the same instance is a store-level error.
            e.HasIndex(r => new { r.InstanceId, r.Seq })
                .IsUnique()
                .HasDatabaseName("ux_workflow_events_instance_seq");
        });
    }

    private static void ConfigureIdempotency(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkflowStepIdempotencyRecord>(e =>
        {
            e.ToTable("workflow_step_idempotency");
            // PK on the stable (instance, iteration, step) key — a duplicate advance is rejected.
            e.HasKey(r => r.Key);

            e.Property(r => r.Key).HasMaxLength(512).IsRequired();
            e.Property(r => r.InstanceId).HasMaxLength(128).IsRequired();
            e.Property(r => r.Step).HasMaxLength(256).IsRequired();
            e.Property(r => r.Iteration).IsRequired();

            e.Property(r => r.ResultJson)
                .HasColumnName("result_json")
                .HasColumnType("jsonb")
                .IsRequired();

            e.Property(r => r.CompletedAt).HasConversion(Iso8601UtcConverter).HasMaxLength(33).IsRequired();

            // Lookup by instance (replay scan).
            e.HasIndex(r => r.InstanceId).HasDatabaseName("ix_workflow_step_idempotency_instance");
        });
    }
}
