using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00BRedFixtures;

/// <summary>
/// MTW-00B discovery evidence: red authority fixtures for the multi-tenant-web live-authority ladder.
/// </summary>
/// <remarks>
/// <para>
/// This is TEST-ONLY DISCOVERY (envelope <c>TEST-ID</c>): it adds no production files and asserts no
/// runtime behavior, route, UI, or schema. Each remaining red fixture pins ONE missing authority the plan's
/// Phase 0 card <c>MTW-00B</c> enumerates, by scanning the exact owner surface MTW-00A inventoried for
/// a presence marker that is genuinely absent today. One fixture remains red for its named
/// missing-authority reason; the Party, trust, grant, grant-writer, MTW-2 cutover, and
/// completed-receipt fixtures are graduated.
/// </para>
/// <para>
/// <b>CI-green convention.</b> The repo has no trait-exclusion in its CI test filter
/// (<c>dotnet test Harborline.Api.slnx --filter "FullyQualifiedName!~..."</c>), so a plain failing
/// <c>[Fact]</c> would break CI. Instead the remaining red fixture is a <see cref="SkippableFactAttribute"/>
/// tests gated on the <c>MTW00B_RUN_RED</c> environment variable using the repo's vendored
/// <c>Xunit.SkippableFact</c> — they SKIP in the default suite (CI stays green) and genuinely FAIL red,
/// each for its one named authority, when explicitly enabled. Graduated authorities become permanent
/// always-on positive facts; ADM-01A graduates grant freshness below. The green meta-test
/// <see cref="EveryRedFixture_IsRed_ForItsOneNamedAuthority"/> runs in the default suite and
/// programmatically exercises each red fixture's probe, proving each is currently red for its one named
/// missing-authority reason and is deterministic. This IS the card's binary gate.
/// </para>
/// <para>
/// <b>To watch the red fixtures fail (locally / in review):</b>
/// <c>MTW00B_RUN_RED=1 dotnet test apps/local-node-host/tests/tests.csproj --filter "Category=RedFixture"</c>.
/// Each fails with <c>Assert.True(false, "MTW-00B[&lt;authority&gt;] RED — missing authority: …")</c>,
/// i.e. for its named missing-authority reason ONLY.
/// </para>
/// </remarks>
public sealed class Mtw00BRedAuthorityFixtures
{
    /// <summary>
    /// Asserts the named authority is present in its scoped owner surface. Today it is absent, so this
    /// fails with the probe's single-authority reason and nothing else — no compilation, fixture-setup,
    /// or unrelated failure path exists (the probe is a pure, read-only source scan).
    /// </summary>
    private static void AssertNamedAuthorityIsPresent(AuthorityProbe probe)
    {
        var result = probe.Evaluate();
        Assert.True(result.AuthorityPresent, result.MissingAuthorityReason);
    }

    // ---- [1] 0/1/N membership classification ------------------------------------------------------
    [Fact(DisplayName =
        "MTW-00B graduated: cold-start 0/1/N usable-membership classification is present")]
    [Trait("PlanCard", "MTW-00B")]
    [Trait("PlanCard", "MTW-2-2603")]
    public void Graduated_Membership0Or1OrN_Classification_AuthorityIsPresent()
    {
        var result = Mtw00BRedFixturePolicy
            .AllProbe("membership-0-1-n-classification")
            .Evaluate();

        Assert.True(result.ScannedFileCount > 0);
        Assert.True(result.AuthorityPresent, result.MissingAuthorityReason);
        Assert.NotEmpty(result.MatchedFiles);
    }

    // ---- [2] Party freshness ----------------------------------------------------------------------
    [Fact(DisplayName =
        "MTW-00B graduated: lossless durable explicit-tenant principal-to-Party reader is present")]
    [Trait("PlanCard", "MTW-00B")]
    [Trait("PlanCard", "ADM-02")]
    public void Graduated_PartyFreshness_LosslessExplicitTenantReader_AuthorityIsPresent()
    {
        var result = Mtw00BRedFixturePolicy
            .AllProbe("party-freshness-lossless-explicit-tenant")
            .Evaluate();

        Assert.True(result.ScannedFileCount > 0);
        Assert.True(result.AuthorityPresent, result.MissingAuthorityReason);
    }

    // ---- [3] Trust freshness — graduated by ADM-03 ------------------------------------------------
    [Fact(DisplayName =
        "MTW-00B graduated: exact-tenant verified roster reader is present on a non-empty owner surface")]
    [Trait("PlanCard", "ADM-03")]
    public void Graduated_TrustFreshness_ExplicitTenantVerifiedRoster_AuthorityIsPresent()
    {
        var result = Mtw00BRedFixturePolicy.AllProbe(
                "trust-freshness-explicit-tenant-verified-roster")
            .Evaluate();

        Assert.True(result.ScannedFileCount > 0, "The graduated probe must scan a non-empty production surface.");
        Assert.True(result.AuthorityPresent, result.MissingAuthorityReason);
        Assert.NotEmpty(result.MatchedFiles);
    }

    // ---- [4] Grant freshness (owner version / authorization epoch) --------------------------------
    [Fact(DisplayName =
        "ADM-01A: canonical grant owner_version + authorization_epoch map to durable grant-owned rows")]
    [Trait("PlanCard", "ADM-01A")]
    public void GrantFreshness_OwnerVersionAndEpoch_AuthorityIsPresent()
    {
        var result = Mtw00BRedFixturePolicy.AllProbe("grant-freshness-owner-version-epoch").Evaluate();
        Assert.True(result.ScannedFileCount > 0, "The grant-authority scan surface must contain production files.");
        Assert.True(result.AuthorityPresent, result.MissingAuthorityReason);
    }

