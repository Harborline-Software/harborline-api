using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Layout;
using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Blocks.LayoutRuntime;

using static Harborline.Api.LocalNodeHost.Tests.Layout.LayoutDenialTestKit;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>
/// T-731 — DES-0052 layout-eng-31 (host half): at the host boundary, for an unauthorized caller, a missing
/// optional related target and a denied one are indistinguishable in shape, in fault classification and in
/// timing, on the ordinary path and on both outbox fault paths. Related traversals go through the REAL gate
/// over the seeded production definitions. In-process: T-735 wires the HTTP route.
/// </summary>
public sealed class LayoutSurfaceHostParityTests
{
    private static readonly ActorId Stranger = new("stranger-731");
    private static readonly ActorId Member = new("member-731");

    public enum Fault { None, GateLogDown, OutboxDown }

    [Theory(DisplayName = "layout-eng-31: an unauthorized caller gets the same resolution for a missing and a denied target, with no fault, on every outbox path")]
    [InlineData(Fault.None)]
    [InlineData(Fault.GateLogDown)]
    [InlineData(Fault.OutboxDown)]
    public async Task MissingAndDeniedReturnTheSameResolution(Fault fault)
    {
        await using var h = await Harness.CreateAsync(fault);
        var host = h.Host(TimeSpan.Zero);

        var missing = await h.ResolveAsync(host, Stranger, ownerExists: false);
        await h.Pipeline.Appender.IdleAsync();
        Assert.Empty(await RowsAsync(h.Pipeline.Trail));
        var denied = await h.ResolveAsync(host, Stranger, ownerExists: true);
        await h.Pipeline.Appender.IdleAsync();

        Assert.Equal(Describe(missing), Describe(denied));
        Assert.Empty(denied.Refusals);
        h.Pipeline.Db.Fault = false;
        var logged = (await RowsAsync(h.Pipeline.Trail)).Count;
        var outboxed = (await h.Pipeline.Outbox.ListUnresolvedAsync()).Count;
        Assert.Equal(fault switch { Fault.None => (1, 0), Fault.GateLogDown => (0, 1), _ => (0, 0) }, (logged, outboxed));
        // The control: a caller allowed records:read sees the owner, so the equality above is not vacuous.
        var allowed = await h.ResolveAsync(host, Member, ownerExists: true);
        Assert.Contains(allowed.Blocks, block => block.BlockId == "owner-name");
    }

