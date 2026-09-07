using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Runtime.DependencyInjection;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.LocalNodeHost.Health;

using Ed25519Signer = Harborline.Api.Kernel.Security.Crypto.Ed25519Signer;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// THE HOST-BOOT SMOKE TEST (cerebrum [2026-06-21] DECISIVE cross-machine verify — BLOCKER-1 + its MANDATE:
/// "a host-boot smoke test"). The cross-machine verify found that <c>local-node-host</c> did NOT BOOT on EITHER
/// platform: a DI circular dependency on <c>IEnumerable&lt;IHostedService&gt;</c> crashed <c>Host.StartAsync</c>
/// unconditionally — and NO test had ever built the real <see cref="IHost"/> and started it, so it slipped past
/// every check. This closes that class of bug: it builds a real host with the PRODUCTION pairing that closes the
/// cycle (<see cref="HostedSyncStatusApiEndpoint"/>, an <see cref="IHostedService"/>, consuming
/// <see cref="IEnrollmentRebindStatus"/>, which the worker registration supplies) and asserts <c>StartAsync</c>
/// reaches a running host.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cycle, exactly.</b> The host starts hosted services by resolving <c>IEnumerable&lt;IHostedService&gt;</c>.
/// That set includes <see cref="HostedSyncStatusApiEndpoint"/>, whose constructor takes an optional
/// <see cref="IEnrollmentRebindStatus"/>. The BUGGY registration resolved that interface via
/// <c>sp.GetServices&lt;IHostedService&gt;().First(s =&gt; s is LocalNodeWorker)</c> — which re-enters the very
/// <c>IEnumerable&lt;IHostedService&gt;</c> resolution → a circular dependency. The FIX
/// (<see cref="LocalNodeWorkerRegistration.AddLocalNodeWorkerAndRebindStatus"/>) registers
/// <see cref="LocalNodeWorker"/> once as a concrete singleton and bridges both facets to it (no enumeration).
/// </para>
/// <para>
/// <b>Why it boots the REAL trio.</b> The two tests below register the SAME pairing the composition root does —
/// the production worker registration helper + the real <see cref="HostedSyncStatusApiEndpoint"/> over a real
/// <see cref="SharedHostedWebApp"/> (Kestrel on an OS-assigned port, <c>HealthPort = 0</c>) — atop the real
/// kernel-runtime graph (<see cref="ServiceCollectionExtensions.AddHarborlineKernelRuntime"/> +
/// <see cref="DefaultTeamServiceRegistrar"/>) the worker needs. No active team is materialized (the lightest boot
/// that still resolves + starts the cycle-prone hosted-service set) — the cycle is a RESOLUTION defect, so it
/// manifests before any team work. The <see cref="Buggy_Registration_Cycles_At_StartAsync_Bite"/> sibling
/// registers the OLD topology and proves the bite is real (red pre-fix / green post-fix).
/// </para>
/// </remarks>
public sealed class HostBootSmokeTests
{
    private static (HostApplicationBuilder builder, string dataDir) NewHostBuilder(
        string? existingDataDir = null)
    {
        var dataDir = existingDataDir ??
            Path.Combine(Path.GetTempPath(), $"harborline-hostboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);

        var builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddLogging();
        builder.Services.AddTestKernelClock();

        // LocalNodeOptions: dynamic health port (HealthPort = 0 ⇒ OS-assigned), no caller-auth token (dev/test
        // parity — un-enforced), the temp data dir. No active team is configured (the boot tolerates none).
        builder.Services.Configure<LocalNodeOptions>(o =>
        {
            o.HealthPort = 0;
            o.DataDirectory = dataDir;
            o.SessionToken = null;
        });

        // Real kernel-runtime graph the worker depends on: IPluginRegistry + INodeHost, and a real
        // ITeamContextFactory + IActiveTeamAccessor via the default per-team registrar (no listener, no peers).
        builder.Services.AddHarborlineKernelRuntime();

        var signer = new Ed25519Signer();
        var rootSeed = new byte[32]; // deterministic 32-byte Ed25519 seed (exact length required).
        for (var i = 0; i < rootSeed.Length; i++) rootSeed[i] = (byte)(i + 1);
        var (rootPub, rootPriv) = signer.GenerateFromSeed(rootSeed);
        var nodeId = Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant();
        var rootIdentity = new NodeIdentity(nodeId, rootPub, rootPriv);
        builder.Services.AddHarborlineMultiTeam(DefaultTeamServiceRegistrar.Compose(
            dataDirectory: dataDir,
            subkeyDerivation: new TeamSubkeyDerivation(signer),
            rootIdentity: rootIdentity,
            sqlCipherKeyDerivation: new SqlCipherKeyDerivation(),
            listenForPeers: false,
            roundIntervalSeconds: 1));

        // The shared Kestrel app + its caller-auth token holder (the production singletons the sync-status
        // endpoint maps its route onto).
        builder.Services.AddSingleton(new NodeCallerSessionToken(null));
        builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry>();
        builder.Services.AddSingleton(sp => new SharedHostedWebApp(
            sp,
            sp.GetRequiredService<IOptions<LocalNodeOptions>>(),
            sp.GetRequiredService<Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry>(),
            NullLogger<SharedHostedWebApp>.Instance,
            sp.GetRequiredService<TimeProvider>()));

        return (builder, dataDir);
    }

    [Fact(DisplayName = "F3 existing-install: Seal failure degrades evidence but cannot block Kestrel start")]
    public async Task ExistingInstall_SealFailure_Degrades_And_Kestrel_Starts()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"harborline-hostboot-existing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var databasePath = Path.Combine(dataDir, "existing-install.db");

        try
        {
            await using (var database = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await database.OpenAsync();
                await using var seed = database.CreateCommand();
                seed.CommandText =
                    "CREATE TABLE install_marker (id INTEGER PRIMARY KEY, value TEXT NOT NULL);" +
                    "INSERT INTO install_marker (id, value) VALUES (1, 'pre-existing');";
                await seed.ExecuteNonQueryAsync();
            }

            var (builder, _) = NewHostBuilder(dataDir);
            builder.Services.AddLocalNodeWorkerAndRebindStatus();
            builder.Services.AddHostedService<HostedSyncStatusApiEndpoint>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SharedHostedWebApp>());

            using var host = builder.Build();
            var sharedApp = host.Services.GetRequiredService<SharedHostedWebApp>();
            var duplicateRoute = $"/f3-duplicate-{Guid.NewGuid():N}";
            sharedApp.MapApiRoutes(app =>
            {
                app.MapGet(duplicateRoute, () => "first");
                app.MapGet(duplicateRoute, () => "second");
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.StartAsync(cts.Token);

            Assert.NotNull(sharedApp.SelectedUrl);
            Assert.False(sharedApp.ExecutableEndpointRegistry.IsSealed);

            await using (var database = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await database.OpenAsync(cts.Token);
                await using var verify = database.CreateCommand();
                verify.CommandText = "SELECT value FROM install_marker WHERE id = 1";
                Assert.Equal("pre-existing", await verify.ExecuteScalarAsync(cts.Token));
            }

            await host.StopAsync(cts.Token);
        }
        finally
        {
            TryDelete(dataDir);
        }
    }

    [Fact(DisplayName = "host-boot BLOCKER-1: the REAL IHost (worker + sync-status endpoint) starts — no DI cycle")]
    public async Task Real_Host_Starts_Without_DiCycle()
    {
        var (builder, dataDir) = NewHostBuilder();
        try
        {
            // THE PRODUCTION PAIRING that the cross-machine verify proved did not boot:
            //   (a) the worker registered ONCE + bridged to IEnrollmentRebindStatus (the FIX), and
            //   (b) HostedSyncStatusApiEndpoint — the IHostedService consumer that closes the cycle.
            builder.Services.AddLocalNodeWorkerAndRebindStatus();
            builder.Services.AddHostedService<HostedSyncStatusApiEndpoint>();
            // The shared app starts LAST in production; register it as a hosted service so Kestrel actually starts.
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SharedHostedWebApp>());

            using var host = builder.Build();

            // THE ASSERTION: StartAsync REACHES a running host. Pre-fix this threw a circular-dependency
            // InvalidOperationException while resolving IEnumerable<IHostedService>. A short timeout guards against
            // a hang.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.StartAsync(cts.Token);

            // Both worker facets resolve to the SAME singleton instance (the bridge is correct, not two objects).
            var worker = host.Services.GetRequiredService<LocalNodeWorker>();
            var rebindStatus = host.Services.GetRequiredService<IEnrollmentRebindStatus>();
            Assert.Same(worker, rebindStatus);
            // Boot posture: no rebind has run ⇒ the rebind status is healthy.
            Assert.True(rebindStatus.LastRebind.IsHealthy);

            var endpointRegistry = host.Services.GetRequiredService<
                Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry>();
            var sharedApp = host.Services.GetRequiredService<SharedHostedWebApp>();
            Assert.Same(endpointRegistry, sharedApp.ExecutableEndpointRegistry);
            Assert.True(endpointRegistry.IsSealed);
            Assert.Contains(
                endpointRegistry.Current.Endpoints,
                endpoint => string.Equals(
                    endpoint.RoutePattern,
                    SyncStatusRoutes.RouteBase,
                    StringComparison.Ordinal));

            await host.StopAsync(cts.Token);
        }
        finally
        {
            TryDelete(dataDir);
        }
    }

