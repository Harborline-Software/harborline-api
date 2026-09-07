using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00CRedFixtures;

/// <summary>
/// Red fixtures for credential recovery (feeds MTW-01G and Phase 2/7 REC-*): the recovery challenge
/// is purpose-bound and cannot be cross-consumed, a recovery code consumes exactly once (the consumed
/// state surviving restart so a replay refuses), and a successful recovery revokes every challenge,
/// selected, and installation session across all tenants before success — no audience omitted. No
/// recovery store or command exists yet, so each invariant is red.
/// </summary>
internal static class CredentialRecoveryRedFixtures
{
    private const string Domain = "credential-recovery";

    internal static IEnumerable<RedFixture> Fixtures()
    {
        yield return Production.Fixture(
            Domain,
            "recovery_challenge_is_purpose_bound",
            authority: "recovery-challenge-purpose-binding",
            reason:
                "a recovery challenge must be purpose-bound: a code presented under a purpose it was " +
                "not issued for is refused by the consuming authority's purpose predicate, a recovery " +
                "code cannot be consumed as a login or select challenge, and a login/select challenge " +
                "cannot be consumed as a recovery challenge.",
            restartShaped: false,
            authorityType: typeof(AccountSetupInvitationStore),
            proof: static () =>
            {
                CredentialRecoveryAuthorityProof.ProvePurposeBinding();
            });

        yield return Production.Fixture(
            Domain,
            "recovery_code_consumes_exactly_once_across_restart",
            authority: "recovery-one-time-consume",
            reason:
                "a recovery challenge must consume exactly once and the consumed state must survive " +
                "restart, so a replayed recovery code refuses after the node restarts.",
            restartShaped: true,
            authorityType: typeof(RecoveryInvitationStore),
            proof: static () =>
            {
                CredentialRecoveryAuthorityProof.ProveOneTimeConsumeAcrossRestart();
            });

        yield return Production.Fixture(
            Domain,
            "credential_recovery_revokes_all_audiences_before_success",
            authority: "recovery-revoke-all-audiences",
            reason:
                "credential recovery must revoke every challenge, selected, and installation session " +
                "across all tenants before success; no audience is omitted or confused.",
            restartShaped: true,
            authorityType: typeof(AccountCredentialRecoveryService),
            proof: static () =>
            {
                CredentialRecoveryAuthorityProof.ProveAllAudiencesRevokedBeforeSuccess();
            });
    }
}
