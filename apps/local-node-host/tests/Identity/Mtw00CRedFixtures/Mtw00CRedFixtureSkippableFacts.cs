namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00CRedFixtures;

/// <summary>
/// MTW-00C red fixtures as first-class <see cref="Xunit.SkippableFactAttribute"/> tests (the Phase-0
/// red-fixture convention ruled by Admiral, mirroring MTW-00B / PR 2023).
/// </summary>
/// <remarks>
/// <para>
/// <b>CI-green convention.</b> The historical 18 discovery facts are gated on
/// <c>MTW00C_RUN_RED</c>. Pending catalog entries fail for their named missing authority; entries whose
/// authority has shipped execute their production proof and pass. The always-on meta-tests run both
/// classes in the default suite and are the binary gate.
/// </para>
/// <para>
/// Each SkippableFact delegates to the same <see cref="RedFixture.Body"/> the meta-test exercises, so
/// there is one source of substance and two surfaces (opt-in SkippableFact + always-on green gate). The
/// fixture bodies, authority names, and restart proofs are unchanged from the reviewed set.
/// </para>
/// <para>
/// <b>To watch the red fixtures fail (locally / in review):</b>
/// <c>MTW00C_RUN_RED=1 dotnet test apps/local-node-host/tests/tests.csproj --filter "Category=RedFixture"</c>.
/// Pending entries fail with <c>MissingAuthorityException</c>; production entries pass only through
/// their production authority proof.
/// </para>
/// </remarks>
public sealed class Mtw00CRedFixtureSkippableFacts
{
    // ── cookie-audience separation (feeds MTW-01C) ─────────────────────────────────────────────────

    [SkippableFact(DisplayName =
        "MTW-00C red: selected-session extractor must reject a challenge-audience handle " +
        "(web-cookie-audience-isolation) is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_CookieAudience_SelectedExtractorRejectsChallengeHandle()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed(
            "cookie-audience-separation", "selected_extractor_rejects_challenge_audience_handle");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: no fallback to another audience after a selected-user cookie miss is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_CookieAudience_NoFallbackAfterMiss()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed("cookie-audience-separation", "no_audience_fallback_after_miss");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: no fallback to another audience after a refused/expired handle is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_CookieAudience_NoFallbackAfterRefusal()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed("cookie-audience-separation", "no_audience_fallback_after_refusal");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: installation-audience business-route scope fence is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_CookieAudience_InstallationCannotAuthorizeBusinessRoute()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed(
            "cookie-audience-separation", "installation_audience_cannot_authorize_business_route");
    }

    // ── R3-H coordinated transition (feeds MTW-01C, SES-08*) ───────────────────────────────────────