    [Fact(DisplayName = "host-boot BLOCKER-1 (bite): the OLD GetServices<IHostedService> bridge CYCLES at StartAsync")]
    public async Task Buggy_Registration_Cycles_At_StartAsync_Bite()
    {
        var (builder, dataDir) = NewHostBuilder();
        try
        {
            // THE PRE-FIX TOPOLOGY, reproduced verbatim: the worker registered only as IHostedService, and
            // IEnrollmentRebindStatus resolved by ENUMERATING IEnumerable<IHostedService> — the re-entrant
            // resolution that cycles once HostedSyncStatusApiEndpoint (itself an IHostedService) consumes it.
            builder.Services.AddHostedService<LocalNodeWorker>();
            builder.Services.AddSingleton<IEnrollmentRebindStatus>(sp =>
                (LocalNodeWorker)sp.GetServices<IHostedService>().First(s => s is LocalNodeWorker));
            builder.Services.AddHostedService<HostedSyncStatusApiEndpoint>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SharedHostedWebApp>());

            using var host = builder.Build();

            // THE BITE: starting the host resolves IEnumerable<IHostedService>, the buggy factory re-enters that
            // resolution → the framework throws a circular-dependency InvalidOperationException. (This is the exact
            // crash the cross-machine verify hit at Host.StartAsync; the test above proves the fix removes it.)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => host.StartAsync(cts.Token));
            Assert.Contains("circular", ex.Message, StringComparison.OrdinalIgnoreCase);
            // No StopAsync: StartAsync threw before the host's hosted-service list was built, so there is nothing
            // started to stop (calling StopAsync on a never-started host throws on its own null bookkeeping).
        }
        finally
        {
            TryDelete(dataDir);
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
