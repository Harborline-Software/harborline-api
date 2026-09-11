using System.Reflection;

using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 366 review note 6: production can supply only the compiled record validator or the
/// registered null object. A new implementation could otherwise turn a reviewed mint site into a bypass.
/// </summary>
public sealed class EntityValidatorImplementationFenceTests
{
    internal sealed record AllowRow(string TypeName, string Reason);

    /// <summary>Every production <see cref="IEntityValidator"/> implementation is reviewed explicitly.</summary>
    internal static readonly AllowRow[] AllowedImplementers =
    [
        new("Harborline.Api.Kernel.Schema.CompiledSchemaEntityValidator",
            "the compiled validator bound to the record-write keyed service"),
        new("Harborline.Api.Foundation.Assets.Entities.NullEntityValidator",
            "the registered fallback null object for non-record envelope writers"),
    ];

    internal static string[] AllowedImplementerNames() =>
        AllowedImplementers.Select(row => row.TypeName).Order(StringComparer.Ordinal).ToArray();

    internal static string[] DiscoveredImplementers() =>
        ServiceRegistrationExtensionSpellingArchTests.ProductionAssemblyFiles().Values
            .Select(Assembly.LoadFrom)
            .SelectMany(Types)
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(IEntityValidator).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact(DisplayName = "Ticket 366 A6: production IEntityValidator implementations are the reviewed set")]
    public void ProductionImplementersEqualTheReviewedAllowList()
    {
        Assert.Equal(AllowedImplementerNames(), DiscoveredImplementers());
        Assert.All(AllowedImplementers, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason)));
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { return exception.Types.OfType<Type>(); }
    }
}
