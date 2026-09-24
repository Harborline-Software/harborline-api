using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Blocks.LayoutRuntime;

using static Harborline.Api.LocalNodeHost.Tests.Layout.LayoutDenialGateLogTests;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>
/// T-731 slice 3 — DES-0052 layout-eng-31 (host half): at the host boundary, for an unauthorized caller,
/// a missing optional related target and a denied one are indistinguishable in shape, in fault
/// classification and in timing. Related traversals go through the REAL gate over the seeded
/// production definitions.
/// </summary>
public sealed class LayoutSurfaceHostParityTests
{
    private static readonly ActorId Stranger = new("stranger-731");
    private static readonly ActorId Member = new("member-731");

    [Fact(DisplayName = "layout-eng-31: an unauthorized caller gets the same resolution for a missing and a denied target, and only the denial is logged")]
    public async Task MissingAndDeniedReturnTheSameResolution()
    {
        await using var h = await Harness.CreateAsync();
        var trail = new InMemoryAuditTrail();
        var host = h.Host(trail, TimeSpan.Zero);

        var missing = await h.ResolveAsync(host, Stranger, ownerExists: false);
        Assert.Empty(await RowsAsync(trail));
        var denied = await h.ResolveAsync(host, Stranger, ownerExists: true);

        Assert.Equal(Describe(missing), Describe(denied));
        Assert.Empty(denied.Refusals);
        Assert.Single(await RowsAsync(trail));
        // The control: a caller allowed records:read sees the owner, so the equality above is not vacuous.
        var allowed = await h.ResolveAsync(host, Member, ownerExists: true);
        Assert.Contains(allowed.Blocks, block => block.BlockId == "owner-name");
    }

    [Fact(DisplayName = "layout-eng-31: a gate-log append fault does not change the error classification or the resolution")]
    public async Task AnAppendFaultDoesNotChangeTheResolution()
    {
        await using var h = await Harness.CreateAsync();
        var host = h.Host(new FaultingTrail(), TimeSpan.Zero);

        var missing = await h.ResolveAsync(host, Stranger, ownerExists: false);
        var denied = await h.ResolveAsync(host, Stranger, ownerExists: true);

        Assert.Equal(Describe(missing), Describe(denied));
    }

    /// <summary>
    /// The statistical timing check. Tolerance: the medians of 20 interleaved pairs differ by at most
    /// 25 ms. The gate log here takes 60 ms per append, standing in for a durable log, so the leak the
    /// floor must hide is larger than the tolerance: without the floor the denied median is about 60 ms
    /// slower and the check fails. With a 150 ms floor both paths finish at the floor plus timer
    /// overshoot, which is path-independent; interleaving puts host load on both paths alike, and the
    /// median ignores up to nine slow samples a side. The tolerance is above one Windows timer tick
    /// (15.6 ms), so quantization alone cannot fail it.
    /// </summary>
    [Fact(DisplayName = "layout-eng-31: an unauthorized caller cannot tell missing from denied by timing (median of 20 interleaved pairs within 25 ms)")]
    public async Task MissingAndDeniedTakeTheSameTime()
    {
        const int pairs = 20;
        var tolerance = TimeSpan.FromMilliseconds(25);
        await using var h = await Harness.CreateAsync();
        var trail = new SlowTrail(TimeSpan.FromMilliseconds(60));
        var host = h.Host(trail, TimeSpan.FromMilliseconds(150));

        var missing = new List<double>();
        var denied = new List<double>();
        await h.ResolveAsync(host, Stranger, ownerExists: true); // warm-up, discarded
        for (var i = 0; i < pairs; i++)
        {
            // Alternate which path goes first so drift and load bursts fall on both.
            foreach (var ownerExists in i % 2 == 0 ? new[] { false, true } : [true, false])
            {
                var clock = Stopwatch.StartNew();
                await h.ResolveAsync(host, Stranger, ownerExists);
                (ownerExists ? denied : missing).Add(clock.Elapsed.TotalMilliseconds);
            }
        }

        Assert.Equal(pairs + 1, (await RowsAsync(trail)).Count);
        var difference = Math.Abs(Median(denied) - Median(missing));
        Assert.True(difference <= tolerance.TotalMilliseconds,
            $"median denied {Median(denied):F1} ms, median missing {Median(missing):F1} ms, difference {difference:F1} ms > {tolerance.TotalMilliseconds} ms");
    }

    private static double Median(List<double> samples)
    {
        var sorted = samples.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
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
        private readonly KeyPair _keys = KeyPair.Generate();
        private readonly AuthorizationGate _gate;
        private int _request;

        private Harness(ServiceProvider provider)
        {
            _provider = provider;
            _gate = new AuthorizationGate(
                provider.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
                provider.GetRequiredService<IRecordStandingResolver>(),
                provider.GetRequiredService<IAuthorizationDefinitionAtomReader>());
        }

        public static async Task<Harness> CreateAsync()
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
            return new Harness(provider);
        }

        public LayoutSurfaceHost Host(IAuditTrail trail, TimeSpan floor)
            => new(_gate, trail, new Ed25519Signer(_keys), new SystemTimeAt(), NullLogger.Instance, floor);

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
            await _provider.DisposeAsync();
            _keys.Dispose();
        }
    }

    /// <summary>Real elapsed time for the floor, with the seeded grants' instant as "now".</summary>
    private sealed class SystemTimeAt : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At;
    }

    private sealed class FaultingTrail : IAuditTrail
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
            => throw new InvalidOperationException("the gate log is down");

        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default)
            => AsyncEnumerable.Empty<AuditRecord>();
    }

    /// <summary>A gate log whose append costs a fixed delay, like a durable store.</summary>
    private sealed class SlowTrail(TimeSpan delay) : IAuditTrail
    {
        private readonly InMemoryAuditTrail _inner = new();

        public async ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            await _inner.AppendAsync(record, ct);
        }

        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default)
            => _inner.QueryAsync(query, ct);
    }
}
