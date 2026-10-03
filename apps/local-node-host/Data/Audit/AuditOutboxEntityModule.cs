using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// T-1048 (DES-0029 ck-6): maps <c>search_audit_outbox</c> into <see cref="LocalNodeDbContext"/>, so a write that
/// commits through this context (a spatial frame mint or quarantine, a <c>NodeEntityWriter</c> write) stages its
/// <see cref="NodeAuditOutbox"/> entry in the same save. The search context owns the table and its migrations;
/// this context only writes rows into it, so the table is excluded from this context's migrations.
/// </summary>
public sealed class AuditOutboxEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.local-node.audit-outbox";

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Search.AuditOutboxRow>(e =>
        {
            Search.AuditOutboxRow.Map(e);
            e.ToTable("search_audit_outbox", table => table.ExcludeFromMigrations());
        });
    }
}
