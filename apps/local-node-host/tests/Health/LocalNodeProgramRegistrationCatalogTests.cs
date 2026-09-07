using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Harborline.Api.LocalNodeHost.Capabilities;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Pins the REAL registration set in <c>Program.cs</c> against the component catalog.
///
/// Why this exists (earlier repository ticket #3403): every other catalog test builds a SYNTHETIC service collection
/// from the catalog itself, so it can only ever agree with the catalog. That tautology let
/// <c>HostedErpnextImportPreviewApiEndpoint</c> ship in #2350 with no catalog entry; the
/// <c>registration_unexpected</c> validator landed the next day in #2662 and the production host
/// could not boot for two weeks. Nothing noticed, because the validator runs at host startup and
/// no test starts the host.
///
/// This test reads what <c>Program.cs</c> actually registers and asserts the catalog accounts for
/// it. It is deliberately source-level: the invariant being protected is "the registrations a human
/// wrote in Program.cs are all catalogued", and reading the source is the most direct expression of
/// that. It cannot be satisfied by a change that only edits the catalog.
/// </summary>
public sealed class LocalNodeProgramRegistrationCatalogTests
{
    [Fact]
    public void Every_AddHostedService_In_Program_Is_Declared_In_The_Catalog()
    {
        var program = ReadProgramSource();

        var registered = Regex
            .Matches(program, @"AddHostedService<\s*([A-Za-z0-9_.]+)\s*>")
            .Select(match => match.Groups[1].Value)
            .Select(name => name.Split('.').Last())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(registered);

        var catalogued = LocalNodeHostedComponentCatalog.Operational
            .Select(item => item.ComponentType.Name)
            .Concat(LocalNodeHostedComponentCatalog.EndpointRegistrars.Select(item => item.RegistrarType.Name))
            .ToHashSet(StringComparer.Ordinal);

        var unaccounted = registered.Where(name => !catalogued.Contains(name)).ToArray();

        Assert.True(
            unaccounted.Length == 0,
            "Program.cs registers hosted components the catalog does not declare, so the host will " +
            "refuse to start with local-node.hosted-component.registration_unexpected: " +
            string.Join(", ", unaccounted) +
            ". Add each to LocalNodeHostedComponentCatalog (and bump PinnedEndpointRegistrarCount) " +
            "rather than loosening the validator.");
    }

