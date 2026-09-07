using System.Runtime.CompilerServices;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00CRedFixtures;

/// <summary>
/// MTW-00C green meta-tests. Pending fixtures must fail for their named missing authority; production
/// fixtures must execute their production proof cleanly. The catalog cannot be made green by adding
/// an authority name to a registration set because no such set exists.
/// </summary>
[Trait("PlanCard", "MTW-00C")]
public sealed class Mtw00CRedFixtureMetaTests
{
    // Production capability-enable / admission-bypass patterns that must never appear in a discovery
    // red fixture (envelope TEST-ID forbids production behavior). Held in THIS file, which the scan
    // deliberately excludes, so the denylist literals never self-match.
    private static readonly string[] ForbiddenProductionEnableTokens =
    [
        "EnableCapability",
        "ForceReady",
        "ForceCapabilityReady",
        "MarkReady",
        "MarkAdmitted",
        "OverrideEffectiveState",
        "TenantCapabilityActivation(",
        ".Activate(",
    ];

    // Expected minimum fixture count per card domain (the five MTW-00C bullet domains).
    private static readonly IReadOnlyDictionary<string, int> ExpectedDomainMinimums =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["cookie-audience-separation"] = 4,
            ["r3h-coordinated-transition"] = 3,
            ["legacy-v1-cutover"] = 4,
            ["credential-recovery"] = 3,
            ["capability-side-door"] = 4,
        };

    public static IEnumerable<object[]> AllFixtureKeys() =>
        Mtw00CRedFixtureCatalog.All()
            .Select(fixture => new object[] { fixture.Domain, fixture.Name });

    /// <remarks>
    /// Enumerates EVERY catalog entry rather than only the pending ones, and branches on the entry's
    /// declared state. Filtering to pending would empty the data set the moment the last authority
    /// ships — xUnit 2 fails a <c>[MemberData]</c> theory with no rows, so the suite would go red for
    /// the good outcome. Enumerating everything also gives each production proof its own named test
    /// case, which the collective sweeps below cannot.
    /// </remarks>
    [Theory(DisplayName =
        "MTW-00C: each catalog entry resolves through its declared state — pending fails for its " +
        "named missing authority, production executes its proof")]
    [MemberData(nameof(AllFixtureKeys))]
    public void CatalogEntry_Resolves_ThroughItsDeclaredState(string domain, string name)
    {
        var fixture = Find(domain, name);

        if (fixture.IsProductionProof)
        {
            fixture.Body();
            return;
        }

        var thrown = Assert.Throws<MissingAuthorityException>(fixture.Body);

        Assert.Equal(fixture.MissingAuthority, thrown.Authority);
        Assert.Equal(fixture.Reason, thrown.Reason);
    }

    [Fact(DisplayName =
        "MTW-00C: every red fixture is deterministic — repeated runs fail with the identical authority " +
        "and reason")]
    public void RedFixtures_AreDeterministic()
    {
        foreach (var fixture in Mtw00CRedFixtureCatalog.All()
                     .Where(fixture => !fixture.IsProductionProof))
        {
            var first = Assert.Throws<MissingAuthorityException>(fixture.Body);
            var second = Assert.Throws<MissingAuthorityException>(fixture.Body);

            Assert.Equal(first.Authority, second.Authority);
            Assert.Equal(first.Reason, second.Reason);
            Assert.Equal(fixture.MissingAuthority, first.Authority);
            Assert.Equal(fixture.Reason, first.Reason);
        }
    }

    [Fact(DisplayName =
        "MTW-00C: every claimed-built authority passes only through its production proof")]
    public void ProductionFixtures_Exercise_TheirAuthority()
    {
        var production = Mtw00CRedFixtureCatalog.All()
            .Where(fixture => fixture.IsProductionProof)
            .ToArray();

        Assert.NotEmpty(production);
        Assert.All(production, fixture =>
        {
            Assert.NotNull(fixture.ProductionAuthorityType);
            Assert.NotEqual(
                typeof(Mtw00CRedFixtureMetaTests).Assembly,
                fixture.ProductionAuthorityType!.Assembly);
            fixture.Body();
        });
    }

    [Fact(DisplayName =
        "MTW-00C: the red-fixture catalog is complete, uniquely named, and covers all five card domains")]
    public void Catalog_IsComplete_AndUnique()
    {
        var all = Mtw00CRedFixtureCatalog.All();
        Assert.NotEmpty(all);

        var ids = all.Select(fixture => fixture.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

        var authorities = all.Select(fixture => fixture.MissingAuthority).ToArray();
        Assert.All(authorities, authority => Assert.False(string.IsNullOrWhiteSpace(authority)));
        Assert.Equal(authorities.Length, authorities.Distinct(StringComparer.Ordinal).Count());

        Assert.All(all, fixture => Assert.False(string.IsNullOrWhiteSpace(fixture.Reason)));

        foreach (var (domain, minimum) in ExpectedDomainMinimums)
        {
            var count = all.Count(fixture => string.Equals(fixture.Domain, domain, StringComparison.Ordinal));
            Assert.True(count >= minimum, $"domain '{domain}' had {count} fixtures; expected >= {minimum}.");
        }

        Assert.All(all, fixture => Assert.Contains(fixture.Domain, ExpectedDomainMinimums.Keys));
    }

    [Fact(DisplayName =
        "MTW-00C: restart-shaped fixtures survive a real encrypted store close/reopen cycle")]
    public void RestartShapedFixtures_SurviveCloseReopen()
    {
        // The restart mechanism itself is sound and reusable across repeated cycles.
        for (var iteration = 0; iteration < 3; iteration++)
        {
            DurableStore.SelfTest();
        }

        // The catalog actually contains restart-shaped fixtures, and each one's body exercises the
        // durable cycle before its red assertion — a broken cycle throws InvalidOperationException
        // (not MissingAuthorityException), so this would fail loudly rather than pass silently.
        //
        // Wired restart-shaped fixtures are deliberately NOT filtered out here. A durability proof
        // belongs in the restart-shaped test whether or not its authority is built yet: once wired,
        // the body drives the real production store across a genuine close/reopen, which is exactly
        // what this test exists to collect. Filtering them out would leave the durable half of a
        // built authority covered only by the generic wired sweep, losing its restart-shaped placement.
        var restartShaped = Mtw00CRedFixtureCatalog.All()
            .Where(fixture => fixture.RestartShaped)
            .ToArray();
        Assert.NotEmpty(restartShaped);

        foreach (var fixture in restartShaped)
        {
            if (fixture.IsProductionProof)
            {
                // Wired: the body must run clean against production — no missing-authority throw.
                fixture.Body();
                continue;
            }

            var thrown = Assert.Throws<MissingAuthorityException>(fixture.Body);
            Assert.Equal(fixture.MissingAuthority, thrown.Authority);
        }
    }

    [Fact(DisplayName =
        "MTW-00C: no red fixture contains a production capability-enable backdoor (envelope TEST-ID)")]
    public void RedFixtures_ContainNoProductionBackdoor()
    {
        var directory = ThisDirectory();

        // These fixtures live under the tests project only — structurally not in the production build.
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
            directory + Path.DirectorySeparatorChar);

        var thisFileName = Path.GetFileName(ThisFile());
        var scanned = Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFileName(file), thisFileName, StringComparison.Ordinal))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(scanned);

        var violations = new List<string>();
        foreach (var file in scanned)
        {
            var source = File.ReadAllText(file);
            foreach (var token in ForbiddenProductionEnableTokens)
            {
                if (source.Contains(token, StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(file)} contains forbidden production-enable token '{token}'");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "MTW-00C red fixtures must contain no production capability-enable path:\n" +
            string.Join("\n", violations));
    }

    private static RedFixture Find(string domain, string name) =>
        Mtw00CRedFixtureCatalog.All().Single(fixture =>
            string.Equals(fixture.Domain, domain, StringComparison.Ordinal) &&
            string.Equals(fixture.Name, name, StringComparison.Ordinal));

    // [CallerFilePath] is resolved at the compile-time call site below, not by xUnit's reflection
    // invocation — so these helpers are called from source, never used as test-method parameters.
    private static string ThisDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(thisFile)!;

    private static string ThisFile([CallerFilePath] string thisFile = "") => thisFile;
}
