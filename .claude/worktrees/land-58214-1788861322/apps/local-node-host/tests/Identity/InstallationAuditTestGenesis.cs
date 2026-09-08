using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Seeds a minimal, hash-valid genesis of the installation-identity audit chain (identity + active
/// root epoch + sequence-1 envelope + head) so tests can exercise durable-audit seams that append
/// onto an already-bootstrapped installation, exactly as production does.
/// </summary>
internal static class InstallationAuditTestGenesis
{
    internal const string InstallationIdentityId = "test-installation-identity";

    internal const string RootFingerprint =
        "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    private static readonly DateTimeOffset GenesisAt =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static async Task SeedAsync(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory)
    {
        await using var context = contextFactory.CreateDbContext();
        await SeedAsync(context);
    }

    internal static async Task SeedAsync(NodeLocalInstallationIdentityDbContext context)
    {
        context.InstallationIdentities.Add(new InstallationIdentityRecord
        {
            SingletonKey = InstallationIdentityRecord.SingletonKeyValue,
            InstallationIdentityId = InstallationIdentityId,
            ActiveRootEpoch = 1,
            AuthorityVersion = InstallationIdentityRecord.Revision3AuthorityVersion,
            OwnerVersion = 1,
            CreatedAtUtc = GenesisAt,
            UpdatedAtUtc = GenesisAt,
        });
        context.RootKeyEpochs.Add(new InstallationRootKeyEpochRecord
        {
            InstallationIdentityId = InstallationIdentityId,
            EpochNumber = 1,
            RootPublicKeyFingerprint = RootFingerprint,
            Status = InstallationRootEpochStatus.Active,
            TransitionCorrelationId = "test-genesis-transition",
            CreatedAtUtc = GenesisAt,
        });

        var genesis = new InstallationAuditEnvelopeRecord
        {
            InstallationIdentityId = InstallationIdentityId,
            Sequence = 1,
            CorrelationId = "test-genesis-correlation",
            CommandFingerprint = "test-genesis-fingerprint",
            EventType = InstallationIdentityAuditEventTypes.FounderBootstrapped,
            ActorKind = "local-bootstrap-authority",
            ActorId = "local-installation-console",
            RootEpoch = 1,
            RootPublicKeyFingerprint = RootFingerprint,
            PreviousHash = InstallationAuditIntegrity.ZeroHash,
            EnvelopeHash = string.Empty,
            PayloadDigest = "test-genesis-payload",
            OccurredAtUtc = GenesisAt,
        };
        genesis.EnvelopeHash = InstallationAuditIntegrity.ComputeEnvelopeHash(genesis);
        context.AuditEnvelopes.Add(genesis);
        context.AuditHeads.Add(new InstallationAuditHeadRecord
        {
            InstallationIdentityId = InstallationIdentityId,
            Sequence = 1,
            HeadHash = genesis.EnvelopeHash,
            OwnerVersion = 1,
            UpdatedAtUtc = GenesisAt,
        });
        await context.SaveChangesAsync();
    }
}
