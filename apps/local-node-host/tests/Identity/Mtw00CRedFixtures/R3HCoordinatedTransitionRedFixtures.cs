using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00CRedFixtures;

/// <summary>
/// Red fixtures for ADR 0160 R3-H coordinated session transitions (feeds MTW-01C and Phase 2
/// SES-08*): select/auto-select, switch, and logout must each compose the installation and affected
/// tenant audit heads, and success is INVISIBLE before every owning head reports `Completed`. A crash
/// mid-transition rolls forward; a stale handle fails after commit; the other browser is unaffected.
/// The current single-founder web mode has only a dormant R3-H coordinator with no production
/// consumer, so each completion-gate invariant below is red. Restart-shaped because coordinator state
/// must survive a restart to roll forward.
/// </summary>
internal static class R3HCoordinatedTransitionRedFixtures
{
    private const string Domain = "r3h-coordinated-transition";

    internal static IEnumerable<RedFixture> Fixtures()
    {
        yield return Production.Fixture(
            Domain,
            "select_success_invisible_before_all_audit_heads_completed",
            authority: "r3h-select-completion-gate",
            reason:
                "an auto/select session must remain invisible until the installation and tenant audit " +
                "heads both report Completed; the one-time challenge consumes exactly once.",
            restartShaped: true,
            authorityType: typeof(WebTenantSelectionAuthority),
            proof: R3HCoordinatedTransitionAuthorityProof.ProveSelectionCompletionGate);

        yield return Production.Fixture(
            Domain,
            "switch_revokes_old_session_only_after_both_tenant_audits_completed",
            authority: "r3h-switch-completion-gate",
            reason:
                "a tenant switch must revoke the old session and complete both old and new tenant audits " +
                "before the new session is visible, and must not change any other browser's session.",
            restartShaped: true,
            authorityType: typeof(WebTenantSwitchAuthority),
            proof: R3HCoordinatedTransitionAuthorityProof.ProveSwitchCompletionGate);

        yield return Production.Fixture(
            Domain,
            "logout_success_waits_for_completed_and_stale_handle_fails_after_commit",
            authority: "r3h-logout-completion-gate",
            reason:
                "logout success must wait for every owning audit head to report Completed; retry is " +
                "idempotent and a stale handle fails immediately after the committed revoke.",
            restartShaped: true,
            authorityType: typeof(WebSelectedSessionLogoutAuthority),
            proof: R3HCoordinatedTransitionAuthorityProof.ProveLogoutCompletionGate);
    }
}

/// <summary>Runs the mutation-sensitive production-authority tests behind the three R3-H fixtures.</summary>
internal static class R3HCoordinatedTransitionAuthorityProof
{
    internal static void ProveSelectionCompletionGate()
    {
        _ = typeof(WebTenantSelectionAuthority);
        new WebTenantSelectionAuthorityTests()
            .Retry_Rolls_Committing_Selection_Forward_By_Durable_Correlation()
            .GetAwaiter()
            .GetResult();
    }

    internal static void ProveSwitchCompletionGate()
    {
        _ = typeof(WebTenantSwitchAuthority);
        new WebTenantSwitchAuthorityTests()
            .Interrupted_Tenant_Finalization_Leaves_Old_Live_And_Retry_Rolls_Forward()
            .GetAwaiter()
            .GetResult();
        new WebTenantSwitchRealSeamTests()
            .Switch_Finalizes_Both_Tenant_Heads_Through_The_Real_Home_Decision_Fence()
            .GetAwaiter()
            .GetResult();
    }

    internal static void ProveLogoutCompletionGate()
    {
        _ = typeof(WebSelectedSessionLogoutAuthority);
        new WebSelectedSessionLogoutAuthorityTests()
            .Committed_Revocation_Precedes_Audit_Finalization_And_Retry_Rolls_Forward()
            .GetAwaiter()
            .GetResult();
    }
}
