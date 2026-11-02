using Harborline.Blocks.BuilderDefinitions;
using Harborline.Api.Foundation.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Definitions;

/// <summary>Visibility only; the definition and its revisions remain in their existing store.</summary>
public sealed class DefinitionArchiveMark
{
    public required string TenantId { get; init; }
    public DefinitionKind Kind { get; init; }
    public required string DefinitionKey { get; init; }
}

/// <summary>Owns the tenant and family scoped archive marks in the node database.</summary>
public sealed class DefinitionArchiveEntityModule : IHarborlineEntityModule
{
    public string ModuleKey => "harborline.local-node.definition-archives";

    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<DefinitionArchiveMark>(entity =>
        {
            entity.ToTable("definition_archive_marks");
            entity.HasKey(row => new { row.TenantId, row.Kind, row.DefinitionKey });
            entity.Property(row => row.TenantId).IsRequired();
            entity.Property(row => row.DefinitionKey).IsRequired();
        });
    }
}
