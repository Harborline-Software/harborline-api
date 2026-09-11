using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Entities;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 366 slice 1 — the record-store write seam takes an unforgeable token, so RW-9 is held by the
/// type system rather than by an allow-list of discovered call sites.
/// <para>
/// Adapted from the 151 s2 competition candidate C (<c>ValidatedBodyAdmissionArchTests</c>), narrowed:
/// the token covers ONLY the record seam, so there is no own-validated mint to enumerate and no public
/// <c>AdmitOwnValidated</c> a coordinator could call (the judge's H9 finding against C). The reviewed set
/// is therefore the set of <see cref="ValidatedRecordBody.AdmitAsync"/> call sites.
/// </para>
/// </summary>
public sealed class ValidatedRecordBodyAdmissionArchTests
{
    internal sealed record AllowRow(string Path, string Reason);

    /// <summary>
    /// Every production file that mints a record-write token. Each row binds the keyed record validator
    /// (<c>CompiledSchemaEntityValidator.RecordWriteKey</c>) before it mints, which is why minting here is
    /// the validation stage rather than a way around it.
    /// </summary>
    internal static readonly AllowRow[] MintSites =
    [
        new("apps/local-node-host/Data/Entities/NodeEntityWriter.cs",
            "route and headless-CLI create/update: gate, then the keyed record validator, then the mint"),
        new("apps/local-node-host/Data/Entities/NodeHierarchyCompositeCoordinator.cs",
            "split/merge mint records: composite admission, then the keyed record validator, then the mint"),
    ];

    /// <summary>Rule 12: the per-call-site exception list, held empty.</summary>
    internal static readonly string[] ExceptionRows = [];

