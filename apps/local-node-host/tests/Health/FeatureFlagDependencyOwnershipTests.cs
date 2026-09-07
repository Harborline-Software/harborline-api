using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Harborline.Api.LocalNodeHost.Capabilities;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// A feature flag must never be the SOLE registrar of a service the base profile needs.
///
/// Why this exists — and a CORRECTION, 2026-07-31. This docstring shipped with earlier repository ticket #3444
/// claiming <c>TimeProvider</c> "had exactly one registration in this host — inside
/// <c>NodeSchedulingComposition</c>", which is feature-flagged, and that the host therefore died at
/// startup with the flag off. **That account is false.**
/// <c>NodeFinancialPostingComposition.AddNodeFinancialPosting</c> has registered
/// <c>TimeProvider</c> unconditionally since earlier repository ticket #1166, and Program.cs calls it at top level, so
/// the clock was never flag-owned. Verified by mutation: deleting the root registration leaves a
/// valid graph and a host that still boots and serves <c>/health</c>. The startup failure #3444 was
/// written to explain was earlier repository ticket #3445 — seven identity types with <c>internal</c> constructors.
///
/// The INVARIANT below is still worth enforcing, and is the reason this file stays: a feature flag
/// that solely owns a base-profile dependency is a real defect shape, whether or not it was the one
/// that bit on 2026-07-30. Only the war story was wrong. It is corrected in place rather than
/// deleted, because a plausible false rationale in a test docstring is what the next author reasons
/// from.
///
/// The existing catalog tests could not catch it: they build a SYNTHETIC service collection from
/// the catalog and register component types with none of their dependencies, so they can only ever
/// agree with the catalog. This test looks at dependencies instead.
///
/// WHAT IT CHECKS: for every hosted component active in the BASE profile (all flags off), every
/// constructor parameter type; then, for each composition class Program.cs calls INSIDE a
/// conditional block, every service type that class registers. A type in both sets, with no
/// registration anywhere else, is a flag-owned core dependency and fails.
///
/// WHAT IT DOES NOT CHECK: it is a source-and-reflection analysis, not a host boot. It will not
/// catch a dependency that is unregistered EVERYWHERE (nothing here boots a provider), nor one
/// resolved dynamically rather than through a constructor.
/// </summary>
public sealed class FeatureFlagDependencyOwnershipTests
{
    [Fact]
    public void No_Feature_Flagged_Composition_Solely_Owns_A_Base_Profile_Dependency()
    {
        var appRoot = LocateAppRoot();
        var program = File.ReadAllText(Path.Combine(appRoot, "Program.cs"));

        var conditionalCompositions = ConditionalCompositionClasses(program);
        Assert.NotEmpty(conditionalCompositions); // the analysis is worthless if it finds no flags

        // Every service type registered by each conditionally-invoked composition class.
        var flagOwned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (className, file) in CompositionFiles(appRoot, conditionalCompositions))
        {
            foreach (var type in RegisteredServiceTypes(File.ReadAllText(file)))
            {
                flagOwned.TryAdd(type, className);
            }
        }

        // Anything registered outside those files is not flag-owned, whatever else registers it.
        var elsewhere = new HashSet<string>(StringComparer.Ordinal);
        var flagFiles = CompositionFiles(appRoot, conditionalCompositions).Select(x => x.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            if (flagFiles.Contains(file)) continue;
            foreach (var type in RegisteredServiceTypes(File.ReadAllText(file))) elsewhere.Add(type);
        }

        var required = BaseProfileConstructorDependencies();
        Assert.NotEmpty(required);

        var violations = required
            .Where(dep => flagOwned.ContainsKey(dep) && !elsewhere.Contains(dep))
            .Select(dep => $"{dep} (only registered by {flagOwned[dep]})")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "A feature-flagged composition is the SOLE registrar of a service the base profile's " +
            "hosted components require. With that flag off the host cannot construct them and dies " +
            "at startup. Register these unconditionally (TryAdd, so the flagged composition stays a " +
            "no-op):\n  " + string.Join("\n  ", violations));
    }

    /// <summary>Constructor parameter type names of every hosted component active with all flags off.</summary>
    private static HashSet<string> BaseProfileConstructorDependencies()
    {
        var baseProfile = new LocalNodeHostedComponentProfile(false, false, false);
        var types = LocalNodeHostedComponentCatalog.SelectOperational(baseProfile)
            .Select(item => item.ComponentType)
            .Concat(LocalNodeHostedComponentCatalog.SelectEndpointRegistrars(baseProfile)
                .Select(item => item.RegistrarType));

        var deps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    var t = parameter.ParameterType;
                    deps.Add(t.IsGenericType ? t.GetGenericTypeDefinition().Name.Split('`')[0] : t.Name);
                    foreach (var arg in t.IsGenericType ? t.GetGenericArguments() : Array.Empty<Type>())
                    {
                        deps.Add(arg.Name);
                    }
                }
            }
        }
        return deps;
    }

    /// <summary>Composition class names Program.cs invokes inside an <c>if</c> block (brace-depth tracked).</summary>
    /// <summary>
    /// Composition class names Program.cs invokes inside an <c>if</c> block. Brace depth is tracked
    /// across lines because the opening brace is conventionally on the line AFTER the condition —
    /// closing the region on the same line as the <c>if</c> would find nothing at all.
    /// </summary>
    private static HashSet<string> ConditionalCompositionClasses(string program)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var depth = 0;
        int? entryDepth = null;
        var entered = false;

        foreach (var raw in program.Split('\n'))
        {
            var line = raw.Trim();

            if (entryDepth is null && Regex.IsMatch(line, @"^if\s*\("))
            {
                entryDepth = depth;
                entered = false;
            }

            if (entryDepth is not null && entered)
            {
                foreach (Match m in Regex.Matches(line, @"([A-Za-z0-9_]*Composition)\s*\."))
                {
                    found.Add(m.Groups[1].Value);
                }
            }

            depth += line.Count(c => c == '{') - line.Count(c => c == '}');

            if (entryDepth is not null && !entered && depth > entryDepth) entered = true;
            else if (entryDepth is not null && entered && depth <= entryDepth)
            {
                entryDepth = null;
                entered = false;
            }
        }

        return found;
    }

    private static (string Class, string File)[] CompositionFiles(string appRoot, HashSet<string> classes) =>
        Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => (Class: classes.FirstOrDefault(c => Path.GetFileNameWithoutExtension(file) == c), File: file))
            .Where(x => x.Class is not null)
            .Select(x => (x.Class!, x.File))
            .ToArray();

    /// <summary>Service type names registered by Add/TryAdd calls in one source file.</summary>
    private static IEnumerable<string> RegisteredServiceTypes(string source)
    {
        foreach (Match m in Regex.Matches(source, @"(?:Try)?Add(?:Singleton|Scoped|Transient)<\s*([A-Za-z0-9_.]+)"))
        {
            var name = m.Groups[1].Value;
            yield return name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        }
    }

    /// <summary>
    /// Walks up to the app project. The candidates never include a bare "bin" — that matches the
    /// TEST project's own bin and short-circuits before reaching the app (a trap this repo has hit).
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