    /// <summary>
    /// The REVERSE direction, and the one that was missing: every component the catalog DECLARES must
    /// actually be registered somewhere in <c>Program.cs</c>.
    ///
    /// Why (earlier repository ticket #3448): the catalog is the fleet's statement of what the host runs. The forward
    /// check above catches a registration with no catalog entry — the #3442 defect, which stopped the
    /// host booting. It cannot catch the opposite: a catalogued component that nothing registers. That
    /// component simply never runs, silently, and the host starts perfectly. Deleting the founder
    /// membership attach's registration left all 1,656 tests green, which is how this gap was found.
    ///
    /// The synthetic catalog tests cannot cover this either — they build their service collection FROM
    /// the catalog, so a catalogued component is registered by construction. Third instance of the same
    /// tautology in one day.
    /// </summary>
    [Fact]
    public void Every_Catalogued_Component_Is_Registered_In_Program()
    {
        var program = ReadProgramSource();

        // Only components WE register. A framework type (the health-check publisher) is registered by
        // the framework's own AddHealthChecks(), so asserting our composition names it would be wrong.
        var catalogued = LocalNodeHostedComponentCatalog.Operational
            .Select(item => item.ComponentType)
            .Concat(LocalNodeHostedComponentCatalog.EndpointRegistrars.Select(item => item.RegistrarType))
            .Where(type => type.Assembly.GetName().Name?.StartsWith("Harborline", StringComparison.Ordinal) == true)
            .Select(type => type.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(catalogued);

        // A component is registered EITHER directly in Program.cs, OR by a composition extension that
        // Program.cs calls. Following that ONE HOP is what makes this catch a removed registration:
        // the extension still exists and still names the type, but if Program.cs no longer calls it,
        // the component never runs. Requiring only that the name appear somewhere would pass in that
        // case and flag five correctly-registered components besides.
        var appRoot = LocateAppRoot();
        var unregistered = new List<string>();

        foreach (var name in catalogued)
        {
            if (RegistersHostedService(program, name))
            {
                continue;
            }

            // Search the app AND the packages beside it: two of the catalogued guards are registered
            // by a foundation package's own composition extension, not by anything under the app.
            var searchRoots = new List<string> { appRoot };
            var packages = Path.Combine(Directory.GetParent(appRoot)?.Parent?.FullName ?? appRoot, "packages");
            if (Directory.Exists(packages))
            {
                searchRoots.Add(packages);
            }

            var reachable = false;
            foreach (var file in searchRoots.SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var source = File.ReadAllText(file);
                if (!RegistersHostedService(source, name))
                {
                    continue;
                }

                foreach (Match extension in Regex.Matches(source, @"static\s+IServiceCollection\s+(Add[A-Za-z0-9_]+)"))
                {
                    if (program.Contains(extension.Groups[1].Value, StringComparison.Ordinal))
                    {
                        reachable = true;
                        break;
                    }
                }

                if (reachable)
                {
                    break;
                }
            }

            if (!reachable)
            {
                unregistered.Add(name);
            }
        }

        Assert.True(
            unregistered.Count == 0,
            "The catalog declares hosted components that Program.cs neither registers directly nor " +
            "reaches through a composition it calls. They will never run, the host will start cleanly, " +
            "and nothing else will notice:\n  " +
            string.Join("\n  ", unregistered));
    }

    /// <summary>
    /// The three forms a hosted component is registered in, all of which are legitimate:
    /// <c>AddHostedService&lt;T&gt;()</c>, and either
    /// <c>ServiceDescriptor.Singleton&lt;IHostedService, T&gt;()</c> or
    /// <c>AddSingleton&lt;IHostedService, T&gt;()</c> — the password-hashing guards use the
    /// <c>TryAddEnumerable(ServiceDescriptor.Singleton&lt;IHostedService, T&gt;(…))</c> shape
    /// deliberately, because <c>TryAddEnumerable</c> deduplicates on the implementation type.
    /// Matching only the first form would report correctly-registered components as missing.
    /// </summary>
    private static bool RegistersHostedService(string source, string typeName)
    {
        var name = Regex.Escape(typeName);
        return Regex.IsMatch(source, @"AddHostedService<\s*(?:[A-Za-z0-9_.]+\.)?" + name + @"\s*>")
            || Regex.IsMatch(source, @"(?:ServiceDescriptor\.|(?:Try)?Add)Singleton<\s*IHostedService\s*,\s*(?:[A-Za-z0-9_.]+\.)?" + name + @"\s*>");
    }

    /// <summary>
    /// Locates <c>Program.cs</c> by walking up from the test assembly. The candidate bases below
    /// deliberately never include a bare "bin": that matches the TEST project's own bin directory
    /// and short-circuits the walk before it reaches the app (a trap this repo has hit before).
    /// </summary>
    private static string ReadProgramSource() =>
        File.ReadAllText(Path.Combine(LocateAppRoot(), "Program.cs"));

    /// <summary>
    /// The app project directory. Candidates deliberately never include a bare "bin": that matches the
    /// TEST project's own bin and short-circuits the walk before it reaches the app.
    /// </summary>
    private static string LocateAppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        var candidates = new[]
        {
            Path.Combine("apps", "local-node-host"),
            "local-node-host",
        };

        while (directory is not null)
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory.FullName, candidate);
                if (File.Exists(Path.Combine(path, "Program.cs")))
                {
                    return path;
                }
            }

            if (File.Exists(Path.Combine(directory.FullName, "Program.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Program.cs not found walking up from {AppContext.BaseDirectory}. This test pins the " +
            "real registration set and must not be silently skipped — fix the probe rather than " +
            "deleting the assertion.");
    }
}
