using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// T-644 route-level proof of the api half of atomic activation over the REAL durable pack store and the
/// real SQLCipher packs database: preparation, the compare-and-swap under one transaction, the evidence
/// outbox, acknowledgement by evidence intent, the crash paths, the one-generation read pin, and the
/// ADR 0081 clock invariant for every handler this ticket touches.
/// </summary>
public sealed class ConfigurationActivationRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000644"));
    private static readonly DateTimeOffset Frozen = new(2026, 9, 19, 9, 0, 0, TimeSpan.Zero);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private PacksTestStore _db = null!;
    private DurablePackInstallStore _store = null!;
    private ConfigurationActivationTarget _target = null!;
    private InMemoryPackInstallAudit _audit = null!;
    private CountingClock _clock = null!;
    private TenantId _tenant;

    public async Task InitializeAsync()
    {
        _db = await PacksTestStore.CreateAsync(keySalt: 44);
        _store = new DurablePackInstallStore(_db.Factory);
        _audit = new InMemoryPackInstallAudit();
        _clock = new CountingClock(Frozen);
        var gate = TestPackGate.AllowAll();
        _target = new ConfigurationActivationTarget(_db.Factory, _store, gate, _audit);
        var activeTeam = new MutableActiveTeamAccessor(new TeamContext(TeamA, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        _tenant = NodeTenant.Resolve(activeTeam);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey("X-Test-Desktop")) http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        ConfigurationActivationRoutes.Map(_app, _target, activeTeam, gate, _clock, NullLogger.Instance);

        // The per-pack activate route this ticket touched, over the same durable store and the same clock.
        var registry = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var installer = new PackInstaller(Substitute.For<IPackVerifier>(), _store, Substitute.For<IPackContentAdmission>(), _audit, gate);
        var projector = PackProjectionTestFixture.Create(_store, registry.GetRequiredService<IEntityTypeRegistry>());
        using var key = KeyPair.Generate();
        PackInstallRoutes.Map(_app, installer, _store, new InMemoryPackTrustStore([]), PackRevocationList.Empty, activeTeam,
            gate, _clock, NullLogger.Instance, authorizingPrincipal: key.PrincipalId.ToBase64Url(), projector: projector);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
        _client.DefaultRequestHeaders.Add("X-Test-Desktop", "1");

        Seed("acme.core", "1.0.0", ["form.shared", "form.core"]);
        Seed("acme.ext", "1.0.0", ["form.shared", "form.ext"]);
        _store.RecordKeyOwnership(_tenant, "form.shared", "acme.core");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Effective_read_resolves_the_active_packs_before_any_activation()
    {
        var effective = await EffectiveAsync();
        Assert.Matches("^[0-9a-f]{64}$", effective.GetProperty("digest").GetString());
        Assert.Equal(_tenant.Value, effective.GetProperty("references").GetProperty("tenantKey").GetString());
        Assert.Equal(["acme.core", "acme.ext"], effective.GetProperty("references").GetProperty("activePackageKeys").EnumerateArray().Select(k => k.GetString()));
    }

    [Fact]
    public async Task Prepare_then_activate_commits_ownership_and_pointer_together_and_publishes_evidence()
    {
        var baseline = await DigestAsync();
        var prepared = await PrepareAsync(baseline, "form.shared", "acme.ext");
        Assert.Equal("preparing", prepared.GetProperty("status").GetString());
        var candidate = prepared.GetProperty("candidateDigest").GetString()!;
        Assert.NotEqual(baseline, candidate);
        Assert.Equal("Preparing", prepared.GetProperty("detail").GetProperty("status").GetString());

        using var activate = await ActivateAsync(baseline, candidate, "intent-1");
        var body = await activate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        Assert.Equal("effective", body.GetProperty("status").GetString());
        Assert.Equal(candidate, body.GetProperty("effectiveDigest").GetString());
        Assert.Equal("Effective", body.GetProperty("detail").GetProperty("status").GetString());

        Assert.Equal(candidate, await DigestAsync());
        Assert.Equal("acme.ext", _store.GetKeyOwnership(_tenant)["form.shared"]);
        var outbox = Assert.Single(Outbox());
        Assert.Equal("intent-1", outbox.IntentId);
        Assert.Equal(baseline, outbox.PriorDigest);
        Assert.Equal(candidate, outbox.NewDigest);
        Assert.Equal(Frozen, outbox.PublishedAt);
        Assert.Equal(Frozen, outbox.CommittedAt);
        var published = Assert.Single(_audit.Query(_tenant), entry => entry.Detail!.StartsWith("configuration.activated:intent-1:", StringComparison.Ordinal));
        Assert.Equal(candidate, published.Version);
    }

    [Fact]
    public async Task Stale_baseline_refuses_and_leaves_the_prior_generation_effective()
    {
        var baseline = await DigestAsync();
        var candidate = (await PrepareAsync(baseline, "form.shared", "acme.ext")).GetProperty("candidateDigest").GetString()!;
        using var activate = await ActivateAsync(new string('0', 64), candidate, "intent-stale");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, activate.StatusCode);
        var body = await activate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refused", body.GetProperty("status").GetString());
        Assert.Equal("configuration-baseline-stale", body.GetProperty("refusals")[0].GetProperty("code").GetString());
        Assert.Equal(baseline, await DigestAsync());
        Assert.Equal("acme.core", _store.GetKeyOwnership(_tenant)["form.shared"]);
        Assert.Empty(Outbox());
    }

    [Fact]
    public async Task Missing_or_changed_projection_refuses_and_leaves_the_prior_generation_effective()
    {
        var baseline = await DigestAsync();
        var candidate = (await PrepareAsync(baseline, "form.shared", "acme.ext")).GetProperty("candidateDigest").GetString()!;
        using (var context = _db.CreateContext())
        {
            var row = context.PreparedProjections.Single();
            row.ReferencesJson = row.ReferencesJson.Replace("acme.ext", "acme.xet", StringComparison.Ordinal);
            context.SaveChanges();
        }
        using (var changed = await ActivateAsync(baseline, candidate, "intent-changed"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
            var body = await changed.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("configuration-projection-changed", body.GetProperty("refusals")[0].GetProperty("code").GetString());
        }
        using (var context = _db.CreateContext())
        {
            context.PreparedProjections.RemoveRange(context.PreparedProjections);
            context.SaveChanges();
        }
        using (var missing = await ActivateAsync(baseline, candidate, "intent-missing"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
            var body = await missing.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("configuration-projection-missing", body.GetProperty("refusals")[0].GetProperty("code").GetString());
        }
        Assert.Equal(baseline, await DigestAsync());
        Assert.Empty(Outbox());
    }

    [Fact]
    public async Task Destination_change_after_preparation_refuses_and_leaves_the_prior_generation_effective()
    {
        // Once a pointer exists the baseline is the pointer, so a destination change is distinguishable from
        // a stale baseline: the candidate was validated against a destination that no longer exists.
        var first = (await PrepareAsync(await DigestAsync(), "form.shared", "acme.ext")).GetProperty("candidateDigest").GetString()!;
        using (var activate = await ActivateAsync(await DigestAsync(), first, "intent-first"))
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        var baseline = await DigestAsync();
        Assert.Equal(first, baseline);
        var candidate = (await PrepareAsync(baseline, "form.shared", "acme.core")).GetProperty("candidateDigest").GetString()!;
        Seed("acme.late", "1.0.0", ["form.late"]);
        using var refused = await ActivateAsync(baseline, candidate, "intent-dest");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refused", body.GetProperty("status").GetString());
        Assert.Equal("configuration-destination-incompatible", body.GetProperty("refusals")[0].GetProperty("code").GetString());
        Assert.Equal(first, await DigestAsync());
        Assert.Equal("acme.ext", _store.GetKeyOwnership(_tenant)["form.shared"]);
        Assert.Single(Outbox());
    }

    [Fact]
    public async Task Crash_after_preparation_leaves_the_pointer_untouched()
    {
        var baseline = await DigestAsync();
        _target.CrashPoint = point => { if (point == "prepared") throw new InvalidOperationException("crash:prepared"); };
        using var prepare = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.PrepareRoute, PrepareBody(baseline, "form.shared", "acme.ext"));
        Assert.Equal(HttpStatusCode.InternalServerError, prepare.StatusCode);
        _target.CrashPoint = null;
        Assert.Equal(baseline, await DigestAsync());
        Assert.Single(PreparedProjections());
        Assert.Empty(Outbox());
    }

    [Fact]
    public async Task Crash_before_commit_reaches_no_partial_state()
    {
        var baseline = await DigestAsync();
        var candidate = (await PrepareAsync(baseline, "form.shared", "acme.ext")).GetProperty("candidateDigest").GetString()!;
        foreach (var point in new[] { "ownership-written", "before-commit" })
        {
            _target.CrashPoint = at => { if (at == point) throw new InvalidOperationException("crash:" + at); };
            using var activate = await ActivateAsync(baseline, candidate, "intent-" + point);
            Assert.Equal(HttpStatusCode.InternalServerError, activate.StatusCode);
            _target.CrashPoint = null;
            Assert.Equal(baseline, await DigestAsync());
            Assert.Equal("acme.core", _store.GetKeyOwnership(_tenant)["form.shared"]);
            Assert.Empty(Outbox());
            Assert.Empty(EffectiveRows());
        }
    }

    [Fact]
    public async Task Lost_acknowledgement_is_indeterminate_and_a_rerequest_is_answered_from_the_outbox()
    {
        var baseline = await DigestAsync();
        var candidate = (await PrepareAsync(baseline, "form.shared", "acme.ext")).GetProperty("candidateDigest").GetString()!;
        _target.CrashPoint = at => { if (at == "after-commit") throw new InvalidOperationException("crash:ack"); };
        using (var lost = await ActivateAsync(baseline, candidate, "intent-ack"))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
            var body = await lost.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("indeterminate", body.GetProperty("status").GetString());
            Assert.Equal("intent-ack", body.GetProperty("evidenceIntentId").GetString());
        }
        _target.CrashPoint = null;
        // The commit was durable: the pointer moved and the evidence intent is in the outbox, unpublished.
        Assert.Equal(candidate, await DigestAsync());
        var row = Assert.Single(Outbox());
        Assert.Null(row.PublishedAt);

        // Same intent, same inputs: acknowledged from the outbox, not switched again and not refused as stale.
        using (var again = await ActivateAsync(baseline, candidate, "intent-ack"))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            var body = await again.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("effective", body.GetProperty("status").GetString());
            Assert.True(body.GetProperty("acknowledged").GetBoolean());
            Assert.Equal(candidate, body.GetProperty("effectiveDigest").GetString());
        }
        // Same intent, different inputs: reuse is refused.
        using (var reused = await ActivateAsync(candidate, candidate, "intent-ack"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
            var body = await reused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("configuration-evidence-intent-reused", body.GetProperty("refusals")[0].GetProperty("code").GetString());
        }
        // The row stays pending: the evidence intent identity is the recovery handle (T-587), and the api
        // never republishes without the decision that admitted the switch.
        Assert.Null(Assert.Single(Outbox()).PublishedAt);
    }

    [Fact]
    public async Task Crash_before_publication_leaves_the_committed_switch_effective_and_the_outbox_row_pending()
    {
        var baseline = await DigestAsync();
        var candidate = (await PrepareAsync(baseline, "form.shared", "acme.ext")).GetProperty("candidateDigest").GetString()!;
        _target.CrashPoint = at => { if (at == "before-publish") throw new InvalidOperationException("crash:publish"); };
        using (var activate = await ActivateAsync(baseline, candidate, "intent-pub"))
        {
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        }
        _target.CrashPoint = null;
        Assert.Equal(candidate, await DigestAsync());
        Assert.Null(Assert.Single(Outbox()).PublishedAt);
        Assert.DoesNotContain(_audit.Query(_tenant), entry => entry.Detail!.StartsWith("configuration.activated:", StringComparison.Ordinal));
        // Committed, unpublished, and still acknowledgeable: the same intent answers from the outbox.
        using var again = await ActivateAsync(baseline, candidate, "intent-pub");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("acknowledged").GetBoolean());
        Assert.Null(Assert.Single(Outbox()).PublishedAt);
    }

    [Fact]
    public async Task Concurrent_reads_resolve_against_one_generation_while_an_activation_commits()
    {
        var baseline = await DigestAsync();
        var candidate = (await PrepareAsync(baseline, "form.shared", "acme.ext")).GetProperty("candidateDigest").GetString()!;
        using var reached = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        _target.CrashPoint = at =>
        {
            if (at != "ownership-written") return;
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        var activation = ActivateAsync(baseline, candidate, "intent-concurrent");
        Assert.True(reached.Wait(TimeSpan.FromSeconds(30)));
        // Ownership is already written inside the open transaction; a reader must not observe it.
        var read = EffectiveAsync();
        var raced = await Task.WhenAny(read, Task.Delay(400));
        Assert.NotSame(read, raced);
        release.Set();
        using var committed = await activation;
        _target.CrashPoint = null;
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var effective = await read;
        var digest = effective.GetProperty("digest").GetString()!;
        Assert.Equal(candidate, digest);
        Assert.Equal(digest, ConfigurationActivationTarget.FromReferences(effective.GetProperty("references").GetRawText()).Digest);
    }

    [Theory]
    [InlineData("effective")]
    [InlineData("prepare")]
    [InlineData("activate")]
    [InlineData("pack-activate")]
    public async Task Every_handler_observes_the_admitted_instant_exactly_once(string handler)
    {
        var baseline = await DigestAsync();
        var candidate = (await PrepareAsync(baseline, "form.shared", "acme.ext")).GetProperty("candidateDigest").GetString()!;
        Seed("acme.draft", "1.0.0", [], activate: false);
        _clock.Arm();
        using var response = handler switch
        {
            "effective" => await _client.GetAsync(ConfigurationActivationRoutes.EffectiveRoute),
            "prepare" => await _client.PostAsJsonAsync(ConfigurationActivationRoutes.PrepareRoute, PrepareBody(baseline, "form.shared", "acme.ext")),
            "activate" => await ActivateAsync(baseline, candidate, "intent-clock"),
            "pack-activate" => await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute, new { packKey = "acme.draft", version = "1.0.0" }),
            _ => throw new ArgumentOutOfRangeException(nameof(handler)),
        };
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // A second read would have returned Frozen + 1 day; every persisted instant carries the first read.
        Assert.Equal(1, _clock.Reads);
        var persisted = handler switch
        {
            "prepare" => PreparedProjections().Select(row => row.PreparedAt),
            "activate" => Outbox().Select(row => row.CommittedAt).Concat(EffectiveRows().Select(row => row.ActivatedAt)),
            "pack-activate" => _audit.Query(_tenant).Where(entry => entry.PackKey == "acme.draft").Select(entry => entry.OccurredAtUtc),
            _ => [],
        };
        Assert.All(persisted, instant => Assert.Equal(Frozen, instant));
        if (handler is not "effective") Assert.NotEmpty(persisted);
    }

    private void Seed(string packKey, string version, string[] contentKeys, bool activate = true)
    {
        var seeds = contentKeys.Select(key => new PackSeedItem(key, PackContentKind.FormDefinition, "1",
            $$"""{"id":"{{key}}","pack":"{{packKey}}"}""", Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(key + packKey)))).ToArray();
        var pack = new InstalledPack(packKey, version, PackScopeTier.Horizontal, PackLifecycleState.Draft, seeds,
            new Dictionary<string, int>(), Frozen, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, Array.Empty<PackDependencyRef>());
        _store.Commit(new PackInstallTransaction(_tenant, pack, new PackInstallWatermark(packKey, version, new Dictionary<string, int>()), []));
        if (activate) _store.Activate(_tenant, packKey, version);
    }

    private static object PrepareBody(string baseline, string definitionKey, string packageKey) => new
    {
        expectedBaselineDigest = baseline,
        activePackageKeys = new[] { "acme.core", "acme.ext" },
        ownership = new[] { new { definitionKey, packageKey } },
    };

    private async Task<JsonElement> PrepareAsync(string baseline, string definitionKey, string packageKey)
    {
        using var response = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.PrepareRoute, PrepareBody(baseline, definitionKey, packageKey));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> ActivateAsync(string baseline, string candidate, string intentId) =>
        _client.PostAsJsonAsync(ConfigurationActivationRoutes.ActivateRoute, new
        {
            expectedBaselineDigest = baseline,
            candidateDigest = candidate,
            evidenceIntent = new { id = intentId, reason = "T-644 route test" },
        });

    private async Task<JsonElement> EffectiveAsync()
    {
        using var response = await _client.GetAsync(ConfigurationActivationRoutes.EffectiveRoute);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> DigestAsync() => (await EffectiveAsync()).GetProperty("digest").GetString()!;

    private List<ConfigurationEvidenceOutboxRow> Outbox()
    {
        using var context = _db.CreateContext();
        return context.EvidenceOutbox.AsNoTracking().ToList();
    }

    private List<ConfigurationPreparedProjectionRow> PreparedProjections()
    {
        using var context = _db.CreateContext();
        return context.PreparedProjections.AsNoTracking().ToList();
    }

    private List<ConfigurationEffectiveGenerationRow> EffectiveRows()
    {
        using var context = _db.CreateContext();
        return context.EffectiveGenerations.AsNoTracking().ToList();
    }

    /// <summary>Returns the admitted instant on the first read of an armed act and a day later on any further read.</summary>
    private sealed class CountingClock(DateTimeOffset admitted) : TimeProvider
    {
        private int _reads;
        private bool _armed;
        public int Reads => Volatile.Read(ref _reads);
        public void Arm() { Interlocked.Exchange(ref _reads, 0); _armed = true; }
        public override DateTimeOffset GetUtcNow() =>
            !_armed || Interlocked.Increment(ref _reads) == 1 ? admitted : admitted.AddDays(1);
    }

    private sealed class MutableActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
