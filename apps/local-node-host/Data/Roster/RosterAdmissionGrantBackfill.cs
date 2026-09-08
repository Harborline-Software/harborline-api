using Harborline.Api.Foundation.Crypto;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>One-time conversion of verified admission history to ordinary durable role grants.</summary>
public sealed class RosterAdmissionGrantBackfill(
    IDbContextFactory<NodeLocalRosterDbContext> rosterFactory,
    NodeEfAuthorizationConfigurationStore configuration,
    IOperationVerifier verifier,
    ILogger<RosterAdmissionGrantBackfill> logger)
{
    /// <summary>The validated owner commits the grants and completion before roster sync starts.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var count = await configuration.CommitRosterAdmissionMigrationAsync(rosterFactory, verifier, ct).ConfigureAwait(false);
        if (count > 0) logger.LogInformation("Migrated {Count} signed admission record(s) into durable grants.", count);
        return count;
    }
}

/// <summary>Durable one-time completion evidence; this is never a permission verdict.</summary>
public sealed class RosterAdmissionGrantBackfillRow
{
    public int Id { get; set; }
    public int RecordCount { get; set; }
}
