using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// The installation-identity activation fence: which dormant identity authorities may be reached
/// from production code, and which readiness claims may not be made at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Retirement record — the founder-bootstrap half (card #3342, 2026-07-29).</b> This fence
/// previously required that NO production file name
/// <see cref="InstallationFounderBootstrapService"/>, which was correct while ADR 0160 Revision 3
/// was awaiting acceptance. Revision 3 has been ACCEPTED since 2026-07-13
/// (<c>docs/adrs/0160-local-human-web-identity-and-session-authority.md</c> frontmatter
/// <c>status: Accepted</c>, <c>revision: 3</c>), and R3-E/R3-F name installation founder-account
/// bootstrap as the one-time ceremony that creates the installation's first account and its first
/// root installation grant. Leaving the fence at zero consumers made that accepted ceremony
/// unreachable and left <c>identity.Accounts</c> permanently empty, so
/// <c>/api/session/account-challenge</c> refused every actor and every route behind it was
/// structurally unreachable.
/// </para>
/// <para>
/// The fence is therefore LOWERED, not removed, and lowered by exactly one file: the consumer set is
/// asserted to EQUAL the ratified ceremony, so a second consumer, a differently-placed consumer, or
/// the silent removal of this one all fail. That is the same allowlist discipline the sibling
/// tenant-candidate-locator fence uses. Nothing else about the dormant-authority posture changed —
/// the multi-tenant-web readiness claim remains forbidden outright, because ADR 0160 R3-I's binary
/// readiness gate is not met.
/// </para>
/// </remarks>
public sealed class InstallationIdentityDormancyArchTests
{
    /// <summary>
    /// The ratified production consumer of the founder-bootstrap authority. ADR 0160 R3-E's
    /// once-per-installation ceremony, reached from local bootstrap authority at host start.
    /// </summary>
    private static readonly string[] RatifiedFounderBootstrapConsumers =
    [
        // earlier repository ticket #3448 — names the bootstrap SERVICE only in a <see cref> doc reference explaining
        // which rows that service writes and which it omits. It calls nothing on it.
        Path.Combine("Data", "Identity", "FounderTenantMembershipAttachService.cs"),
        Path.Combine("Data", "Identity", "InstallationFounderBootstrapCeremony.cs"),
    ];

    private static readonly string[] FounderBootstrapSymbols =
    [
        nameof(InstallationFounderBootstrapService),
        nameof(InstallationFounderBootstrapCommand),
    ];

    /// <summary>
    /// The ratified production files that may name the ceremony WRAPPER. The fence above tracks the
    /// authority's symbols, but the ceremony is a <c>public sealed class</c> with a <c>public
    /// RunAsync</c> registered as a singleton, so it is DI-resolvable from any route handler in this
    /// assembly. Without this list the PR's central claim — "no listener route reaches this authority,
    /// and none should" — would be prose: a future endpoint could take the ceremony in its constructor,
    /// call <c>RunAsync()</c>, and every test would stay green. Only <c>Program.cs</c> composes it; the
    /// other two entries name it without reaching it, and are listed rather than exempted so the fence
    /// reports exactly what it sees.
    /// </summary>
    private static readonly string[] RatifiedCeremonyConsumers =
    [
        // Describes the hosted runner's start order and activation. Ordering metadata, not a call.
        Path.Combine("Capabilities", "LocalNodeHostedComponentCatalog.cs"),
        // 294 s2a: offline recovery derives the founder party through the composition root's function and
        // must share the founder ceremony's correlation constant, so it cannot establish a second key space.
        Path.Combine("Data", "Identity", "AdministratorRecoveryCommand.cs"),
        // earlier repository ticket #3448 — reads InstallationFounderBootstrapCeremony.CorrelationId to resolve the
        // founder's root grant BY KEY (the same key the bootstrap service uses on replay). Reading a
        // compile-time constant, not reaching RunAsync. Listed rather than exempted so the fence keeps
        // reporting exactly what it sees.
        Path.Combine("Data", "Identity", "FounderTenantMembershipAttachService.cs"),
        // A <see cref> cross-reference in the authority's own doc comment, naming its one consumer.
        Path.Combine("Data", "Identity", "InstallationFounderBootstrapService.cs"),
        // The one composition path: AddInstallationFounderBootstrapCeremony.
        "Program.cs",
    ];

    private static readonly string[] CeremonySymbols =
    [
        nameof(InstallationFounderBootstrapCeremony),
        "AddInstallationFounderBootstrapCeremony",
    ];

    /// <summary>The ADR 0154 capability key whose readiness ADR 0160 R3-I has not yet admitted.</summary>
    private const string MultiTenantWebReadinessClaim = "identity.multi-tenant-web/v1";

