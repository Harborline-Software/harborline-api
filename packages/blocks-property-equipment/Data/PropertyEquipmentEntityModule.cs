using Microsoft.EntityFrameworkCore;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.PropertyEquipment.Data;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> contribution for the property-equipment
/// block. Applies every <c>IEntityTypeConfiguration&lt;T&gt;</c> declared in
/// this assembly to the shared Bridge <c>DbContext</c> (see ADR 0015).
/// </summary>
public sealed class PropertyEquipmentEntityModule : IHarborlineEntityModule
{
    /// <summary>The stable module key registered by this block.</summary>
    public const string Key = "harborline.blocks.property-equipment";

    /// <inheritdoc />
    public string ModuleKey => Key;

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PropertyEquipmentEntityModule).Assembly);
    }
}
