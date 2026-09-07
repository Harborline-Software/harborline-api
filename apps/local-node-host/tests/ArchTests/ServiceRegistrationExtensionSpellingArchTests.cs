using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 260 slice 6 fence: no public service-registration extension method declared in a
/// <c>Harborline.Api.*</c> assembly carries the retired family word in its name. Discovery is by
/// SYMBOL — reflection over the compiled extension methods whose first parameter is
/// <see cref="IServiceCollection"/> or the builder — because a half-renamed registration still
/// compiles (the old and new spellings are simply two overloads), so the build is not the
/// inventory here; this fence is.
/// <para>
/// Scope note: the fence asserts the absence of the RETIRED spelling, not the presence of a
/// repository-wide <c>AddHarborline</c> prefix. Ticket 260 governs the family word only; the
/// ~90 registrations named for their own subsystem (<c>AddNode*</c>, <c>AddInMemory*</c>,
/// <c>AddBlocks*</c>, …) are outside it and are not offenders.
/// </para>
/// The retired word is reconstructed from code points so this file does not itself spell it.
/// </summary>
public sealed class ServiceRegistrationExtensionSpellingArchTests
{
    private static readonly string Retired =
        new([.. new[] { 83, 104, 105, 112, 121, 97, 114, 100 }.Select(code => (char)code)]);

    /// <summary>
    /// Exact allow-list, one row per discovered site as
    /// <c>assembly:Namespace.Type.Method</c>. Class: SLICE-7-TAIL. <b>Emptied by ticket 260 slice 7</b>,
    /// which renamed the whole Family A tail; every row is asserted against live discovery below, so a
    /// row that outlives its symbol is red, and <see cref="TheSlice7TailAllowList_IsEmpty"/> keeps it
    /// from being silently refilled. A future retired-spelling registration has no excuse left here.
    /// </summary>
    private static readonly string[] Slice7TailAllowList = [];

    internal static string[] Slice7TailAllowListRows() => Slice7TailAllowList;