    [Fact]
    public void Founder_Bootstrap_Authority_Has_Exactly_One_Ratified_Production_Consumer()
    {
        var hostRoot = FindHostSourceRoot();
        var declaration = Path.GetFullPath(Path.Combine(
            hostRoot, "Data", "Identity", "InstallationFounderBootstrapService.cs"));

        var consumers = ScanProductionSources(hostRoot)
            .Where(file => !string.Equals(file, declaration, StringComparison.Ordinal))
            .Where(file => ContainsAny(File.ReadAllText(file), FounderBootstrapSymbols))
            .Select(file => Path.GetRelativePath(hostRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(RatifiedFounderBootstrapConsumers, consumers);
    }

    /// <summary>
    /// The ceremony wrapper is reachable only from composition. Same allowlist discipline as the
    /// authority fence above: set equality, so a new consumer, a moved consumer, or the silent removal
    /// of the composition call all fail.
    /// </summary>
    [Fact]
    public void Founder_Bootstrap_Ceremony_Is_Reached_Only_From_Ratified_Composition()
    {
        var hostRoot = FindHostSourceRoot();
        var declaration = Path.GetFullPath(Path.Combine(
            hostRoot, "Data", "Identity", "InstallationFounderBootstrapCeremony.cs"));

        var consumers = ScanProductionSources(hostRoot)
            .Where(file => !string.Equals(file, declaration, StringComparison.Ordinal))
            .Where(file => ContainsAny(File.ReadAllText(file), CeremonySymbols))
            .Select(file => Path.GetRelativePath(hostRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(RatifiedCeremonyConsumers, consumers);
    }

    [Fact]
    public void Multi_Tenant_Web_Readiness_Is_Not_Claimed_By_Any_Production_File()
    {
        var hostRoot = FindHostSourceRoot();

        var claimants = ScanProductionSources(hostRoot)
            .Where(file => File.ReadAllText(file)
                .Contains(MultiTenantWebReadinessClaim, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(hostRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(claimants);
    }

    /// <summary>
    /// The fence bites: an unratified production consumer of a fenced symbol is reported. Proven
    /// mechanically over a synthetic tree so the bite-proof cannot itself rot, and so the two
    /// exclusion classes the real scan relies on (the declaration exemption and the test/obj/bin
    /// skip) are each shown to be narrow rather than a blanket pass.
    /// </summary>
    [Fact]
    public void Dormancy_Scanner_Bites_On_An_Unratified_Production_Consumer()
    {
        var root = Path.Combine(Path.GetTempPath(), $"harborline-dormancy-fence-{Guid.NewGuid():N}");
        try
        {
            Write(root, Path.Combine("Data", "Identity", "InstallationFounderBootstrapService.cs"),
                $"class {nameof(InstallationFounderBootstrapService)} {{ }}");
            Write(root, Path.Combine("Data", "Identity", "InstallationFounderBootstrapCeremony.cs"),
                $"new {nameof(InstallationFounderBootstrapService)}();");
            Write(root, Path.Combine("Health", "WebSession", "SmuggledConsumerRoutes.cs"),
                $"new {nameof(InstallationFounderBootstrapCommand)}();");
            Write(root, Path.Combine("Health", "WebSession", "SmuggledCeremonyRoutes.cs"),
                $"{nameof(InstallationFounderBootstrapCeremony)} c; c.RunAsync();");
            Write(root, Path.Combine("tests", "Identity", "SomeTests.cs"),
                $"new {nameof(InstallationFounderBootstrapService)}();");
            Write(root, Path.Combine("obj", "Debug", "Generated.cs"),
                $"new {nameof(InstallationFounderBootstrapCommand)}();");
            Write(root, Path.Combine("bin", "Debug", "Copied.cs"),
                $"new {nameof(InstallationFounderBootstrapCommand)}();");

            var declaration = Path.GetFullPath(Path.Combine(
                root, "Data", "Identity", "InstallationFounderBootstrapService.cs"));
            var consumers = ScanProductionSources(root)
                .Where(file => !string.Equals(file, declaration, StringComparison.Ordinal))
                .Where(file => ContainsAny(File.ReadAllText(file), FounderBootstrapSymbols))
                .Select(file => Path.GetRelativePath(root, file))
                .Order(StringComparer.Ordinal)
                .ToArray();

            // The ratified ceremony AND the smuggled route are both reported; equality against the
            // ratified set alone is what turns the extra one into a failure. The test, obj, and bin
            // copies are skipped, and the declaration itself is exempt.
            Assert.Equal(
                [
                    Path.Combine("Data", "Identity", "InstallationFounderBootstrapCeremony.cs"),
                    Path.Combine("Health", "WebSession", "SmuggledConsumerRoutes.cs"),
                ],
                consumers);
            Assert.NotEqual(RatifiedFounderBootstrapConsumers, consumers);

            // The wrapper fence bites on the same tree: a route that resolves the ceremony and calls
            // RunAsync is reported, and is not in the ratified composition set.
            var ceremonyDeclaration = Path.GetFullPath(Path.Combine(
                root, "Data", "Identity", "InstallationFounderBootstrapCeremony.cs"));
            var ceremonyConsumers = ScanProductionSources(root)
                .Where(file => !string.Equals(file, ceremonyDeclaration, StringComparison.Ordinal))
                .Where(file => ContainsAny(File.ReadAllText(file), CeremonySymbols))
                .Select(file => Path.GetRelativePath(root, file))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [Path.Combine("Health", "WebSession", "SmuggledCeremonyRoutes.cs")],
                ceremonyConsumers);
            Assert.NotEqual(RatifiedCeremonyConsumers, ceremonyConsumers);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Activated_Tenant_Candidate_Locator_Has_Only_Ratified_Production_Consumers()
    {
        var hostRoot = FindHostSourceRoot();
        var declaration = Path.GetFullPath(Path.Combine(
            hostRoot, "Data", "Identity", "InstallationTenantCandidateLocator.cs"));
        var symbols = new[]
        {
            nameof(IInstallationTenantCandidateLocator),
            nameof(InstallationTenantCandidateLocator),
            "AddInstallationTenantCandidateClassification",
        };

        var consumers = ScanProductionSources(hostRoot)
            .Where(file => !string.Equals(file, declaration, StringComparison.Ordinal))
            .Where(file => ContainsAny(File.ReadAllText(file), symbols))
            .Select(file => Path.GetRelativePath(hostRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                // earlier repository ticket #3448 — names the locator only in a <see cref> doc reference explaining WHY the
        // founder had no candidates. It does not call it.
        Path.Combine("Data", "Identity", "FounderTenantMembershipAttachService.cs"),
        Path.Combine("Data", "Identity", "WebTenantSelectionAuthority.cs"),
                Path.Combine("Data", "Identity", "WebTenantSwitchAuthority.cs"),
                // earlier repository ticket #3329 step 2, ratified on the card (see earlier repository ticket #3329 and the PR that added this
                // line) — the challenge returns the caller's
                // OWN 0/1/N classification so the Harborline App can mount TenantWorkspacePicker. Ratified as
                // a READ of the caller's own memberships, after the password has already verified, and
                // it decides nothing: `select` still re-resolves candidates server-side and remains the
                // only authority on what is usable. The route consumes it fail-SOFT — a locator failure
                // degrades to "unknown" and the client falls back to the null-tenant Single path, so a
                // classification outage can never cost a sign-in that would otherwise succeed.
                //
                // Why it had to be added here at all: `select` accepts a null tenant id ONLY when the
                // account has exactly one candidate. With 0 or >=2 it refuses AND strands a challenge
                // cookie, so a browser that could only ever send null could not sign a multi-workspace
                // account in, and its failed select wedged the next attempt.
                Path.Combine("Health", "WebSession", "AccountChallengeRoutes.cs"),
                "Program.cs",
            ],
            consumers);
    }

    [Fact]
    public void Tenant_Authority_Key_Has_One_Production_Writer()
    {
        const string authorityKey = "identity/tenant-membership-authority/v3";
        var hostRoot = FindHostSourceRoot();
        var declaration = Path.Combine(
            hostRoot, "Data", "Identity", "TenantMembershipAuthorityStore.cs");
        var owners = Directory.EnumerateFiles(hostRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains(authorityKey, StringComparison.Ordinal))
            .Select(Path.GetFullPath)
            .ToArray();

        Assert.Equal([Path.GetFullPath(declaration)], owners);
    }

    /// <summary>
    /// Every non-test, non-build-output C# source under a host root, as absolute paths. Shared by
    /// each fence above so one scan definition governs them all.
    /// </summary>
    private static IEnumerable<string> ScanProductionSources(string hostRoot) =>
        Directory.EnumerateFiles(hostRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(Path.GetFullPath);

    private static bool ContainsAny(string text, IReadOnlyList<string> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (text.Contains(symbol, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string FindHostSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "local-node-host", "Program.cs");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)
                    ?? throw new InvalidOperationException("Program.cs has no parent directory.");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the local-node-host source root from " + AppContext.BaseDirectory);
    }
}