    internal static string[] MintSiteRows() =>
        MintSites.Select(row => row.Path).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Production files that name the mint, discovered from source.</summary>
    internal static string[] DiscoveredMintSites() =>
        EnumerateProductionSource()
            .Where(file => Mints(File.ReadAllText(file.Absolute)))
            .Select(file => file.Relative)
            .Where(relative => relative != TokenSourcePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private const string TokenSourcePath = "packages/foundation/Assets/Entities/ValidatedRecordBody.cs";

    [Fact(DisplayName = "Ticket 366: the record-write mint has exactly the reviewed call sites (holds RW-9 RW-H2)")]
    public void OwnValidatedMintSitesEqualTheReviewedSet()
    {
        Assert.Equal(MintSiteRows(), DiscoveredMintSites());
        Assert.All(MintSites, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason)));
        Assert.Empty(ExceptionRows);
    }

    [Fact(DisplayName = "Ticket 366: nothing outside the token's own type can construct one (holds RW-9 RW-H2)")]
    public void ValidatedRecordBodyHasNoReachableConstructor()
    {
        var constructors = typeof(ValidatedRecordBody).GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotEmpty(constructors);
        // Private, not internal: an InternalsVisibleTo friend (forms, workflow, the host tests) still
        // cannot forge one, which is the difference from candidate C's internal constructor.
        Assert.All(constructors, c => Assert.True(c.IsPrivate, $"{c} is not private"));
        Assert.True(typeof(ValidatedRecordBody).IsSealed);

        // H11: no production assembly reaches that constructor by reflection either.
        var reflectors = EnumerateProductionSource()
            .Where(file => File.ReadAllText(file.Absolute) is { } text
                && text.Contains(nameof(ValidatedRecordBody), StringComparison.Ordinal)
                && (text.Contains("Activator.CreateInstance", StringComparison.Ordinal)
                    || text.Contains("GetConstructor", StringComparison.Ordinal)
                    || text.Contains("RuntimeHelpers.GetUninitializedObject", StringComparison.Ordinal)))
            .Select(file => file.Relative)
            .ToArray();
        Assert.Empty(reflectors);
    }

    [Fact(DisplayName = "Ticket 366: the record seam takes the token; the raw seam is not public (holds RW-9 RW-H2)")]
    public void RecordSeamTakesTheTokenAndTheRawSeamIsInternal()
    {
        var port = typeof(IEntityMutationStore);
        foreach (var name in new[] { nameof(IEntityMutationStore.CreateAsync), nameof(IEntityMutationStore.UpdateAsync) })
        {
            var overloads = port.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == name)
                .ToArray();
            Assert.Equal(2, overloads.Length);

            var token = Assert.Single(overloads, m =>
                m.GetParameters().Any(p => p.ParameterType == typeof(ValidatedRecordBody)));
            Assert.True(token.IsPublic, $"{name}(ValidatedRecordBody) must be the public seam");

            var raw = Assert.Single(overloads, m =>
                m.GetParameters().Any(p => p.ParameterType == typeof(JsonDocument)));
            Assert.True(raw.IsAssembly, $"{name}(JsonDocument) must be internal to the envelope writers");
        }

        // The batch default-interface member fans out to the raw seam, so it is internal too.
        var batch = Assert.Single(
            port.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            m => m.Name == nameof(IEntityMutationStore.CreateBatchAsync));
        Assert.True(batch.IsAssembly, "CreateBatchAsync must be internal to the envelope writers");
    }

    [Fact(DisplayName = "Ticket 366: the raw seam is visible only to the named non-record write owners (holds RW-9 RW-H2)")]
    public void RawSeamFriendsAreTheReviewedSet()
    {
        // apps/local-node-host is deliberately absent: that absence is what makes a raw record write a
        // compile error in NodeEntityWriter and NodeHierarchyCompositeCoordinator.
        var friends = typeof(ValidatedRecordBody).Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName.Split(',')[0])
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("Harborline.Api.Foundation.Forms", friends);
        Assert.Contains("Harborline.Api.Foundation.Forms.Engine", friends);
        Assert.Contains("Harborline.Api.Blocks.Workflow", friends);
        Assert.DoesNotContain("Harborline.Api.LocalNodeHost", friends);
    }

    [Fact(DisplayName = "Ticket 366: a raw-body record write does not compile outside the friend set (holds RW-9 RW-H2)")]
    public void RawBodyWriteDoesNotCompile()
    {
        const string snippet = """
            using System.Text.Json;
            using System.Threading;
            using System.Threading.Tasks;
            using Harborline.Api.Foundation.Assets.Common;
            using Harborline.Api.Foundation.Assets.Entities;

            internal static class Bypass
            {
                internal static Task<EntityId> Write(
                    IEntityMutationStore store, SchemaId schema, JsonDocument body, CreateOptions options)
                    => store.CreateAsync(schema, body, options, CancellationToken.None);
            }
            """;

        // "Bypass.Probe" is not in foundation's InternalsVisibleTo set, exactly like Harborline.Api.LocalNodeHost.
        var compilation = CSharpCompilation.Create(
            "Bypass.Probe",
            [CSharpSyntaxTree.ParseText(snippet)],
            ReferenceSet(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.NotEmpty(errors);
        // The raw overload is simply not a candidate, so the token overload is the only one tried.
        Assert.Contains(errors, d => d.Id is "CS1503" or "CS1501" or "CS7036" or "CS1061");

        // Control: the same snippet against the token seam compiles, so the assertion above is about the
        // raw body and not about a broken reference set.
        var admitted = CSharpCompilation.Create(
            "Admitted.Probe",
            [CSharpSyntaxTree.ParseText(snippet.Replace(
                "store.CreateAsync(schema, body, options, CancellationToken.None)",
                "store.CreateAsync(default(ValidatedRecordBody)!, options, CancellationToken.None)",
                StringComparison.Ordinal))],
            ReferenceSet(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(admitted.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    private static MetadataReference[] ReferenceSet() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => a.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(location => (MetadataReference)MetadataReference.CreateFromFile(location))
            .ToArray();

    private static bool Mints(string source) =>
        source.Contains($"{nameof(ValidatedRecordBody)}.{nameof(ValidatedRecordBody.AdmitAsync)}", StringComparison.Ordinal);

    private sealed record SourceFile(string Absolute, string Relative);

    private static IEnumerable<SourceFile> EnumerateProductionSource()
    {
        var root = RepositoryRoot();
        foreach (var area in new[] { "apps", "packages" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, area), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Contains("/tests/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/bin/", StringComparison.Ordinal))
                    continue;
                yield return new SourceFile(path, relative);
            }
        }
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"Could not locate the repository root from '{thisFile}'.");
    }
}
