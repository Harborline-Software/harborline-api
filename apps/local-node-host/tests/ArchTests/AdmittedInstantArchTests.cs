using System.Reflection;
using System.Runtime.CompilerServices;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-1015 (DES-0029 ck-5 and ck-9, board finding F2): no public path builds a gate request from a raw instant.
/// The request and the write context take only an <see cref="AdmittedInstant"/>; that type is minted from the
/// kernel clock, and every production mint from a raw <see cref="DateTimeOffset"/> (a stored act's recorded
/// instant, or a named owed exemption) is pinned here, so a new one fails until it is reviewed.
/// </summary>
public sealed class AdmittedInstantArchTests
{
    /// <summary>Every production raw-to-admitted mint, by file and factory, with the reviewed reason.</summary>
    private static readonly (string Path, string Kind, int Count, string Reason)[] ReviewedMints =
    [
        ("apps/local-node-host/Data/Configuration/VerificationCandidateWorld.cs", "Exempt", 1,
            "Owed (T-1015 slice 3): the verification candidate world seeds and installs at the fixture's rehearsal instant."),
        ("apps/local-node-host/Data/Configuration/VerificationRunner.cs", "Exempt", 1,
            "Owed (T-1015 slice 3): verification decides the rehearsed create at the fixture's rehearsal instant."),
        ("apps/local-node-host/Data/Identity/EffectiveMemberPermissions.cs", "Exempt", 1,
            "Owed (T-1015 slice 3): roster sync reads install-root authority at the device-signed roster IssuedAt (ADR-0053)."),
        ("apps/local-node-host/Layout/LayoutDenialOutbox.cs", "FromRecordedAct", 1,
            "The appender replays a stored denial at the OccurredAt the server clock wrote into its outbox row."),
        ("packages/foundation-packs/Install/PackProjectionAuthority.cs", "FromRecordedAct", 1,
            "Boot replay of a stored pack projection admission carries the activation instant recorded at install."),
    ];

    /// <summary>The only assemblies that can reach the internal raw mints at all.</summary>
    private static readonly string[] ReviewedFriends =
    [
        "Harborline.Api.Foundation.Authorization.Tests",
        "Harborline.Api.Foundation.Packs",
        "Harborline.Api.LocalNodeHost",
        "Harborline.Api.LocalNodeHost.Tests",
    ];

    [Fact]
    public void AdmittedInstant_IsMintedPubliclyOnlyFromTheKernelClock()
    {
        var type = typeof(AdmittedInstant);
        Assert.DoesNotContain(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            ctor => !ctor.IsPrivate);
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name is "op_Implicit" or "op_Explicit");
        Assert.DoesNotContain(type.GetProperties(), property => property.SetMethod is { IsPrivate: false });

        string[] Factories(Func<MethodInfo, bool> visibility) => type
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.ReturnType == type && visibility(method))
            .Select(Signature)
            .Order(StringComparer.Ordinal)
            .ToArray();
        // A caller outside the reviewed friends can mint only by reading a clock (and production holds only the
        // host's, ticket 216). The raw mints are internal, so no public path builds a request from a raw instant.
        Assert.Equal(["Read(TimeProvider)"], Factories(method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly));
        Assert.Equal(
            ["Exempt(DateTimeOffset,AdmittedInstantExemption)", "FromRecordedAct(DateTimeOffset)"],
            Factories(method => method.IsAssembly));
        Assert.Equal(ReviewedFriends, type.Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .Order(StringComparer.Ordinal)
            .ToArray());
    }

    [Theory]
    [InlineData(typeof(AuthorizationGateRequest))]
    [InlineData(typeof(AuthorizationWriteContext))]
    public void GateRequestAndWriteContext_AcceptNoRawDecisionInstant(Type type)
    {
        var rawConstructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(ctor => !ctor.IsPrivate && ctor.GetParameters().Any(parameter => IsRawInstant(parameter.ParameterType)))
            .Select(ctor => ctor.ToString())
            .ToArray();
        var rawSetters = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => IsRawInstant(property.PropertyType) && property.SetMethod is { IsPrivate: false })
            .Select(property => property.Name)
            .ToArray();
        var rawFields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => IsRawInstant(field.FieldType) && !field.IsPrivate)
            .Select(field => field.Name)
            .ToArray();

        Assert.Empty(rawConstructors);
        Assert.Empty(rawSetters);
        Assert.Empty(rawFields);
        // The decision instant itself is the admitted type, and it can be set only to another admitted instant.
        Assert.Equal(typeof(AdmittedInstant), type.GetProperty("Instant")!.PropertyType);
    }

    [Fact]
    public void ProductionRawInstantMintsMatchTheReviewedListExactly()
    {
        // Read from compiled IL, not source text, so an alias, a using static or a method group cannot hide a mint.
        var expected = ReviewedMints
            .Select(row => $"{row.Path}	{row.Kind}	{row.Count}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.All(ReviewedMints, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason)));

        var actual = RawMutationPortSymbolInventoryTests.DiscoverCalls(ProductionAssemblies(),
                target => target.DeclaringType == typeof(AdmittedInstant)
                    && target.Name is nameof(AdmittedInstant.FromRecordedAct) or nameof(AdmittedInstant.Exempt))
            .GroupBy(site => (site.Path, Kind: site.Target.Contains($".{nameof(AdmittedInstant.Exempt)}(", StringComparison.Ordinal)
                ? nameof(AdmittedInstant.Exempt) : nameof(AdmittedInstant.FromRecordedAct)))
            .Select(group => $"{group.Key.Path}	{group.Key.Kind}	{group.Count()}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"Expected:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}" +
            $"{Environment.NewLine}Actual:{Environment.NewLine}{string.Join(Environment.NewLine, actual)}");
    }

    private static Assembly[] ProductionAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline*.dll")
            .Where(path => !Path.GetFileName(path).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.Exists(Path.ChangeExtension(path, ".pdb")))
            .Select(Assembly.LoadFrom)
            .ToArray();

    private static bool IsRawInstant(Type type) =>
        type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?) || type == typeof(DateTime) || type == typeof(DateTime?);

    private static string Signature(MethodInfo method) =>
        $"{method.Name}({string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name))})";
}
