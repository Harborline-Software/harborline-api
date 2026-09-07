using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Comms;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Startup hook that registers the comms <see cref="CommsCrdtProjection"/> on the install-level
/// <see cref="IDeltaRouter"/> as the SECOND synced doctype (after contacts), and runs cold-start hydration
/// so the CRDT comms list carries the full existing message log before the gossip daemon ships any delta.
/// The messaging analogue of <c>ContactSyncBootstrapHostedService</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Doctype #2 is an additive router registration.</b> Contacts is registered as the FIRST (default)
/// doctype by <c>ContactSyncBootstrapHostedService</c>; this registers comms as the second — one more
/// <c>IDeltaRouter.Register</c> call (the #1265 seam), NOT a re-architecture. The router routes by the
/// <c>"comms"</c> document id to this projection (the daemon's Phase-1 <c>"default"</c> stream id still
/// routes to the first-registered default, contacts — genuine per-id wire-level fan-out is the daemon
/// follow-on the container-bridge survey flags separately; this registration is the seam that makes that
/// follow-on additive).
/// </para>
/// <para>
/// <b>Ordering.</b> Registered AFTER <see cref="ContactSyncBootstrapHostedService"/> (so contacts keeps the
/// default route) and BEFORE <see cref="MultiTeamBootstrapHostedService"/> (whose StartAsync materializes
/// the first team / per-team daemon bridge) — so the router already owns the comms route, and the doc is
/// already hydrated, by the time the daemon starts. <c>IHostedService.StartAsync</c> runs in registration
/// order, so the registration position guarantees the precondition.
/// </para>
/// </remarks>
public sealed class CommsSyncBootstrapHostedService : IHostedService
{
    private readonly CommsConversationRegistry _conversations;
    private readonly ILogger<CommsSyncBootstrapHostedService> _logger;

    public CommsSyncBootstrapHostedService(
        CommsConversationRegistry conversations,
        ILogger<CommsSyncBootstrapHostedService> logger)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // C1 — register + hydrate the TEAM conversation (the well-known "team" channel). GetOrCreate creates the
        // team-channel projection and registers its CRDT document (id = "team") on the install-level delta
        // router as the comms-side doctype (after contacts). DM conversations register lazily on first use via
        // the registry. The team channel is the comms conversation registered FIRST, so it claims the comms-side
        // default route exactly as the pre-C1 single "comms" document did.
        _conversations.GetOrCreate(CommsConversation.TeamConversationId);
        _logger.LogInformation(
            "Registered team-channel comms conversation '{ConversationId}' on the delta router.",
            CommsConversation.TeamConversationId);

        // Cold-start hydration: seed the team channel's CRDT list from the recoverable local-node.db so the
        // existing message log replicates to a fresh peer on the first sync round. (Scoped to the team
        // conversation — pre-C1 rows back-filled to "team" hydrate here, preserving the team log exactly.)
        var hydrated = await _conversations
            .HydrateAsync(CommsConversation.TeamConversationId, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Comms CRDT cold-start hydration complete ({Count} message(s)).", hydrated);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