    /// <summary>The fence's own discovery, allow-list bypassed.</summary>
    internal static string[] DiscoveredRetiredRegistrationSymbols() =>
        [.. RegistrationExtensions()
            .Where(method => CarriesRetiredSpelling(method.Name))
            .Select(method => $"{method.DeclaringType!.Assembly.GetName().Name}:{method.DeclaringType.FullName}.{method.Name}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    internal static bool CarriesRetiredSpelling(string name) =>
        name.Contains(Retired, StringComparison.Ordinal);

    [Fact(DisplayName = "Ticket 260 slice 6: no Harborline.Api.* service-registration extension carries the retired family spelling")]
    public void RegistrationExtensions_DoNotCarryTheRetiredFamilySpelling()
    {
        var allowed = Slice7TailAllowListRows().ToHashSet(StringComparer.Ordinal);
        var offenders = DiscoveredRetiredRegistrationSymbols()
            .Where(row => !allowed.Contains(row))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Service-registration extensions still carrying a retired family spelling ({offenders.Length}):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact(DisplayName = "Ticket 260 slice 6: the slice-7 allow-list equals the set the fence discovers")]
    public void AllowListEqualsTheDiscoveredSetItExcuses()
    {
        Assert.Equal(
            Slice7TailAllowListRows().Order(StringComparer.Ordinal).ToArray(),
            DiscoveredRetiredRegistrationSymbols());
    }

    [Fact(DisplayName = "Ticket 260 slice 6: this slice's own registrations are gone in the retired spelling and present in the new one")]
    public void RenamedRegistrations_ExistOnlyUnderTheNewSpelling()
    {
        string[] renamed =
        [
            "", "KernelRuntime", "KernelSync", "KernelSecurity", "KernelAudit", "KernelAuditReaderInMemory",
            "KernelBuckets", "KernelLedger", "KernelLease", "KernelSchemaRegistry", "KernelEventBus",
            "DeltaRouter", "MultiTeam", "TenantContext", "RecoveryCoordinator",
        ];
        var names = RegistrationExtensions().Select(method => method.Name).ToHashSet(StringComparer.Ordinal);

        Assert.All(renamed, suffix =>
        {
            Assert.DoesNotContain("Add" + Retired + suffix, names);
            Assert.Contains("AddHarborline" + suffix, names);
        });
    }

    [Fact(DisplayName = "Ticket 260 slice 7: the slice-7 tail allow-list is empty — no registration has an excuse")]
    public void TheSlice7TailAllowList_IsEmpty() => Assert.Empty(Slice7TailAllowListRows());

    [Fact(DisplayName = "Ticket 260 slice 6 mutation: the spelling predicate flags the retired name and clears its replacement")]
    public void SpellingPredicate_FlagsTheRetiredNameOnly()
    {
        Assert.True(CarriesRetiredSpelling("Add" + Retired + "MultiTeam"));
        Assert.False(CarriesRetiredSpelling("AddHarborlineMultiTeam"));
    }

    /// <summary>
    /// Every public static extension method on <see cref="IServiceCollection"/> or on the builder
    /// declared by a production <c>Harborline.Api.*</c> assembly.
    /// </summary>
    private static IEnumerable<MethodInfo> RegistrationExtensions()
    {
        var builder = typeof(Foundation.Extensions.HarborlineBuilder);

        foreach (var file in ProductionAssemblyFiles().Values)
        {
            Type[] types;
            try
            {
                types = Assembly.LoadFrom(file).GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.Where(type => type is not null).Cast<Type>()];
            }

            foreach (var method in types
                .Where(type => type.IsSealed && type.IsAbstract && type.IsPublic)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(method => method.IsDefined(typeof(ExtensionAttribute), inherit: false))
                .Where(method => method.GetParameters() is [{ } first, ..]
                    && (first.ParameterType == typeof(IServiceCollection) || first.ParameterType == builder)))
            {
                yield return method;
            }
        }
    }

    /// <summary>
    /// The build configuration of the currently running test assembly (<c>Release</c> under the gate),
    /// from its own <see cref="AssemblyConfigurationAttribute"/>. The fallback fails loudly rather than
    /// guessing, because guessing is how a Debug dll gets reflected over in a Release run.
    /// </summary>
    internal static string TestRunConfiguration =>
        typeof(ServiceRegistrationExtensionSpellingArchTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
        ?? throw new InvalidOperationException(
            "The test assembly declares no AssemblyConfiguration, so the fence cannot tell which "
            + "configuration's build output belongs to this run.");

    /// <summary>
    /// Ticket 260 slice 20. Every production <c>Harborline.Api.*</c> assembly the SOLUTION builds,
    /// as assembly name → dll path.
    /// <para>
    /// Until this slice the fence enumerated <c>AppContext.BaseDirectory</c>, i.e. only what the
    /// test project's reference closure copied to its own output. A package no test project
    /// references was not scanned at all, and nothing said so — the fence looked green because it
    /// had never looked. The inventory is now the solution file: every <c>&lt;Project&gt;</c> in
    /// <c>Harborline.Api.slnx</c> whose <c>&lt;AssemblyName&gt;</c> is a production
    /// <c>Harborline.Api.*</c> name. Each assembly is taken from the test output when it is there
    /// (already loaded, same bits) and otherwise from that project's own newest build output, and
    /// <see cref="EveryProductionAssemblyInTheSolution_IsScanned"/> makes a missing one RED rather
    /// than silently unscanned.
    /// </para>
    /// </summary>
    internal static SortedDictionary<string, string> ProductionAssemblyFiles()
    {
        var root = RepositoryRoot();
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, projectDirectory) in SolutionProductionAssemblies(root))
        {
            var inTestOutput = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            if (File.Exists(inTestOutput))
            {
                found[name] = inTestOutput;
                continue;
            }

            var bin = Path.Combine(projectDirectory, "bin");
            if (!Directory.Exists(bin)) continue;

            // Ticket 260 slice 20 fix 1: pick within THIS run's configuration, never "newest across
            // every configuration and TFM" — a stale bin/Debug/** dll newer than the Release one just
            // built would otherwise be what the fence reflects over. Nothing in the right
            // configuration means nothing is recorded, and EveryProductionAssemblyInTheSolution_IsScanned
            // fails loudly rather than the fence quietly scanning old bits.
            var configurationSegment = $"{Path.DirectorySeparatorChar}{TestRunConfiguration}{Path.DirectorySeparatorChar}";
            var newest = Directory.EnumerateFiles(bin, name + ".dll", SearchOption.AllDirectories)
                .Where(file => file.Contains(configurationSegment, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null) found[name] = newest;
        }

        return found;
    }

    /// <summary>
    /// The solution's own inventory: assembly name → project directory, for every production
    /// <c>Harborline.Api.*</c> project. <c>AssemblyName</c> is declared per csproj in this
    /// repository; the csproj basename is the fallback the SDK itself uses.
    /// </summary>
    internal static SortedDictionary<string, string> SolutionProductionAssemblies(string root)
    {
        var solution = File.ReadAllText(Path.Combine(root, "Harborline.Api.slnx"));
        var projects = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(solution, "Path=\"([^\"]+)\""))
        {
            var relative = match.Groups[1].Value.Replace('\\', '/');
            var project = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(project)) continue;

            var text = File.ReadAllText(project);
            var declared = Regex.Match(text, "<AssemblyName>([^<]+)</AssemblyName>");
            var name = declared.Success
                ? declared.Groups[1].Value.Trim()
                : Path.GetFileNameWithoutExtension(project);

            if (!name.StartsWith("Harborline.Api.", StringComparison.Ordinal)) continue;
            if (name.Contains(".Tests", StringComparison.Ordinal)) continue;

            projects[name] = Path.GetDirectoryName(project)!;
        }

        return projects;
    }

    [Fact(DisplayName = "Ticket 260 slice 20: every production Harborline.Api.* assembly the solution builds is scanned, not just the test project's closure")]
    public void EveryProductionAssemblyInTheSolution_IsScanned()
    {
        var declared = SolutionProductionAssemblies(RepositoryRoot());
        var scanned = ProductionAssemblyFiles();

        Assert.NotEmpty(declared);
        var unscanned = declared.Keys.Where(name => !scanned.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            unscanned.Length == 0,
            $"Production assemblies the solution declares but the fence found no build output for "
            + $"({unscanned.Length} of {declared.Count}); build the solution, or the fence is scanning "
            + $"less than it claims:{Environment.NewLine}" + string.Join(Environment.NewLine, unscanned));
    }

    private static string RepositoryRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harborline.Api.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
