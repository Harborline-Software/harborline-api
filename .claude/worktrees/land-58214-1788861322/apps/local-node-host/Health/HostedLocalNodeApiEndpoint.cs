using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0114/0115 cohort 1b hosted service that maps the local-node validation
/// API surface on the shared Kestrel listener. Routes are intentionally minimal
/// — they exist to prove the domain-layer pipeline (DI → DbContextFactory →
/// SQLite migration schema) is reachable over HTTP from the embedded Anchor
/// shell. No frontend is wired at this stage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes registered:</b>
/// <list type="bullet">
///   <item><c>GET /api/local-node/status</c> — pings the SQLite database and
///     returns a JSON object with the provider name, migration count, and
///     entity-type count from the compiled model.</item>
///   <item><c>GET /api/local-node/tables</c> — returns the list of table names
///     registered in <see cref="LocalNodeDbContext"/>'s compiled model. Useful
///     for verifying that all 7 shared entity modules produced the expected
///     schema surface during the design-time migration run.</item>
/// </list>
/// </para>
/// <para>
/// Registration order: this service must be added to the composition root
/// BEFORE <see cref="SharedHostedWebApp"/> so its <c>StartAsync</c> registers
/// paths while the shared app is still in its pre-<c>StartAsync</c>
/// configuration phase.
/// </para>
/// </remarks>
public sealed class HostedLocalNodeApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IDbContextFactory<LocalNodeDbContext> _dbContextFactory;
    private readonly ILogger<HostedLocalNodeApiEndpoint> _logger;

    /// <summary>
    /// Constructs the hosted API endpoint. All dependencies resolve from the
    /// outer DI container via the composition root.
    /// </summary>
    public HostedLocalNodeApiEndpoint(
        SharedHostedWebApp sharedApp,
        IDbContextFactory<LocalNodeDbContext> dbContextFactory,
        ILogger<HostedLocalNodeApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(dbContextFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
        {
            var deviceReachable = app.MapDeviceReachableProductDataGroup();
            var selectedSession = app.MapSelectedSessionProductGroup();

            // GET /api/local-node/status
            // Proves the EF factory + SQLite connection + migration schema are
            // operational. Returns JSON with provider + migration count + entity
            // type count.
            deviceReachable.MapGet("/api/local-node/status", async (CancellationToken ct) =>
            {
                await using var ctx = await _dbContextFactory
                    .CreateDbContextAsync(ct)
                    .ConfigureAwait(false);

                var providerName = ctx.Database.ProviderName ?? "(unknown)";

                // Pending migrations array — empty when the DB is up-to-date.
                // EnsureCreated / migrate must have run first; if not, this will
                // return 0 applied + N pending.
                var applied = await ctx.Database
                    .GetAppliedMigrationsAsync(ct)
                    .ConfigureAwait(false);
                var pending = await ctx.Database
                    .GetPendingMigrationsAsync(ct)
                    .ConfigureAwait(false);

                var entityTypeCount = ctx.Model.GetEntityTypes().Count();

                return Results.Ok(new
                {
                    provider = providerName,
                    appliedMigrations = applied.Count(),
                    pendingMigrations = pending.Count(),
                    entityTypes = entityTypeCount,
                });
            });

            // GET /api/local-node/tables
            // Lists every relational table name registered in the compiled model.
            // Validates that all 7 shared entity modules contributed their expected
            // tables after the SQLite post-config sweep (ADR 0114 C1).
            selectedSession.MapGet("/api/local-node/tables", async (CancellationToken ct) =>
            {
                await using var ctx = await _dbContextFactory
                    .CreateDbContextAsync(ct)
                    .ConfigureAwait(false);

                var tables = ctx.Model
                    .GetEntityTypes()
                    .Select(e => new
                    {
                        entity = e.ClrType.Name,
                        table = e.GetTableName() ?? "(no table)",
                    })
                    .OrderBy(r => r.table)
                    .ToList();

                return Results.Ok(tables);
            });
        });

        _logger.LogInformation(
            "ADR 0114/0115 cohort 1b local-node validation API registered on shared hosted web-app " +
            "(/api/local-node/status, /api/local-node/tables).");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Shared app owns Kestrel shutdown.
        return Task.CompletedTask;
    }
}
