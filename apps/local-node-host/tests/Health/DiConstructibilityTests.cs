using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Every type registered in the DI container must be constructible BY the container.
///
/// Why this exists: on 2026-07-30 the dogfood host would not start, dying on
/// <c>WebTenantSelectionAuthority</c> with "a suitable constructor could not be located". The
/// constructor was <c>internal</c>. Microsoft's container selects constructors with
/// <c>Type.GetConstructors()</c>, which returns PUBLIC instance constructors only — so an
/// internal constructor on a registered type can never be resolved, whatever its parameters are.
///
/// SEVEN types were affected, the whole ADR 0160 selected-audience identity layer. None of it had
/// ever been DI-constructible, which is the mechanical reason behind earlier repository ticket #3329 ("no member can
/// sign in") and earlier repository ticket #3311 ("the Team &amp; access panel is unreachable in production"): the
/// routes were registered and could never have served a request.
///
/// Why the tests could not catch it: every test constructs these types with <c>new</c>, which
/// honours <c>internal</c> inside the assembly. Nothing anywhere resolved one through a provider.
/// The tests exercised a construction path production never uses.
///
/// SCOPE: every <c>Add(Singleton|Scoped|Transient)&lt;…&gt;</c> and <c>AddHostedService&lt;T&gt;</c>
/// registration written anywhere under <c>apps/local-node-host</c> — Program.cs and the ~30
/// composition extension methods alike. <c>AddHostedService&lt;T&gt;</c> reduces to
/// <c>Singleton&lt;IHostedService, T&gt;</c> and is constructed under the identical rule, so omitting
/// it would have missed hosted actors. Endpoint mappers are constructed explicitly at the
/// composition root and are covered by the composed-host boot tests instead.
///
/// NOT covered: factory-lambda registrations (<c>AddSingleton(sp =&gt; …)</c>), which the container
/// does not construct, and open generics. It needs no dependency stubs: a public constructor is
/// exactly and only what constructor selection requires. It cannot catch a MISSING registration —
/// only the container's own <c>ValidateOnBuild</c> does that, and it is currently Development-only
/// (see the followup card).
/// </summary>
public sealed class DiConstructibilityTests
{
    [Fact]
    public void Every_Di_Registered_Type_Exposes_A_Public_Constructor()
    {
        var appRoot = LocateAppRoot();
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            registered.UnionWith(RegisteredImplementationTypeNames(File.ReadAllText(file)));
        }

        // A DEGRADED parse — not merely an empty one — is the real vacuity risk: the service-type
        // pattern cannot match a generic service type, and the seven types this test was written for
        // sit in one contiguous block. Reformat that block and a count-only floor still passes while
        // the assertion has quietly stopped covering them. Assert the canaries are present by NAME.
        foreach (var canary in new[]
                 {
                     "WebTenantSelectionAuthority", "WebTenantSwitchAuthority", "WebSelectedSessionStore",
                     "WebSelectedSessionIdentityAuthority", "WebSelectedSessionLogoutAuthority",
                     "WebSelectedSessionPrincipalAuthority", "InstallationIdentityCoordinatorService",
                 })
        {
            Assert.True(
                registered.Contains(canary),
                $"the registration parse no longer sees {canary}. The check has narrowed silently — " +
                "fix the parser rather than dropping the canary.");
        }

        var assembly = typeof(Harborline.Api.LocalNodeHost.Capabilities.LocalNodeHostedComponentProfile).Assembly;
        var offenders = new List<string>();

        foreach (var name in registered.OrderBy(x => x, StringComparer.Ordinal))
        {
            var type = assembly.GetTypes().FirstOrDefault(t => t.Name == name && !t.IsInterface && !t.IsAbstract);
            if (type is null) continue;                       // generic/aliased registration, or not ours
            if (type.GetConstructors().Length > 0) continue;   // public instance ctor -> resolvable

            var nonPublic = type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
            // Defensive: shapes with no declared instance constructor at all. A class with none gets
            // an implicit PUBLIC parameterless one and has already exited above, so this is not the
            // factory-registered case — those are not parsed here in the first place.
            if (nonPublic.Length == 0) continue;

            offenders.Add($"{type.FullName} (has only {string.Join("/", nonPublic.Select(c => c.IsAssembly ? "internal" : c.IsPrivate ? "private" : "protected"))} constructors)");
        }

        Assert.True(
            offenders.Count == 0,
            "These types are registered in the DI container but expose no PUBLIC constructor, so the " +
            "container cannot construct them and the host dies at startup with 'a suitable constructor " +
            "could not be located'. Making the constructor public widens nothing outside the assembly " +
            "when the class itself is internal:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>Implementation type names from Add/TryAdd registrations in Program.cs.</summary>
    private static HashSet<string> RegisteredImplementationTypeNames(string source)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        // AddSingleton<IService, Implementation>()
        foreach (Match m in Regex.Matches(source, @"(?:Try)?Add(?:Singleton|Scoped|Transient)<\s*[A-Za-z0-9_.]+\s*,\s*([A-Za-z0-9_.]+)\s*>"))
        {
            names.Add(Simple(m.Groups[1].Value));
        }

        // AddSingleton<Implementation>() — no factory lambda, no second type argument
        foreach (Match m in Regex.Matches(source, @"(?:Try)?Add(?:Singleton|Scoped|Transient)<\s*([A-Za-z0-9_.]+)\s*>\s*\(\s*\)"))
        {
            names.Add(Simple(m.Groups[1].Value));
        }

        // AddHostedService<T>() — reduces to Singleton<IHostedService, T>, same constructor rule.
        foreach (Match m in Regex.Matches(source, @"AddHostedService<\s*([A-Za-z0-9_.]+)\s*>"))
        {
            names.Add(Simple(m.Groups[1].Value));
        }

        return names;
    }

    private static string Simple(string name) =>
        name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;

    /// <summary>
    /// Walks up to the app project. Candidates never include a bare "bin": that matches the TEST
    /// project's own bin and short-circuits before reaching the app (a trap this repo has hit).
    /// </summary>
    private static string LocateAppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var candidate in new[] { Path.Combine("apps", "local-node-host"), "local-node-host" })
            {
                var path = Path.Combine(directory.FullName, candidate);
                if (File.Exists(Path.Combine(path, "Program.cs"))) return path;
            }
            if (File.Exists(Path.Combine(directory.FullName, "Program.cs"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"local-node-host Program.cs not found walking up from {AppContext.BaseDirectory}. " +
            "Fix the probe rather than deleting the assertion.");
    }
}
