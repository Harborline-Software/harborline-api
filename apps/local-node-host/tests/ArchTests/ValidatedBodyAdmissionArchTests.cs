using System.Reflection;
using System.Runtime.CompilerServices;

using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 151 slice 2 (ledger L1418) — the write token is unforgeable, and the named path that mints
/// one for an already-validated body is an exact, reviewed set.
/// </summary>
public sealed class ValidatedBodyAdmissionArchTests
{
    private sealed record AllowRow(string Path, string Reason);

    /// <summary>
    /// Every production file that mints a token for a body its own engine validated. Reaching this
    /// path already requires holding the unregistered raw mutation port
    /// (<see cref="IEntityMutationStore"/>), which <c>RawMutationPortSymbolInventoryTests</c> fences.
    /// </summary>
    private static readonly AllowRow[] OwnValidatedMinters =
    [
        new("packages/foundation/Assets/Entities/ValidatedBody.cs",
            "the admission itself declares the path"),
        new("packages/foundation/Definitions/EntityStoreDefinitionLifecycle.cs",
            "definition-envelope transitions: the lifecycle serialized the envelope under a schema the record registry does not hold"),
        new("packages/foundation-forms/AuthorizedFormDefinitionLifecycle.cs",
            "form-definition envelope, frozen and checked by FormDefinitionValidation"),
        new("packages/blocks-workflow/src/durable/AuthorizedWorkflowDefinitionLifecycle.cs",
            "workflow-definition envelope, admitted by IWorkflowAdmissionValidator"),
        new("packages/foundation-forms-engine/IAuthorizedFormEntityWriter.cs",
            "form instance: FormEngine validated the submission against ISchemaRegistry before field protection made it ciphertext"),
    ];

    internal static string[] OwnValidatedMinterRows() =>
        OwnValidatedMinters.Select(row => row.Path).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Production files that name the own-validated mint, discovered from source.</summary>
    internal static string[] DiscoveredOwnValidatedMinters() =>
        EnumerateProductionSource()
            .Where(file => Mints(File.ReadAllText(file.Absolute)))
            .Select(file => file.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact(DisplayName = "Ticket 151: the own-validated mint has exactly the reviewed call sites")]
    public void OwnValidatedMintSitesEqualTheReviewedSet()
    {
        Assert.Equal(OwnValidatedMinterRows(), DiscoveredOwnValidatedMinters());
        Assert.All(OwnValidatedMinters, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason)));
    }

    [Fact(DisplayName = "Ticket 151: only the admission can construct a ValidatedBody")]
    public void ValidatedBodyHasNoProductionReachableConstructor()
    {
        var constructors = typeof(ValidatedBody).GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.All(constructors, c => Assert.False(c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly));

        // …and the foundation assembly does not hand the internal constructor to anyone else.
        Assert.DoesNotContain(
            typeof(ValidatedBody).Assembly
                .GetCustomAttributes<InternalsVisibleToAttribute>()
                .Select(attribute => attribute.AssemblyName),
            name => name.Contains("Forms", StringComparison.Ordinal)
                || name.Contains("Workflow", StringComparison.Ordinal)
                || name.Contains("LocalNodeHost", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Ticket 151: the mutation port accepts only a token, never a raw body")]
    public void MutationPortTakesTheTokenNotAJsonDocument()
    {
        foreach (var name in new[] { "CreateAsync", "UpdateAsync" })
        {
            var method = typeof(IEntityMutationStore).GetMethod(name)!;
            Assert.Contains(method.GetParameters(), p => p.ParameterType == typeof(ValidatedBody));
            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(System.Text.Json.JsonDocument));
        }
    }

    /// <summary>The own-validated mint, directly or through the definition lifecycle's named wrapper.</summary>
    private static bool Mints(string source) =>
        source.Contains("AdmitOwnValidated", StringComparison.Ordinal)
        || source.Contains("AdmitEnvelope(", StringComparison.Ordinal);

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
