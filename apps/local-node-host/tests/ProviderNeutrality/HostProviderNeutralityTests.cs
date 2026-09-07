using System.Reflection;

using Microsoft.Data.Sqlite;

using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.ProviderNeutrality;

/// <summary>Guards provider-neutral boundaries exported by the local-node host.</summary>
public sealed class HostProviderNeutralityTests
{
    [Fact(DisplayName = "Ticket 026: SQLite unique codes become a provider-neutral conflict result")]
    public void SqliteUniqueCode_BecomesProviderNeutralConflictResult()
    {
        var exception = new SqliteException("unique", errorCode: 19, extendedErrorCode: 2067);

        var result = NodePersistenceConflict.Classify(exception);

        Assert.Equal(NodePersistenceConflictKind.Duplicate, result);
    }

    [Fact(DisplayName = "Ticket 026: exported Map seams do not expose EF Core types")]
    public void ExportedMapSeams_DoNotExposeEntityFrameworkCoreTypes()
    {
        var violations = typeof(SharedHostedWebApp).Assembly
            .GetExportedTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "Map")
                .SelectMany(method => method.GetParameters()
                    .Where(parameter => UsesNamespace(parameter.ParameterType, "Microsoft.EntityFrameworkCore"))
                    .Select(parameter => $"{type.FullName}.{method.Name}({parameter.ParameterType})")))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Exported endpoint registration seams expose the persistence provider:\n" +
            string.Join("\n", violations));
    }

    private static bool UsesNamespace(Type type, string namespacePrefix)
    {
        if (type.IsGenericType && type.GetGenericArguments().Any(argument => UsesNamespace(argument, namespacePrefix)))
        {
            return true;
        }

        return type.Namespace?.StartsWith(namespacePrefix, StringComparison.Ordinal) == true;
    }
}
