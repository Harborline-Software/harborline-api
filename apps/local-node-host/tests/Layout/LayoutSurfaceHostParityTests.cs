using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

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
[Trait(LayoutTimingParityCollection.LaneTrait, LayoutTimingParityCollection.PerfLane)]
[Collection(LayoutTimingParityCollection.Name)]
public sealed class LayoutSurfaceHostParityTests(ITestOutputHelper output)
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
    /// The statistical timing check, shared with the audit-degraded tests (<see cref="TimingParity"/>),
    /// compares the two full distributions with a two-sample Kolmogorov-Smirnov test at significance 0.001: 60 interleaved samples a path, so the largest gap
    /// between the empirical distribution functions must stay under 1.949 * sqrt(2 / 60) = 0.356.
    /// <para>
    /// The floor is derived the way owner ruling 1 derives the production one: time 100
    /// denied resolutions with no floor on this host, as loaded as it is now, then double the worst case
    /// and round up to the 15.6 ms timer tick, never below four ticks. Each local-store operation is
    /// slowed by 5 ms, so the denied path's extra work is wider than the jitter: without the floor the
    /// distributions barely overlap and D is near 1.
    /// </para>
    /// <para>
    /// One attempt, no retries. Run 36155000912 on hosted Ubuntu failed all of #213's three attempts
    /// (D = 0.483, 0.400, 0.383 with the gate log down): the difference was real, not noise. The host now
    /// finishes every denial-side step inside the floor and ends each resolution on a spin onto the
    /// deadline, so one attempt holds.
    /// </para>
    /// </summary>
    [Theory(DisplayName = "layout-eng-31: an unauthorized caller cannot tell missing from denied by timing (two-sample KS over 60 interleaved samples a path, alpha 0.001, measured floor)")]
    [InlineData(Fault.None)]
    [InlineData(Fault.GateLogDown)]
    public async Task MissingAndDeniedTimingDistributionsAreTheSame(Fault fault)
    {
        await using var h = await Harness.CreateAsync(fault);
        h.Pipeline.Db.Delay = TimeSpan.FromMilliseconds(5);
        var detail = await TimingParity.AssertSameAsync(
            floor => ownerExists => h.ResolveAsync(h.Host(floor), Stranger, ownerExists),
            h.Pipeline.Appender.IdleAsync);
        output.WriteLine($"{fault}: {detail}");
    }

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