    [Fact(DisplayName = "layout-eng-31: the response floor is configuration, 250 ms by default (a placeholder), and a resolution that overruns it raises the health alert")]
    public async Task AnOverrunOfTheFloorRaisesTheHealthAlert()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), new LayoutSurfaceOptions().ResponseFloor);
        await using var h = await Harness.CreateAsync(Fault.None);

        await h.ResolveAsync(h.Host(LayoutSurfaceOptions.DefaultResponseFloor), Stranger, ownerExists: true);
        await h.Pipeline.Appender.IdleAsync();
        Assert.Equal(HealthStatus.Healthy, (await h.Pipeline.HealthAsync()).Status);

        h.Pipeline.Db.Delay = TimeSpan.FromMilliseconds(40);
        await h.ResolveAsync(h.Host(TimeSpan.FromMilliseconds(5)), Stranger, ownerExists: true);
        await h.Pipeline.Appender.IdleAsync();

        var alert = await h.Pipeline.HealthAsync();
        Assert.Equal(HealthStatus.Degraded, alert.Status);
        Assert.Equal(1L, alert.Data["floorOverruns"]);
    }

    /// <summary>
    /// The statistical timing check compares the two full distributions with a two-sample
    /// Kolmogorov-Smirnov test at significance 0.001: 60 interleaved samples a path, so the largest gap
    /// between the empirical distribution functions must stay under 1.949 * sqrt(2 / 60) = 0.356.
    /// <para>
    /// The floor is derived in the test the way owner ruling 1 derives the production one: time 100
    /// denied resolutions with no floor on this host, as loaded as it is now, then double the worst case
    /// and round up to the 15.6 ms timer tick, never below four ticks. A loaded CI host therefore gets a longer floor instead of a
    /// flaky test. Each local-store operation is slowed by 5 ms, so the denied path's extra work is wider
    /// than the jitter: without the floor the distributions barely overlap and D is near 1. With it, both
    /// paths end at the same deadline plus timer overshoot, which does not depend on the path. Samples
    /// alternate order pair by pair, so load falls on both alike. A false alarm under the null has
    /// probability 0.001 per run.
    /// </para>
    /// </summary>
    [Theory(DisplayName = "layout-eng-31: an unauthorized caller cannot tell missing from denied by timing (two-sample KS over 60 interleaved samples a path, alpha 0.001, measured floor)")]
    [InlineData(Fault.None)]
    [InlineData(Fault.GateLogDown)]
    public async Task MissingAndDeniedTimingDistributionsAreTheSame(Fault fault)
    {
        const int samples = 60;
        const double critical = 1.949 * 0.1825741858; // c(0.001) * sqrt((n + m) / (n * m)), n = m = 60
        const double tick = 15.625;
        await using var h = await Harness.CreateAsync(fault);
        h.Pipeline.Db.Delay = TimeSpan.FromMilliseconds(5);

        var calibration = h.Host(TimeSpan.Zero);
        for (var i = 0; i < 5; i++) await h.ResolveAsync(calibration, Stranger, ownerExists: true); // JIT warm-up, discarded
        var worst = 0.0;
        for (var i = 0; i < 100; i++)
        {
            var clock = Stopwatch.StartNew();
            await h.ResolveAsync(calibration, Stranger, ownerExists: true);
            worst = Math.Max(worst, clock.Elapsed.TotalMilliseconds);
        }
        await h.Pipeline.Appender.IdleAsync();
        // The worst of 100 stands in for the p99.9; four ticks is the least floor a coarse timer can hold.
        var floor = TimeSpan.FromMilliseconds(Math.Max(4, Math.Ceiling(2 * worst / tick)) * tick);
        var host = h.Host(floor);

        var missing = new List<double>();
        var denied = new List<double>();
        for (var i = 0; i < samples; i++)
        {
            foreach (var ownerExists in i % 2 == 0 ? new[] { false, true } : [true, false])
            {
                var clock = Stopwatch.StartNew();
                await h.ResolveAsync(host, Stranger, ownerExists);
                (ownerExists ? denied : missing).Add(clock.Elapsed.TotalMilliseconds);
            }
        }
        await h.Pipeline.Appender.IdleAsync();

        var d = KolmogorovSmirnov(missing, denied);
        Assert.True(d <= critical,
            $"KS D = {d:F3} > {critical:F3} at floor {floor.TotalMilliseconds} ms; missing p50 {Quantile(missing, 0.5):F1} "
            + $"p90 {Quantile(missing, 0.9):F1} ms, denied p50 {Quantile(denied, 0.5):F1} p90 {Quantile(denied, 0.9):F1} ms");
    }

    /// <summary>The largest vertical gap between the two empirical distribution functions.</summary>
    private static double KolmogorovSmirnov(List<double> a, List<double> b)
    {
        var x = a.Order().ToArray();
        var y = b.Order().ToArray();
        double d = 0;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var t = Math.Min(x[i], y[j]);
            while (i < x.Length && x[i] <= t) i++;
            while (j < y.Length && y[j] <= t) j++;
            d = Math.Max(d, Math.Abs((double)i / x.Length - (double)j / y.Length));
        }
        return d;
    }

    private static double Quantile(List<double> samples, double q) => samples.Order().ElementAt((int)(q * (samples.Count - 1)));

    private static string Describe(LayoutBindingResolution resolution) => JsonSerializer.Serialize(new
    {
        blocks = resolution.Blocks.Select(block => new { block.BlockId, block.Kind, block.BindingKind, block.Name, Value = block.Value?.ToJsonString(), block.RowId }),
        resolution.Refusals,
        resolution.Hidden,
    });

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AuthorizationGate _gate;
        private int _request;

        private Harness(ServiceProvider provider, Fault fault)
        {
            _provider = provider;
            _gate = new AuthorizationGate(
                provider.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
                provider.GetRequiredService<IRecordStandingResolver>(),
                provider.GetRequiredService<IAuthorizationDefinitionAtomReader>());
            Pipeline = new Pipeline(new SwitchableTrail { Down = fault == Fault.GateLogDown });
            Pipeline.Db.Fault = fault == Fault.OutboxDown;
        }

        public Pipeline Pipeline { get; }

        public static async Task<Harness> CreateAsync(Fault fault)
        {
            var services = new ServiceCollection();
            services.AddSingleton(TestAuthorization.AllowGate());
            services.AddAccessGrantModule();
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
                .InstallAsync(Tenant, At, AuthorizationSeedProfile.Production);
            await provider.GetRequiredService<IGrantStore>().AppendAsync(Tenant, new AccessGrant(
                GrantId.New(), Tenant, Member, AccessGrantAuthorizationSeed.MemberRole, ScopeExpression.Parse("/"),
                GrantResidency.Cache, new GrantValidity(At.AddHours(-1)), GranterKind.Person, new ActorId("tenant-admin"),
                At.AddHours(-1), new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual),
                    new ActorId("tenant-admin")), At.AddHours(-1)));
            return new Harness(provider, fault);
        }

        public LayoutSurfaceHost Host(TimeSpan floor) => new(_gate, Pipeline.Outbox, Pipeline.Appender, Pipeline.Alarms,
            new SystemTimeAt(), NullLogger.Instance, new LayoutSurfaceOptions { ResponseFloor = floor });

        public Task<LayoutBindingResolution> ResolveAsync(LayoutSurfaceHost host, ActorId caller, bool ownerExists)
        {
            var owner = new LayoutRelatedRecord(Owner, new LayoutBindingScope(null, null,
                new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["name"] = JsonValue.Create("Contoso") }));
            return host.ResolveAsync(Surface(), new OutcomeSources(LayoutRelatedResult.Absent),
                new Dictionary<string, Func<LayoutBindingScope, LayoutRelatedRecord?>>(StringComparer.Ordinal)
                {
                    ["invoice.owner"] = _ => ownerExists ? owner : null,
                },
                LayoutBindingScope.Root(new Dictionary<string, JsonNode?>(StringComparer.Ordinal)),
                Tenant, caller, $"request-{Interlocked.Increment(ref _request)}");
        }

        public async ValueTask DisposeAsync()
        {
            await Pipeline.Appender.IdleAsync();
            await _provider.DisposeAsync();
            Pipeline.Dispose();
        }
    }

    /// <summary>Real elapsed time for the floor, with the seeded grants' instant as "now".</summary>
    private sealed class SystemTimeAt : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At;
    }
}