    [SkippableFact(DisplayName =
        "MTW-00C red: R3-H select completion gate (invisible before all audit heads Completed) is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_R3HTransition_SelectSuccessInvisibleBeforeCompleted()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed(
            "r3h-coordinated-transition", "select_success_invisible_before_all_audit_heads_completed");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: R3-H switch completion gate (old revoked only after both tenant audits Completed) " +
        "is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_R3HTransition_SwitchRevokesOldAfterBothTenantAudits()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed(
            "r3h-coordinated-transition", "switch_revokes_old_session_only_after_both_tenant_audits_completed");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: R3-H logout completion gate (success waits for Completed; stale handle fails after " +
        "commit) is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_R3HTransition_LogoutWaitsForCompletedStaleFailsAfterCommit()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed(
            "r3h-coordinated-transition", "logout_success_waits_for_completed_and_stale_handle_fails_after_commit");
    }

    // ── legacy v1 -> v2 cutover (feeds MTW-01E, MIG-*) ─────────────────────────────────────────────

    [SkippableFact(DisplayName =
        "MTW-00C red: exclusive restart-safe installation migration lease is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_LegacyCutover_MigrationLeaseExclusiveRestartSafe()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed(
            "legacy-v1-cutover", "installation_migration_lease_is_exclusive_and_restart_safe");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: durable v1 mutation write-barrier (identity_migration_in_progress) is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_LegacyCutover_V1WriteBarrierBlocksNewMutation()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed(
            "legacy-v1-cutover", "v1_mutation_write_barrier_blocks_new_mutation_while_lease_held");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: atomic v2 authority-marker CAS (flips exactly once under a 100-way race) is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_LegacyCutover_V2AuthorityMarkerCasFlipsOnce()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed("legacy-v1-cutover", "v2_authority_marker_cas_flips_exactly_once");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: post-cutover v1 bearer rejection across all audiences is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_LegacyCutover_PostCutoverV1BearerRejected()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed("legacy-v1-cutover", "post_cutover_v1_bearer_is_rejected");
    }

    // ── credential recovery (feeds MTW-01G, REC-*) ─────────────────────────────────────────────────

    [SkippableFact(DisplayName =
        "MTW-00C red: recovery-challenge purpose binding (no cross-consume) is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_Recovery_ChallengeIsPurposeBound()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed("credential-recovery", "recovery_challenge_is_purpose_bound");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: recovery code one-time consume across restart is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_Recovery_CodeConsumesExactlyOnceAcrossRestart()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed("credential-recovery", "recovery_code_consumes_exactly_once_across_restart");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: recovery revoke-all-audiences before success is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_Recovery_RevokesAllAudiencesBeforeSuccess()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed("credential-recovery", "credential_recovery_revokes_all_audiences_before_success");
    }

    // ── capability side-door fence (feeds MTW-01F, CAP-03*) ────────────────────────────────────────

    [SkippableFact(DisplayName =
        "MTW-00C red: capability operator-enable side-door fence (disable-only) is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_CapabilitySideDoor_OperatorFlagCannotEnable()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed("capability-side-door", "operator_flag_cannot_enable_unadmitted_capability");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: capability route-registration / DI side-door fence is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_CapabilitySideDoor_RegistrationOrDiCannotEnable()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed(
            "capability-side-door", "route_registration_or_di_presence_cannot_enable_capability");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: capability pack / per-tenant activation side-door fence is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_CapabilitySideDoor_PackOrTenantActivationCannotEnable()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed(
            "capability-side-door", "pack_install_or_per_tenant_activation_cannot_enable_capability");
    }

    [SkippableFact(DisplayName =
        "MTW-00C red: capability global-disable veto-only fence is missing")]
    [Trait("PlanCard", "MTW-00C")]
    [Trait("Category", "RedFixture")]
    public void Red_CapabilitySideDoor_GlobalDisableIsVetoOnly()
    {
        Skip.IfNot(Mtw00CRedFixturePolicy.RedFixturesEnabled, Mtw00CRedFixturePolicy.SkipReason);
        Mtw00CRedFixturePolicy.RunRed("capability-side-door", "global_disable_is_veto_only_never_enable");
    }
}

/// <summary>Shared opt-in policy + fixture-body dispatch for the MTW-00C red SkippableFacts.</summary>
internal static class Mtw00CRedFixturePolicy
{
    /// <summary>
    /// Red fixtures run (and fail red) only when <c>MTW00C_RUN_RED=1</c>. Unset — the default, including
    /// CI — skips them so the suite stays green; the green meta-tests prove each is red regardless.
    /// </summary>
    internal static bool RedFixturesEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("MTW00C_RUN_RED"), "1", StringComparison.Ordinal);

    internal const string SkipReason =
        "MTW-00C red fixture — excluded from the default suite so CI stays green. Set MTW00C_RUN_RED=1 " +
        "to run the current catalog outcome. Pending entries fail red; production entries execute " +
        "their production proof. Mtw00CRedFixtureMetaTests checks both states in every run.";

    /// <summary>Look up the named fixture and invoke its pending failure or production proof.</summary>
    internal static void RunRed(string domain, string name) =>
        Mtw00CRedFixtureCatalog.All().Single(fixture =>
            string.Equals(fixture.Domain, domain, StringComparison.Ordinal) &&
            string.Equals(fixture.Name, name, StringComparison.Ordinal)).Body();
}