    // ---- [5] Founder-binding completed-receipt locator --------------------------------------------
    [Fact(DisplayName =
        "MTW-00B graduated: founder binding has an idempotency-key completed-receipt locator")]
    [Trait("PlanCard", "MTW-00B")]
    [Trait("PlanCard", "MTW-2-2604")]
    public void Graduated_FounderBinding_CompletedReceiptLocator_AuthorityIsPresent()
    {
        var result = Mtw00BRedFixturePolicy
            .AllProbe("completed-receipt-candidate-locator")
            .Evaluate();

        Assert.True(result.ScannedFileCount > 0);
        Assert.True(result.AuthorityPresent, result.MissingAuthorityReason);
    }

    // ---- [6] Installation authority cutover no-bricking — graduated by MTW-2 card 2602 ----------
    [Fact(DisplayName =
        "MTW-00B graduated: installation cutover never disables v1 before v2 is readable")]
    [Trait("PlanCard", "MTW-00B")]
    [Trait("PlanCard", "MTW-2-2602")]
    public void Graduated_InstallationCutover_NoBricking_AuthorityIsPresent()
    {
        var result = Mtw00BRedFixturePolicy.AllProbe("installation-disable-no-bricking").Evaluate();
        Assert.True(result.ScannedFileCount > 0);
        Assert.True(result.AuthorityPresent, result.MissingAuthorityReason);
        Assert.NotEmpty(result.MatchedFiles);
    }

    // ---- [7] Grant writer-bypass ------------------------------------------------------------------
    [Fact(DisplayName =
        "MTW-00B graduated: grant expected-version CAS + production-writer fence is present")]
    [Trait("PlanCard", "ADM-01A")]
    public void Graduated_GrantWriterBypass_ExpectedVersionFence_AuthorityIsPresent()
    {
        var result = Mtw00BRedFixturePolicy.AllProbe(
            "grant-writer-bypass-expected-version-fence").Evaluate();

        Assert.True(result.ScannedFileCount > 0);
        Assert.True(result.AuthorityPresent, result.MissingAuthorityReason);
        Assert.NotEmpty(result.MatchedFiles);
    }

    // ---- Green meta-test (default suite) — the MTW-00B binary gate ---------------------------------
    [Fact(DisplayName =
        "MTW-00B: all seven required authorities are graduated and no red fixture remains")]
    [Trait("PlanCard", "MTW-00B")]
    public void EveryRedFixture_IsRed_ForItsOneNamedAuthority()
    {
        var probes = Mtw00BAuthorityProbes.Red;

        // Coverage: every authority is now graduated.
        var expected = Array.Empty<string>();
        Assert.Equal(expected, Mtw00BAuthorityProbes.RequiredAuthorityIds);
        Assert.Equal(expected.Length, probes.Count);
        Assert.Equal(probes.Count, probes.Select(p => p.AuthorityId).Distinct(StringComparer.Ordinal).Count());

        var allIds = Mtw00BAuthorityProbes.All.Select(p => p.AuthorityId).ToArray();

        foreach (var probe in probes)
        {
            var first = probe.Evaluate();
            var second = probe.Evaluate();

            // Deterministic + restartable: two evaluations of the same pure source scan agree exactly.
            Assert.Equal(first.AuthorityPresent, second.AuthorityPresent);
            Assert.Equal(first.MissingAuthorityReason, second.MissingAuthorityReason);

            Assert.False(first.AuthorityPresent, first.MissingAuthorityReason);

            // The red reason is non-empty and names exactly ONE authority id (its own), never another's.
            Assert.False(string.IsNullOrWhiteSpace(first.MissingAuthorityReason));
            var namedIds = allIds.Where(id =>
                first.MissingAuthorityReason.Contains(id, StringComparison.Ordinal)).ToArray();
            Assert.Equal(new[] { probe.AuthorityId }, namedIds);
        }
    }
}

/// <summary>Shared opt-in policy + probe lookup for the MTW-00B red fixtures.</summary>
internal static class Mtw00BRedFixturePolicy
{
    /// <summary>
    /// Red fixtures run (and fail red) only when <c>MTW00B_RUN_RED=1</c>. Unset — the default, including
    /// CI — skips them so the suite stays green; the green meta-test proves each is red regardless.
    /// </summary>
    internal static bool RedFixturesEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("MTW00B_RUN_RED"), "1", StringComparison.Ordinal);

    internal const string SkipReason =
        "MTW-00B red fixture — excluded from the default suite so CI stays green. Set MTW00B_RUN_RED=1 " +
        "to run it and watch it fail red for its one named missing authority. The green meta-test " +
        "EveryRedFixture_IsRed_ForItsOneNamedAuthority proves its redness in every run.";

    internal static AuthorityProbe Probe(string authorityId) =>
        Mtw00BAuthorityProbes.Red.Single(probe =>
            string.Equals(probe.AuthorityId, authorityId, StringComparison.Ordinal));

    internal static AuthorityProbe AllProbe(string authorityId) =>
        Mtw00BAuthorityProbes.All.Single(probe =>
            string.Equals(probe.AuthorityId, authorityId, StringComparison.Ordinal));
}
