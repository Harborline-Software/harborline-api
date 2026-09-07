using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00CRedFixtures;

/// <summary>
/// Authority fixtures for the ADR 0160 R3-H legacy v1 → v2 cutover (feeds MTW-01E and Phase 1B
/// MIG-*): exclusive restart-safe migration lease, durable v1 mutation write-barrier, atomic v2
/// authority-marker CAS after final-delta verification, and post-CAS rejection of every v1 bearer.
/// All four now ship in <see cref="InstallationIdentityCutoverOrchestrator"/>, so each entry is a
/// production proof whose body drives that authority over a file-backed store and across a genuine
/// restart. Restart-shaped because lease, barrier, and marker state must survive a restart.
/// </summary>
/// <remarks>
/// The MTW-00A category-6 legacy-v1 migration sources the ceremony must dispose of are
/// NodeWebSessionAuthority (founder credential and web sessions), ActiveTeamAuthorizationContext.LocalUserId, NodeTeamRoster,
/// and NodeTenant. Each consults the orchestrator's admission gate rather than owning its own barrier,
/// which is why these proofs assert on that gate — see <see cref="LegacyV1CutoverAuthorityProof"/>.
/// </remarks>
internal static class LegacyV1CutoverRedFixtures
{
    private const string Domain = "legacy-v1-cutover";

    internal static IEnumerable<RedFixture> Fixtures()
    {
        yield return Production.Fixture(
            Domain,
            "installation_migration_lease_is_exclusive_and_restart_safe",
            authority: "installation-migration-lease",
            reason:
                "the v1->v2 migration lease must be exclusive and survive restart; a second holder cannot " +
                "acquire it while the first lease is live, and no copied authority becomes visible.",
            restartShaped: true,
            authorityType: typeof(InstallationIdentityCutoverOrchestrator),
            proof: static () =>
            {
                LegacyV1CutoverAuthorityProof.ProveMigrationLeaseIsExclusiveAndRestartSafe();
            });

        yield return Production.Fixture(
            Domain,
            "v1_mutation_write_barrier_blocks_new_mutation_while_lease_held",
            authority: "v1-mutation-write-barrier",
            reason:
                "while the migration lease is held, every fenced v1 session/roster/tenant/actor writer " +
                "must return identity_migration_in_progress; in-flight work drains and new mutation cannot " +
                "cross the barrier.",
            restartShaped: true,
            authorityType: typeof(InstallationIdentityCutoverOrchestrator),
            proof: static () =>
            {
                LegacyV1CutoverAuthorityProof.ProveV1MutationWriteBarrierBlocksMutationWhileLeaseHeld();
            });

        yield return Production.Fixture(
            Domain,
            "v2_authority_marker_cas_flips_exactly_once",
            authority: "v2-authority-marker-cas",
            reason:
                "the v2 authority marker must flip exactly once under a 100-way race after final-delta " +
                "verification; afterwards v2 mutations require the committed marker.",
            restartShaped: true,
            authorityType: typeof(InstallationIdentityCutoverOrchestrator),
            proof: static () =>
            {
                LegacyV1CutoverAuthorityProof.ProveV2AuthorityMarkerCasFlipsExactlyOnce();
            });

        yield return Production.Fixture(
            Domain,
            "post_cutover_v1_bearer_is_rejected",
            authority: "post-cutover-v1-bearer-rejection",
            reason:
                "after the authority-marker CAS commits, every v1 bearer audience (challenge, selected, " +
                "installation, tooling) must be rejected and no v1 authority resurrects.",
            restartShaped: true,
            authorityType: typeof(InstallationIdentityCutoverOrchestrator),
            proof: static () =>
            {
                LegacyV1CutoverAuthorityProof.ProvePostCutoverV1BearerIsRejected();
            });
    }
}
