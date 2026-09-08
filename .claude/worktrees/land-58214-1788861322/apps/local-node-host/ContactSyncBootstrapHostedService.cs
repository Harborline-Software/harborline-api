using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.LocalNodeHost.Data.People;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Container-bridge a2 startup hook (ONR survey 2026-06-19). Registers the contacts
/// <see cref="ContactCrdtProjection"/> on the install-level <see cref="IDeltaRouter"/>
/// as the FIRST (default) synced doctype, and runs cold-start hydration so the CRDT
/// document carries the full existing contact set before the gossip daemon ships any
/// delta.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering (the load-bearing reason this is its own hosted service).</b> It is
/// registered BEFORE <see cref="MultiTeamBootstrapHostedService"/> — whose
/// <c>StartAsync</c> materializes the first team, which invokes the per-team
/// registrar, which bridges the per-team daemon's <c>IDeltaProducer</c>/<c>IDeltaSink</c>
/// to THIS router (resolved from the outer provider). The router must therefore already
/// own the contacts route, and the doc must already be hydrated, by the time the team
/// (and later the daemon) starts. <c>IHostedService.StartAsync</c> runs in registration
/// order, so registering this first guarantees the precondition.
/// </para>
/// <para>
/// <b>Why register here and not in DI.</b> <c>IDeltaRouter.Register</c> resolves the
/// concrete projection + the router singleton and wires them — a startup action, not a
/// service registration. Doing it in a hosted service keeps the DI graph declarative and
/// the wiring explicit + ordered. Doctype #2 adds one more <c>Register</c> call here (or
/// its own bootstrap hook) — additive, not a re-architecture.
/// </para>
/// </remarks>
public sealed class ContactSyncBootstrapHostedService : IHostedService
{
    private readonly IDeltaRouter _router;
    private readonly ContactCrdtProjection _projection;
    private readonly ILogger<ContactSyncBootstrapHostedService> _logger;

    public ContactSyncBootstrapHostedService(
        IDeltaRouter router,
        ContactCrdtProjection projection,
        ILogger<ContactSyncBootstrapHostedService> logger)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Register contacts as the first (default) synced doctype on the install-level
        // router. The daemon's Phase-1 "default" stream id routes to this entry.
        _router.Register(ContactCrdtProjection.DocumentId, _projection, _projection);
        _logger.LogInformation(
            "Registered contacts doctype '{DocumentId}' on the delta router (default route).",
            ContactCrdtProjection.DocumentId);

        // Cold-start hydration (Step 5 / the earlier repository ticket #1260 F2 deferral, now closed): seed
        // the CRDT doc from local-node.db so un-touched contacts replicate to a fresh peer.
        var hydrated = await _projection.HydrateFromStoreAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Contacts CRDT cold-start hydration complete ({Count} contact(s)).", hydrated);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
