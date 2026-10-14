using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// The release-wide atomic install through <c>POST /configuration/releases/{digest}/install</c>, over the same
/// production composition as <see cref="ReleasedPackAtomicInstallTests"/>: the route's status, the overlays and the
/// absent releasing-pack rows are read back from a restart of the encrypted database.
/// </summary>
public sealed class ReleasedPackAtomicInstallRouteTests : IAsyncLifetime
{
    private ReleasedPackAtomicFixture _fixture = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly CapturingLogger _log = new();

    public async Task InitializeAsync()
    {
        _fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var builder = WebApplication.CreateBuilder();
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey("X-Test-Desktop")) http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        ConfigurationProposalRoutes.Map(_app, _fixture.Proposals, _fixture.ActiveTeam, TestPackGate.AllowAll(),
            new FrozenClock(ReleasedPackAtomicFixture.Frozen), _log, _fixture.Releases);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
        _client.DefaultRequestHeaders.Add("X-Test-Desktop", "1");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _fixture.DisposeAsync();
    }

    /// <summary>
    /// Regression foreign-only-success: two legitimate narrowings of acme.finance and nothing of the release's own.
    /// The route answers 200 "narrowed" naming both, each overlay is the literal RFC 7396 removal, and no releasing
    /// pack seed, version, watermark or projection admission exists.
    /// </summary>
    [Fact]
    public async Task Foreign_only_release_through_the_route_is_narrowed_with_both_literal_overlays()
    {
        var digest = await _fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", """{"id":"forms/invoice"}"""),
             ("records/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", """{"id":"records/invoice"}""")],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));
        var outbox = await _fixture.OutboxCountAsync();

        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), new { });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
        Assert.Equal("narrowed", body.GetProperty("status").GetString());
        Assert.Equal(string.Empty, body.GetProperty("packKey").GetString());
        Assert.Empty(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(new[] { "forms/invoice", "records/invoice" },
            body.GetProperty("narrowed").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal((0, 0, 2), (_fixture.Exporter.Exports, _fixture.Installer.Installs, _fixture.Installer.Narrowings));

        await using var restart = SearchTestStore.Reopen(_fixture.Harness.Store);
        var reopened = new DurablePackInstallStore(restart.PacksFactory);
        Assert.Equal(
            new[] { ("forms/invoice", """{"pack":null}"""), ("records/invoice", """{"pack":null}""") },
            reopened.GetOverrides(_fixture.Tenant, ReleasedPackAtomicFixture.Finance)
                .Select(row => (row.ContentKey, row.OverlayPatch.ToJsonString())).ToArray());
        Assert.DoesNotContain(reopened.ListInstalled(_fixture.Tenant), pack => pack.PackKey == ReleasedPackAtomicFixture.ReleaseKey);
        Assert.Null(reopened.GetWatermark(_fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey));
        Assert.Empty(((IPackProjectionAdmissionStore)reopened).ListIncompleteProjectionAdmissions());
        Assert.Empty(_fixture.Projector.Published);
        Assert.Equal(outbox + 2, await _fixture.OutboxCountAsync());
        Assert.Equal(2, _fixture.Audit.Successes().Count(entry => entry.Action == PackInstallAuditAction.Narrowed
            && entry.PackKey == ReleasedPackAtomicFixture.Finance));
        // Nothing failed after the commit, so the body has no diagnostics member and nothing is logged as a warning.
        Assert.False(body.TryGetProperty("diagnostics", out _), body.ToString());
        Assert.DoesNotContain(_log.Entries, entry => entry.Level == LogLevel.Warning);
    }

    /// <summary>
    /// Regression foreign-only-success, unchanged half: edits equal to the owner's Active definitions take nothing
    /// over and narrow nothing, so the release is a 200 "narrowed" no-op that runs no kernel write at all.
    /// </summary>
    [Fact]
    public async Task Unchanged_foreign_only_release_through_the_route_is_a_no_op()
    {
        var digest = await _fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", """{"id":"forms/invoice","pack":"acme.finance"}"""),
             ("records/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", """{"id":"records/invoice","pack":"acme.finance"}""")],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));
        var live = _fixture.LiveState();
        var durable = await _fixture.DurableStateAsync();

        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), new { });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
        Assert.Equal("narrowed", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("narrowed").EnumerateArray());
        Assert.Equal((0, 0, 0, 0), (_fixture.Exporter.Exports, _fixture.Installer.Installs,
            _fixture.Installer.Activations, _fixture.Installer.Narrowings));
        Assert.Equal(live, _fixture.LiveState());
        Assert.Equal(durable, await _fixture.DurableStateAsync());
        Assert.Empty(_fixture.Audit.Successes());
    }

    /// <summary>
    /// Regression mixed-refusal through the route: the own upgrade and the first overlay stage, the later overlay is
    /// refused by the composed cascade admission, the route answers 422, and a restart reads the pre-call rows.
    /// </summary>
    [Fact]
    public async Task Mixed_release_refused_through_the_route_writes_nothing()
    {
        _fixture.SeedOwnPrior();
        var digest = await _fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.OwnNote, ReleasedPackAtomicFixture.WithoutContactTitle, ReleasedPackAtomicFixture.WithoutPackageCeiling],
            (ReleasedPackAtomicFixture.Defaults, "1.0.0")));
        var live = _fixture.LiveState();
        var durable = await _fixture.DurableStateAsync();
        var outbox = await _fixture.OutboxCountAsync();

        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), new { });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, body.ToString());
        Assert.Equal("refused", body.GetProperty("status").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("configuration-release-foreign-definition", refusal.GetProperty("code").GetString());
        Assert.Equal(ReleasedPackAtomicFixture.PackageDefaults, refusal.GetProperty("target").GetString());
        Assert.EndsWith("its narrowing overlay was refused (pack.defaults.relax_forbidden).",
            refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(body.GetProperty("narrowed").EnumerateArray());
        Assert.Equal((1, 1, 2), (_fixture.Installer.Installs, _fixture.Installer.Activations, _fixture.Installer.Narrowings));
        Assert.Equal(live, _fixture.LiveState());
        Assert.Equal(durable, await _fixture.DurableStateAsync());
        Assert.Equal(outbox, await _fixture.OutboxCountAsync());
        Assert.Empty(_fixture.Audit.Successes());
    }

    /// <summary>
    /// Review follow-up to PR 397: a failing audit delivery and a failing projection observer after the release
    /// commits leave the route's 200 "installed" unchanged. The body names the failure by a stable code and carries no
    /// exception text (tickets 092/094); the warning log carries the failure and its exception.
    /// </summary>
    [Fact]
    public async Task Postcommit_reaction_failure_through_the_route_is_surfaced_on_the_success()
    {
        _fixture.SeedOwnPrior();
        _fixture.Audit.FailAppend = entry => entry.Action == PackInstallAuditAction.Activated
            ? new InvalidOperationException("injected activation audit failure")
            : null;
        _fixture.Projector.ObserverFailure = new InvalidOperationException("injected projection observer failure");
        var digest = await _fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.OwnNote, ReleasedPackAtomicFixture.WithoutContactTitle, ReleasedPackAtomicFixture.WithoutNoteDefault],
            (ReleasedPackAtomicFixture.Defaults, "1.0.0")));

        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), new { });

        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        var body = document.RootElement;
        Assert.True(response.StatusCode == HttpStatusCode.OK, raw);
        Assert.Equal("installed", body.GetProperty("status").GetString());
        Assert.Equal((ReleasedPackAtomicFixture.ReleaseKey, "1.1.0"),
            (body.GetProperty("packKey").GetString(), body.GetProperty("version").GetString()));
        Assert.Empty(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("post-commit-reaction-failed", body.GetProperty("diagnostics").GetString());
        AssertNoExceptionText(raw, "injected activation audit failure", "injected projection observer failure");
        var warning = Assert.Single(_log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(digest, warning.Message, StringComparison.Ordinal);
        Assert.Contains("post-commit-reaction-failed", warning.Message, StringComparison.Ordinal);
        Assert.Contains("injected activation audit failure", warning.Message, StringComparison.Ordinal);
        Assert.Contains("injected projection observer failure", warning.Message, StringComparison.Ordinal);
        var logged = Assert.IsType<AggregateException>(warning.Exception);
        Assert.Contains(logged.InnerExceptions, inner => inner.Message == "injected projection observer failure");
        // The commit stands: a restart reads the upgraded release as the Active version.
        await using var restart = SearchTestStore.Reopen(_fixture.Harness.Store);
        Assert.Equal("1.1.0", new DurablePackInstallStore(restart.PacksFactory)
            .GetActive(_fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey)!.Version);
    }

    /// <summary>
    /// Review follow-up to PR 397, refusal half: the mixed release is refused and its deferred refusal record then
    /// fails. The route still answers 422 "refused" with the refusal, names the failure by a stable code with no
    /// exception text, and logs the failure and its exception at warning level.
    /// </summary>
    [Fact]
    public async Task Deferred_refusal_record_failure_through_the_route_is_surfaced_on_the_refusal()
    {
        _fixture.SeedOwnPrior();
        _fixture.Audit.FailAppend = entry => entry.Action == PackInstallAuditAction.Refused
            ? new InvalidOperationException("injected refusal record failure")
            : null;
        var digest = await _fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.OwnNote, ReleasedPackAtomicFixture.WithoutContactTitle, ReleasedPackAtomicFixture.WithoutPackageCeiling],
            (ReleasedPackAtomicFixture.Defaults, "1.0.0")));
        var durable = await _fixture.DurableStateAsync();

        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), new { });

        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        var body = document.RootElement;
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, raw);
        Assert.Equal("refused", body.GetProperty("status").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("configuration-release-foreign-definition", refusal.GetProperty("code").GetString());
        Assert.Equal("deferred-refusal-record-failed", body.GetProperty("diagnostics").GetString());
        AssertNoExceptionText(raw, "injected refusal record failure");
        var warning = Assert.Single(_log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(digest, warning.Message, StringComparison.Ordinal);
        Assert.Contains("deferred-refusal-record-failed", warning.Message, StringComparison.Ordinal);
        Assert.Contains("injected refusal record failure", warning.Message, StringComparison.Ordinal);
        var logged = Assert.IsType<AggregateException>(warning.Exception);
        Assert.Contains(logged.InnerExceptions, inner => inner.Message == "injected refusal record failure");
        Assert.Equal(durable, await _fixture.DurableStateAsync());
    }

    /// <summary>Neither the injected failure messages nor the transaction's aggregate wording reach the wire.</summary>
    private static void AssertNoExceptionText(string raw, params string[] injected)
    {
        foreach (var message in injected) Assert.DoesNotContain(message, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Projection committed", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Projection discarded", raw, StringComparison.Ordinal);
    }

    private static string InstallRoute(string digest) => $"/api/local-node/configuration/releases/{digest}/install";

    private sealed class FrozenClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
