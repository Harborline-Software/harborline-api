using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Audit;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Packs;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

using IPackProjectionParticipant = Harborline.Api.Foundation.Definitions.IPackProjectionParticipant;
using PackProjectionTransaction = Harborline.Api.Foundation.Definitions.PackProjectionTransaction;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// A Released package installs as ONE write. The release's own install, its activation and projection, and every
/// narrowing overlay it carries for another package are staged into one projection transaction and commit once;
/// a refusal, fault or cancellation anywhere before that commit leaves no release-produced business state, in
/// memory or on a restart, and publishes no success reaction. These run the real <see cref="ReleasedPackInstaller"/>
/// over the real <see cref="PackInstaller"/>, the real <see cref="DurablePackInstallStore"/> and the real kernel
/// audit outbox on one encrypted database. Only the seams that inject a refusal, a fault, a cancellation or a
/// suspension are test doubles, and each delegates every business write to the production engine.
/// </summary>
public sealed class ReleasedPackAtomicInstallTests
{
    private const string FinanceForm = """{"id":"forms/invoice"}""";
    private const string FinanceRecord = """{"id":"records/invoice"}""";

    /// <summary>The production reader/writer split joins through the installer without widening the reader.</summary>
    [Fact]
    public async Task Production_reader_facade_composition_commits_foreign_overlays_and_rolls_back_a_later_refusal()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        Assert.IsType<DurablePackInstallStoreReader>(fixture.Reader);
        Assert.False(fixture.Reader is IPackInstallMutationStore);
        Assert.False(fixture.Reader is IPackProjectionParticipant);
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", FinanceForm),
             ("records/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", FinanceRecord)],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));
        var success = await fixture.InstallAsync(digest);
        Assert.True(success.Succeeded);
        Assert.False(success.Installed);
        Assert.Equal(new[] { "forms/invoice", "records/invoice" }, success.Narrowed);
        await using (var restart = SearchTestStore.Reopen(fixture.Harness.Store))
        {
            var reopened = new DurablePackInstallStore(restart.PacksFactory);
            Assert.Equal(new[] { ("forms/invoice", "{\"pack\":null}"), ("records/invoice", "{\"pack\":null}") },
                reopened.GetOverrides(fixture.Tenant, ReleasedPackAtomicFixture.Finance)
                    .Select(row => (row.ContentKey, row.OverlayPatch.ToJsonString())).ToArray());
        }
        var before = await fixture.DurableStateAsync();
        fixture.Admission.SiblingConflict = (ReleasedPackAtomicFixture.FieldDefaults, ReleasedPackAtomicFixture.PackageDefaults);
        var refusedDigest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.WithoutContactTitle, ReleasedPackAtomicFixture.WithoutNoteDefault],
            (ReleasedPackAtomicFixture.Defaults, "1.0.0")));
        var refusal = await fixture.InstallAsync(refusedDigest);
        Assert.False(refusal.Succeeded);
        Assert.False(refusal.Installed);
        Assert.Empty(refusal.Narrowed ?? []);
        Assert.Equal(before, await fixture.DurableStateAsync());
    }

    /// <summary>
    /// Regression preflight-refusal: every refusal the release can decide from its bound snapshot is decided before
    /// anything is exported, installed, activated or narrowed, so no kernel write runs and nothing changes.
    /// </summary>
    [Theory]
    [InlineData("missing-dependency", "pack.install.refused.unmet_dependency", "acme.missing", "no such version of it is Active")]
    [InlineData("malformed-pin", "pack.install.refused.malformed_dependency_pin", ReleasedPackAtomicFixture.Finance, "no such version of it is Active")]
    [InlineData("unauthorized-owner", "configuration-release-foreign-definition", "forms/invoice", "the caller may not narrow that package")]
    [InlineData("kind-mismatch", "configuration-release-foreign-definition", "forms/invoice", "the edit states AssetTypeDefinition over a FormDefinition definition")]
    [InlineData("widening", "configuration-release-foreign-definition", "forms/invoice", "the edit widens it at '/extra'")]
    [InlineData("no-owner-definition", "configuration-release-foreign-definition", "forms/unknown", "that package has no Active definition under this key to narrow")]
    public async Task Preflight_refusal_changes_no_business_state_and_runs_no_kernel_write(
        string variant, string code, string target, string because)
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync(
            releaseGate: variant == "unauthorized-owner" ? TestPackGate.ScopedTo(ReleasedPackAtomicFixture.ReleaseKey) : null);
        var form = variant switch
        {
            "kind-mismatch" => ("forms/invoice", ReleasedPackAtomicFixture.Finance, "AssetTypeDefinition", FinanceForm),
            "widening" => ("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition",
                """{"id":"forms/invoice","pack":"acme.finance","extra":true}"""),
            "no-owner-definition" => ("forms/unknown", ReleasedPackAtomicFixture.Finance, "FormDefinition", FinanceForm),
            _ => ("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", FinanceForm),
        };
        var pins = variant switch
        {
            "missing-dependency" => new[] { (ReleasedPackAtomicFixture.Finance, "1.0.0"), ("acme.missing", "1.0.0") },
            "malformed-pin" => new[] { (ReleasedPackAtomicFixture.Finance, "one.zero") },
            _ => new[] { (ReleasedPackAtomicFixture.Finance, "1.0.0") },
        };
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0", [ReleasedPackAtomicFixture.OwnNote, form], pins));
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();

        var outcome = await fixture.InstallAsync(digest);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Installed);
        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal((code, target), (refusal.Code, refusal.Target));
        Assert.Contains(because, refusal.Message, StringComparison.Ordinal);
        Assert.Equal((0, 0, 0, 0), (fixture.Exporter.Exports, fixture.Installer.Installs, fixture.Installer.Activations, fixture.Installer.Narrowings));
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
        // Authorization may audit its own decision; the release publishes no install, activation or narrowing.
        Assert.Empty(fixture.Audit.Successes());
    }

    /// <summary>
    /// Regression later-composed-admission-refusal: two narrowings of the SAME owner's CascadeDefaults siblings pass
    /// the outer preflight; the second is refused by the composed admission run over the whole staged batch. Neither
    /// planned overlay persists, the owner's earlier override is unchanged, and the admission that refused saw the
    /// first sibling's staged overlay rather than the second patch alone.
    /// </summary>
    [Theory]
    [InlineData("restriction", "pack.defaults.relax_forbidden")]
    [InlineData("sibling", ComposedCascadeAdmission.SiblingConflictCode)]
    public async Task Later_composed_admission_refusal_persists_no_planned_overlay(string kind, string code)
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        if (kind == "sibling")
            fixture.Admission.SiblingConflict = (ReleasedPackAtomicFixture.FieldDefaults, ReleasedPackAtomicFixture.PackageDefaults);
        var second = kind == "restriction" ? ReleasedPackAtomicFixture.WithoutPackageCeiling : ReleasedPackAtomicFixture.WithoutNoteDefault;
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.WithoutContactTitle, second], (ReleasedPackAtomicFixture.Defaults, "1.0.0")));
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();

        var outcome = await fixture.InstallAsync(digest);

        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal(("configuration-release-foreign-definition", ReleasedPackAtomicFixture.PackageDefaults), (refusal.Code, refusal.Target));
        Assert.EndsWith($"its narrowing overlay was refused ({code}).", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(outcome.Narrowed ?? []);
        Assert.Equal(2, fixture.Installer.Narrowings);
        var kept = Assert.Single(fixture.Store.GetOverrides(fixture.Tenant, ReleasedPackAtomicFixture.Defaults));
        Assert.Equal((ReleasedPackAtomicFixture.ExtraDefaults, ReleasedPackAtomicFixture.ExtraOverride), (kept.ContentKey, kept.OverlayPatch.ToJsonString()));
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());

        // The refused admission composed the second sibling over the first one's staged overlay and the earlier override.
        var batch = Assert.Single(fixture.Admission.Batches, batch => ComposedCascadeAdmission.Changed(batch, ReleasedPackAtomicFixture.PackageDefaults));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ReleasedPackAtomicFixture.WithoutContactTitle.Body),
            JsonNode.Parse(Assert.Single(batch, item => item.Key == ReleasedPackAtomicFixture.FieldDefaults).CanonicalJson)));
        Assert.True(ComposedCascadeAdmission.Changed(batch, ReleasedPackAtomicFixture.ExtraDefaults));
        Assert.DoesNotContain(fixture.Audit.Successes(), entry => entry.Action == PackInstallAuditAction.Narrowed);
        Assert.Contains(fixture.Audit.Published, entry => entry.Action == PackInstallAuditAction.Refused
            && entry.Detail == $"{code}:{ReleasedPackAtomicFixture.PackageDefaults}");
    }

    /// <summary>The control for the sibling refusal: the second sibling alone is admitted and commits.</summary>
    [Fact]
    public async Task The_second_sibling_alone_is_admitted_so_the_refusal_is_the_composition()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        fixture.Admission.SiblingConflict = (ReleasedPackAtomicFixture.FieldDefaults, ReleasedPackAtomicFixture.PackageDefaults);
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.WithoutNoteDefault], (ReleasedPackAtomicFixture.Defaults, "1.0.0")));

        var outcome = await fixture.InstallAsync(digest);

        Assert.True(outcome.Succeeded, string.Join(";", outcome.Refusals.Select(refusal => refusal.Message)));
        Assert.Equal([ReleasedPackAtomicFixture.PackageDefaults], outcome.Narrowed);
        await using var restart = SearchTestStore.Reopen(fixture.Harness.Store);
        var reopened = new DurablePackInstallStore(restart.PacksFactory);
        Assert.Equal(
            new[]
            {
                (ReleasedPackAtomicFixture.ExtraDefaults, ReleasedPackAtomicFixture.ExtraOverride),
                (ReleasedPackAtomicFixture.PackageDefaults, """{"defaults":[{"masking":{"revealLast":0}}]}"""),
            },
            reopened.GetOverrides(fixture.Tenant, ReleasedPackAtomicFixture.Defaults)
                .Select(row => (row.ContentKey, row.OverlayPatch.ToJsonString())).ToArray());
    }

    /// <summary>
    /// Regression mixed-refusal: the release's own upgrade installs and activates, its projection runs, its first
    /// overlay stages, and then its later overlay refuses. No release-created seed, version, watermark, projection
    /// admission, projected state, overlay or audit outbox row persists; the earlier own version and every other
    /// package's state are retained.
    /// </summary>
    [Fact]
    public async Task Mixed_release_refusal_leaves_no_release_write_and_keeps_preexisting_state()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        fixture.SeedOwnPrior();
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.OwnNote, ReleasedPackAtomicFixture.WithoutContactTitle, ReleasedPackAtomicFixture.WithoutPackageCeiling],
            (ReleasedPackAtomicFixture.Defaults, "1.0.0")));
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();
        var outbox = await fixture.OutboxCountAsync();

        var outcome = await fixture.InstallAsync(digest);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Installed);
        Assert.Equal(("configuration-release-foreign-definition", ReleasedPackAtomicFixture.PackageDefaults),
            (Assert.Single(outcome.Refusals).Code, outcome.Refusals[0].Target));
        Assert.Equal((1, 1, 2), (fixture.Installer.Installs, fixture.Installer.Activations, fixture.Installer.Narrowings));
        Assert.Equal("1.0.0", fixture.Store.GetActive(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey)!.Version);
        Assert.Null(fixture.Store.GetVersion(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey, "1.1.0"));
        Assert.Empty(fixture.Projector.Published);
        Assert.Equal(0, fixture.Projector.Notified);
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
        Assert.Equal(outbox, await fixture.OutboxCountAsync());
        Assert.Empty(fixture.Audit.Successes());
        // The refusal record is distinct from the discarded write and is still kept.
        Assert.Contains(fixture.Audit.Published, entry => entry.Action == PackInstallAuditAction.Refused
            && entry.Detail == $"pack.defaults.relax_forbidden:{ReleasedPackAtomicFixture.PackageDefaults}");
    }

    /// <summary>Regression mixed-refusal, own half: an own activation refusal leaves no staged install write.</summary>
    [Theory]
    [InlineData("refused", "pack.install.activate.projection_refused")]
    [InlineData("faulted", "pack.install.activate.projection_failed")]
    public async Task Own_activation_refusal_leaves_no_staged_install(string projection, string error)
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        fixture.SeedOwnPrior();
        fixture.Projector.OnProject = (_, _) => projection == "refused"
            ? new RefusedProjection()
            : throw new InvalidOperationException("injected projection fault");
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.OwnNote, ReleasedPackAtomicFixture.WithoutContactTitle], (ReleasedPackAtomicFixture.Defaults, "1.0.0")));
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();

        var outcome = await fixture.InstallAsync(digest);

        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal(("configuration-release-activation-refused", "releasedPackage", error), (refusal.Code, refusal.Target, refusal.Message));
        Assert.False(outcome.Installed);
        Assert.Equal((1, 1, 0), (fixture.Installer.Installs, fixture.Installer.Activations, fixture.Installer.Narrowings));
        Assert.Null(fixture.Store.GetVersion(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey, "1.1.0"));
        Assert.Equal("1.0.0", fixture.Store.GetWatermark(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey)!.Version);
        Assert.Empty(fixture.Projector.Published);
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
        Assert.Empty(fixture.Audit.Successes());
        Assert.Contains(fixture.Audit.Published, entry => entry.Action == PackInstallAuditAction.Refused && entry.Detail == error);
    }

    /// <summary>
    /// Regression later-readset-drift: the release binds its premises, then suspends while signing its own pack. A
    /// legitimate write commits meanwhile through the ordinary engine. The release refuses with the installed-state
    /// control and writes nothing, and the concurrent write is exactly what persists, not the pre-call state.
    /// </summary>
    [Theory]
    [InlineData("sibling override")]
    [InlineData("same-key override")]
    [InlineData("owner upgraded")]
    [InlineData("none")]
    public async Task Drift_after_admission_refuses_and_keeps_the_concurrent_write(string change)
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.OwnNote, ("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", FinanceForm)],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));
        var suspension = fixture.Exporter.Suspend();

        var operation = fixture.InstallAsync(digest).AsTask();
        await suspension.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            Assert.False(operation.IsCompleted);
            if (change == "sibling override")
                Assert.True((await fixture.NarrowConcurrentlyAsync("records/invoice", """{"pack":null}""")).Recorded);
            else if (change == "same-key override")
                Assert.True((await fixture.NarrowConcurrentlyAsync("forms/invoice", """{"id":null}""")).Recorded);
            else if (change == "owner upgraded")
                fixture.Seed(ReleasedPackAtomicFixture.Finance, "1.1.0", ReleasedPackAtomicFixture.FinanceItems);
            var concurrent = fixture.LiveState();
            suspension.Release.TrySetResult();
            var outcome = await operation.WaitAsync(TimeSpan.FromSeconds(30));

            if (change == "none")
            {
                Assert.True(outcome.Succeeded, string.Join(";", outcome.Refusals.Select(refusal => refusal.Message)));
                Assert.Equal(PackLifecycleState.Active,
                    fixture.Store.GetActive(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey)!.Lifecycle);
                var overlay = Assert.Single(fixture.Store.GetOverrides(fixture.Tenant, ReleasedPackAtomicFixture.Finance));
                Assert.Equal(("forms/invoice", """{"pack":null}"""), (overlay.ContentKey, overlay.OverlayPatch.ToJsonString()));
                return;
            }
            var refusal = Assert.Single(outcome.Refusals);
            Assert.Equal(("pack.install.refused.installed_state_changed", ReleasedPackAtomicFixture.Finance), (refusal.Code, refusal.Target));
            Assert.Equal((0, 0, 0), (fixture.Installer.Installs, fixture.Installer.Activations, fixture.Installer.Narrowings));
            Assert.Equal(concurrent, fixture.LiveState());
            Assert.Null(fixture.Store.GetActive(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey));

            await using var restart = SearchTestStore.Reopen(fixture.Harness.Store);
            var reopened = new DurablePackInstallStore(restart.PacksFactory);
            Assert.DoesNotContain(reopened.ListInstalled(fixture.Tenant), pack => pack.PackKey == ReleasedPackAtomicFixture.ReleaseKey);
            var overrides = reopened.GetOverrides(fixture.Tenant, ReleasedPackAtomicFixture.Finance)
                .Select(row => (row.ContentKey, row.OverlayPatch.ToJsonString())).ToArray();
            Assert.Equal(change switch
            {
                "sibling override" => new[] { ("records/invoice", """{"pack":null}""") },
                "same-key override" => new[] { ("forms/invoice", """{"id":null}""") },
                _ => Array.Empty<(string, string)>(),
            }, overrides);
            Assert.Equal(change == "owner upgraded" ? "1.1.0" : "1.0.0",
                reopened.GetActive(fixture.Tenant, ReleasedPackAtomicFixture.Finance)!.Version);
            // The only success the audit carries is the independent narrowing, never the refused release's.
            Assert.All(fixture.Audit.Successes(), entry => Assert.Equal(ReleasedPackAtomicFixture.Finance, entry.PackKey));
            Assert.DoesNotContain(fixture.Audit.Successes(), entry => entry.Detail == "pack.install.narrowed:forms/invoice"
                && change != "same-key override");
        }
        finally { suspension.Release.TrySetResult(); }
    }

    /// <summary>
    /// Regression precommit-fault-cancellation: a fault or cancellation after staging has begun, at a later overlay,
    /// at the activation's projection or after everything is staged, rolls the whole release back in memory and on a
    /// restart, publishes no success reaction, and leaves no lease or unit behind: the same release then commits.
    /// </summary>
    [Theory]
    [InlineData("overlay-fault")]
    [InlineData("overlay-cancel")]
    [InlineData("projection-cancel")]
    [InlineData("after-staging-cancel")]
    public async Task Precommit_fault_or_cancellation_rolls_back_and_publishes_nothing(string seam)
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        fixture.SeedOwnPrior();
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.OwnNote, ReleasedPackAtomicFixture.WithoutContactTitle, ReleasedPackAtomicFixture.WithoutNoteDefault],
            (ReleasedPackAtomicFixture.Defaults, "1.0.0")));
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();
        var outbox = await fixture.OutboxCountAsync();
        using var cancellation = new CancellationTokenSource();
        var later = $"pack.install.narrowed:{ReleasedPackAtomicFixture.PackageDefaults}";
        switch (seam)
        {
            case "overlay-fault":
                fixture.Audit.BeforePrepare = (entry, _) => entry.Detail == later
                    ? throw new InvalidOperationException("injected precommit fault")
                    : ValueTask.CompletedTask;
                break;
            case "overlay-cancel":
                fixture.Audit.BeforePrepare = (entry, _) =>
                {
                    if (entry.Detail == later) cancellation.Cancel();
                    return ValueTask.CompletedTask;
                };
                break;
            case "projection-cancel":
                fixture.Projector.OnProject = (_, ct) =>
                {
                    cancellation.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return null;
                };
                break;
            case "after-staging-cancel":
                // Install, activation and both overlays have each reached React: everything is staged.
                fixture.Stages.OnReact = count => { if (count == 4) cancellation.Cancel(); };
                break;
        }

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => fixture.InstallAsync(digest, cancellation.Token).AsTask());

        if (seam == "overlay-fault") Assert.Equal("injected precommit fault", Assert.IsType<InvalidOperationException>(thrown).Message);
        else Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Equal(seam is "projection-cancel" ? (1, 1, 0) : (1, 1, 2),
            (fixture.Installer.Installs, fixture.Installer.Activations, fixture.Installer.Narrowings));
        Assert.Empty(fixture.Projector.Published);
        Assert.Equal(0, fixture.Projector.Notified);
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
        Assert.Equal(outbox, await fixture.OutboxCountAsync());
        Assert.Empty(fixture.Audit.Successes());

        // Nothing was left holding the write lease or the SQLite unit: the same release now commits as one write.
        fixture.Audit.BeforePrepare = null;
        fixture.Projector.OnProject = null;
        fixture.Stages.OnReact = null;
        var retried = await fixture.InstallAsync(digest).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(retried.Succeeded, string.Join(";", retried.Refusals.Select(refusal => refusal.Message)));
        Assert.Equal("1.1.0", fixture.Store.GetActive(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey)!.Version);
        Assert.Equal([$"{ReleasedPackAtomicFixture.ReleaseKey}@1.1.0"], fixture.Projector.Published);
    }

    /// <summary>
    /// Exercises the real final SQLite COMMIT, not a throw before Commit. SQLite's native commit hook
    /// converts a nonzero return into a confirmed rollback (sqlite.org/c3ref/commit_hook.html), reported as the
    /// extended code SQLITE_CONSTRAINT_COMMITHOOK (sqlite.org/rescode.html); that code and the reopened business
    /// state distinguish this from an ambiguous I/O/acknowledgement failure. The callback only counts/vetoes; it
    /// never runs SQL or reenters the connection. No rollback hook is installed: SQLite keeps one per connection
    /// and the open SqliteTransaction owns it to learn of exactly this automatic rollback, so replacing it would
    /// make the library ROLLBACK a second time. The zero-return control proves the same native path commits
    /// exactly once for foreign-only and mixed releases.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Final_sqlite_commit_veto_rolls_back_all_staged_writes_and_zero_return_commits(bool own, bool veto)
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        if (own) fixture.SeedOwnPrior();
        var edits = own
            ? new[] { ReleasedPackAtomicFixture.OwnNote, ReleasedPackAtomicFixture.WithoutContactTitle, ReleasedPackAtomicFixture.WithoutNoteDefault }
            : new[] { ReleasedPackAtomicFixture.WithoutContactTitle, ReleasedPackAtomicFixture.WithoutNoteDefault };
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0", edits,
            (ReleasedPackAtomicFixture.Defaults, "1.0.0")));
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();
        var outbox = await fixture.OutboxCountAsync();
        var commits = 0;
        fixture.Installer.OnJoinedConnection = connection =>
        {
            SQLitePCL.raw.sqlite3_commit_hook(connection.Handle!, _ =>
            {
                Interlocked.Increment(ref commits);
                return veto ? 1 : 0;
            }, null!);
        };

        if (veto)
        {
            var failure = await Assert.ThrowsAsync<SqliteException>(() => fixture.InstallAsync(digest).AsTask());
            Assert.Equal(19, failure.SqliteErrorCode); // SQLITE_CONSTRAINT, a known native veto, not unknown commit outcome.
            Assert.Equal(531, failure.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_COMMITHOOK: SQLite rolled the COMMIT back.
            Assert.Equal(1, commits);
            Assert.Equal(own ? (1, 1, 2) : (0, 0, 2),
                (fixture.Installer.Installs, fixture.Installer.Activations, fixture.Installer.Narrowings));
            Assert.Equal(live, fixture.LiveState());
            Assert.Equal(durable, await fixture.DurableStateAsync());
            Assert.Equal(outbox, await fixture.OutboxCountAsync());
            Assert.Empty(fixture.Audit.Successes());
            Assert.Empty(fixture.Projector.Published);
            Assert.Equal(0, fixture.Projector.Notified);
            // The rejected unit released its resources; an unhooked fresh transaction can commit the release.
            fixture.Installer.OnJoinedConnection = null;
            Assert.True((await fixture.InstallAsync(digest).AsTask().WaitAsync(TimeSpan.FromSeconds(30))).Succeeded);
        }
        else
        {
            var outcome = await fixture.InstallAsync(digest);
            Assert.True(outcome.Succeeded);
            Assert.Equal(own, outcome.Installed);
            Assert.Equal(1, commits);
            Assert.Equal([ReleasedPackAtomicFixture.FieldDefaults, ReleasedPackAtomicFixture.PackageDefaults], outcome.Narrowed);
            Assert.Equal(outbox + (own ? 4 : 2), await fixture.OutboxCountAsync());
            await using var restart = SearchTestStore.Reopen(fixture.Harness.Store);
            var reopened = new DurablePackInstallStore(restart.PacksFactory);
            Assert.Equal("""{"defaults":[{"masking":{"revealLast":0}}]}""",
                reopened.GetOverrides(fixture.Tenant, ReleasedPackAtomicFixture.Defaults)
                    .Single(row => row.ContentKey == ReleasedPackAtomicFixture.PackageDefaults).OverlayPatch.ToJsonString());
            Assert.Equal(own ? "1.1.0" : null,
                reopened.GetActive(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey)?.Version);
        }
    }

    /// <summary>
    /// Regression precommit-fault-cancellation, post-commit half: once the release has committed, a failing audit
    /// delivery or projection observer is a diagnostic on a success, never a refusal or a rollback.
    /// </summary>
    [Fact]
    public async Task Postcommit_reaction_failure_is_a_success_diagnostic()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        fixture.SeedOwnPrior();
        fixture.Audit.FailAppend = entry => entry.Action == PackInstallAuditAction.Activated
            ? new InvalidOperationException("injected activation audit failure")
            : null;
        fixture.Projector.ObserverFailure = new InvalidOperationException("injected projection observer failure");
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.OwnNote, ReleasedPackAtomicFixture.WithoutContactTitle, ReleasedPackAtomicFixture.WithoutNoteDefault],
            (ReleasedPackAtomicFixture.Defaults, "1.0.0")));
        var outbox = await fixture.OutboxCountAsync();

        var outcome = await fixture.InstallAsync(digest);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Installed);
        Assert.Equal((ReleasedPackAtomicFixture.ReleaseKey, "1.1.0"), (outcome.PackKey, outcome.Version));
        Assert.Equal([ReleasedPackAtomicFixture.FieldDefaults, ReleasedPackAtomicFixture.PackageDefaults], outcome.Narrowed);
        Assert.NotNull(outcome.Diagnostics);
        Assert.StartsWith("Projection committed; post-commit notification or cleanup failed.", outcome.Diagnostics, StringComparison.Ordinal);
        Assert.Contains("injected activation audit failure", outcome.Diagnostics, StringComparison.Ordinal);
        Assert.Contains("injected projection observer failure", outcome.Diagnostics, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Projector.Notified);
        Assert.Equal([$"{ReleasedPackAtomicFixture.ReleaseKey}@1.1.0"], fixture.Projector.Published);
        // Install, activation and both overlays staged their audit rows with the one commit.
        Assert.Equal(outbox + 4, await fixture.OutboxCountAsync());
        Assert.Contains(fixture.Audit.Successes(), entry => entry.Action == PackInstallAuditAction.Upgraded && entry.Version == "1.1.0");
        Assert.Equal(2, fixture.Audit.Successes().Count(entry => entry.Action == PackInstallAuditAction.Narrowed));

        await using var restart = SearchTestStore.Reopen(fixture.Harness.Store);
        var reopened = new DurablePackInstallStore(restart.PacksFactory);
        Assert.Equal("1.1.0", reopened.GetActive(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey)!.Version);
        var reopenedDefinitions = new NodeEfAuthorizationConfigurationStore(restart.Factory, fixture.Roles);
        var binding = Assert.Single(await reopenedDefinitions.ListAsync(fixture.Tenant),
            row => row.Definition.PublisherPackageId == ReleasedPackAtomicFixture.ReleaseKey);
        Assert.Equal("records:read", binding.Definition.Operation.ToString());
        Assert.Equal(["sys.platform-roles/administrator"], binding.EffectiveRoles.Roles.Select(role => role.ToString()).ToArray());
        Assert.Empty(((IPackProjectionAdmissionStore)reopened).ListIncompleteProjectionAdmissions());
        Assert.Equal(
            [ReleasedPackAtomicFixture.ExtraDefaults, ReleasedPackAtomicFixture.FieldDefaults, ReleasedPackAtomicFixture.PackageDefaults],
            reopened.GetOverrides(fixture.Tenant, ReleasedPackAtomicFixture.Defaults).Select(row => row.ContentKey).ToArray());
    }
}

/// <summary>
/// One tenant on one encrypted database, wired as the host wires a Released-package install: the real exporter,
/// install engine, durable store and kernel audit outbox. The test seams are the admission (the real cascade
/// admission, optionally with a sibling rule), a projector participant, a stage observer, a suspendable signer
/// and recorders over the installer and the audit, each delegating its business write to the real engine.
/// </summary>
internal sealed class ReleasedPackAtomicFixture : IAsyncDisposable
{
    internal const string ReleaseKey = "tenant.release";
    internal const string Finance = "acme.finance";
    internal const string Defaults = "acme.defaults";
    internal const string PackageDefaults = "defaults/package";
    internal const string FieldDefaults = "defaults/field";
    internal const string ExtraDefaults = "defaults/extra";
    internal const string Principal = "test-operator";
    internal const string ExtraOverride = """{"defaults":[{"recordType":"task","field":"owner","masking":{"revealLast":1}}]}""";
    internal static readonly DateTimeOffset Frozen = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly TeamId Team = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000433"));

    internal static readonly (string Key, string Owner, string Kind, string Body) OwnNote = ("authorization/tenant-read", ReleaseKey,
        "AuthorizationCapabilityBinding", """{"operation":"records:read","scope":"/","offeredRoles":["sys.platform-roles/administrator"]}""");

    // The package ceiling masks everything; the field and record defaults beneath it are each redundant with it.
    private const string PackageSeed = """{"schemaVersion":1,"title":"Package","defaults":[{"masking":{"revealLast":0}},{"recordType":"note","masking":{"revealLast":2}}]}""";
    private const string FieldSeed = """{"schemaVersion":1,"title":"Field","defaults":[{"recordType":"contact","field":"title","masking":{"revealLast":4}},{"recordType":"invoice","masking":{"revealLast":1}}]}""";
    private const string ExtraSeed = """{"schemaVersion":1,"title":"Extra","defaults":[{"recordType":"task","masking":{"revealLast":3}},{"recordType":"task","field":"owner","masking":{"revealLast":1}}]}""";

    /// <summary>Drops the contact title default: admitted, the package ceiling still masks it more strictly.</summary>
    internal static readonly (string Key, string Owner, string Kind, string Body) WithoutContactTitle = (FieldDefaults, Defaults,
        "CascadeDefaults", """{"schemaVersion":1,"title":"Field","defaults":[{"recordType":"invoice","masking":{"revealLast":1}}]}""");

    /// <summary>Drops the package ceiling: a proper-subset narrowing that the composed restriction check refuses.</summary>
    internal static readonly (string Key, string Owner, string Kind, string Body) WithoutPackageCeiling = (PackageDefaults, Defaults,
        "CascadeDefaults", """{"schemaVersion":1,"title":"Package","defaults":[{"recordType":"note","masking":{"revealLast":2}}]}""");

    /// <summary>Drops the note default: admitted on its own, the package ceiling masks notes more strictly.</summary>
    internal static readonly (string Key, string Owner, string Kind, string Body) WithoutNoteDefault = (PackageDefaults, Defaults,
        "CascadeDefaults", """{"schemaVersion":1,"title":"Package","defaults":[{"masking":{"revealLast":0}}]}""");

    internal static readonly (string Key, PackContentKind Kind, string Json)[] FinanceItems =
    [
        ("forms/invoice", PackContentKind.FormDefinition, """{"id":"forms/invoice","pack":"acme.finance"}"""),
        ("records/invoice", PackContentKind.FormDefinition, """{"id":"records/invoice","pack":"acme.finance"}"""),
    ];

    private readonly PackInstallContext _concurrent;

    private ReleasedPackAtomicFixture(DurableAuditHarness harness, NodePrincipalSigner node, AuthorizationGate? releaseGate)
    {
        Harness = harness;
        Node = node;
        var kernelAudit = new KernelAuditPackInstallAudit(harness.Trail, node.Signer,
            NullLogger<KernelAuditPackInstallAudit>.Instance, harness.Outbox);
        Store = new DurablePackInstallStore(harness.Store.PacksFactory, kernelAudit);
        var gate = TestPackGate.AllowAll();
        var activation = new ConfigurationActivationTarget(harness.Store.PacksFactory, Store, gate, evidence: null);
        Proposals = new ConfigurationProposalStore(harness.Store.PacksFactory, activation, node.Signer);
        var codec = new PackFileCodec();
        Exporter = new SuspendableExporter(new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, TimeProvider.System));
        Audit = new RecordingAudit(kernelAudit);
        Reader = new DurablePackInstallStoreReader(Store);
        AuthorizationConfiguration = new NodeEfAuthorizationConfigurationStore(harness.Store.Factory, Roles);
        var writer = new AuthorizationDefinitionWriter(AuthorizationConfiguration, AuthorizationConfiguration,
            new AuthorizationDefinitionAdmission(Roles), new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(), TestInMemoryAuthorizationStores.GrantStore());
        Projector = new StagedProjector(new PackSeedProjector(Reader,
            _projectionServices.GetRequiredService<IEntityTypeRegistry>(), NullLogger<PackSeedProjector>.Instance,
            time: TimeProvider.System, roleVocabulary: Roles, authorizationDefinitions: writer,
            authorizationDefinitionCatalogue: AuthorizationConfiguration, defaults: DefaultsProjection),
            authority => AuthorizationConfiguration.ListAsync(authority.Tenant).GetAwaiter().GetResult()
                .Count(binding => binding.Definition.PublisherPackageId == ReleaseKey));
        Engine = new PackInstaller(new PackVerifier(new Ed25519Verifier(), codec), Reader, Store, Store, Admission, Audit,
            TestAuthorization.AllowGate(), Projector, pipelineObserver: Stages);
        Installer = new RecordingInstaller(Engine, harness.Store.PacksFactory);
        // The composition root's trust surface: the release's own pack verifies against the node's roster root.
        var trust = HostedPackInstallApiEndpoint.BuildTrustStore(node, NullLogger.Instance);
        Releases = new ReleasedPackInstaller(Proposals, Exporter, Installer, node, new Ed25519Verifier(), trust,
            PackRevocationList.Empty, Reader, releaseGate ?? gate);
        ActiveTeam = new AtomicActiveTeam(new TeamContext(Team, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        Tenant = NodeTenant.Resolve(ActiveTeam);
        _concurrent = new PackInstallContext(Tenant, trust, PackRevocationList.Empty, AdmittedInstant.FromRecordedAct(Frozen),
            TimeSpan.FromDays(30), Principal: Principal);
    }

    internal DurableAuditHarness Harness { get; }
    internal NodePrincipalSigner Node { get; }
    internal DurablePackInstallStore Store { get; }
    internal IPackInstallStore Reader { get; }
    internal ConfigurationProposalStore Proposals { get; }
    internal SuspendableExporter Exporter { get; }
    internal RecordingAudit Audit { get; }
    internal ComposedCascadeAdmission Admission { get; } = new();
    private readonly ServiceProvider _projectionServices = new ServiceCollection().AddLogging()
        .AddInMemoryAssetTypeSystem().BuildServiceProvider();
    internal InMemoryRoleVocabulary Roles { get; } = new(AccessGrantAuthorizationSeed.RoleDefinitions);
    internal NodeEfAuthorizationConfigurationStore AuthorizationConfiguration { get; }
    internal ActiveCascadeDefaultsProjection DefaultsProjection { get; } = new();
    internal StagedProjector Projector { get; }
    internal StageObserver Stages { get; } = new();
    internal PackInstaller Engine { get; }
    internal RecordingInstaller Installer { get; }
    internal ReleasedPackInstaller Releases { get; }
    internal AtomicActiveTeam ActiveTeam { get; }
    internal TenantId Tenant { get; }

    /// <summary>Creates the fixture with the platform pack, acme.finance and acme.defaults Active.</summary>
    /// <param name="releaseGate">The gate the release decides each narrowed owner with; allow-all by default.</param>
    internal static async Task<ReleasedPackAtomicFixture> CreateAsync(AuthorizationGate? releaseGate = null)
    {
        var harness = await DurableAuditHarness.CreateAsync();
        await using (var packs = harness.Store.CreatePacksContext()) await packs.Database.MigrateAsync();
        var seed = new byte[32];
        for (var index = 0; index < seed.Length; index++) seed[index] = (byte)(index + 43);
        var fixture = new ReleasedPackAtomicFixture(harness, new NodePrincipalSigner(seed), releaseGate);
        PlatformPackTestPreload.Activate(fixture.Store, fixture.Tenant);
        fixture.Seed(Finance, "1.0.0", FinanceItems);
        fixture.Seed(Defaults, "1.0.0",
            (PackageDefaults, PackContentKind.CascadeDefaults, PackageSeed),
            (FieldDefaults, PackContentKind.CascadeDefaults, FieldSeed),
            (ExtraDefaults, PackContentKind.CascadeDefaults, ExtraSeed));
        // An earlier, independently admitted override the release must neither drop nor overwrite.
        fixture.Store.SaveOverride(fixture.Tenant, Defaults, new PackTenantOverride(ExtraDefaults, JsonNode.Parse(ExtraOverride)!));
        return fixture;
    }

    /// <summary>The release's own pack at an earlier version, Active before the release upgrades it.</summary>
    internal void SeedOwnPrior() =>
        Seed(ReleaseKey, "1.0.0", (OwnNote.Key, PackContentKind.AuthorizationCapabilityBinding, OwnNote.Body));

    internal void Seed(string packKey, string version, params (string Key, PackContentKind Kind, string Json)[] items)
    {
        var seeds = items.Select(item => new PackSeedItem(item.Key, item.Kind, version, item.Json,
            Cid.FromBytes(Encoding.UTF8.GetBytes(packKey + version + item.Key + item.Json)))).ToArray();
        var pack = new InstalledPack(packKey, version, PackScopeTier.Horizontal, PackLifecycleState.Draft, seeds,
            new Dictionary<string, int>(), Frozen, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, Array.Empty<PackDependencyRef>());
        Store.Commit(new PackInstallTransaction(Tenant, pack, new PackInstallWatermark(packKey, version, new Dictionary<string, int>()), []));
        Store.Activate(Tenant, packKey, version);
    }

    /// <summary>A released <c>PlatformPackageManifest</c> document in the shape <see cref="ReleasedPackConversion"/> reads.</summary>
    internal static string Document(string revision, (string Key, string Owner, string Kind, string Body)[] items,
        params (string Key, string Version)[] pins) => new JsonObject
        {
            ["packageKey"] = ReleaseKey,
            ["revision"] = revision,
            ["items"] = new JsonArray(items.Select(item => (JsonNode?)new JsonObject
            {
                ["id"] = item.Key,
                ["content"] = new JsonObject
                {
                    ["classification"] = "present",
                    ["payload"] = new JsonObject
                    {
                        ["definitionKey"] = item.Key,
                        ["packageKey"] = item.Owner,
                        ["contentKind"] = item.Kind,
                        ["body"] = JsonNode.Parse(item.Body),
                    },
                },
            }).ToArray()),
            ["closure"] = new JsonObject
            {
                ["dependencies"] = new JsonArray(pins.Select(pin => (JsonNode?)new JsonObject
                {
                    ["key"] = pin.Key,
                    ["version"] = pin.Version,
                }).ToArray()),
            },
        }.ToJsonString();

    /// <summary>
    /// Offers <paramref name="document"/> as a Released package exactly as the release store keeps one: the bytes,
    /// and the node's signature over their SHA-256 and the package identity. The producer's own rules are covered
    /// by the existing release route tests; this pins the document so every oracle here is a literal.
    /// </summary>
    internal async Task<string> OfferAsync(string document)
    {
        var bytes = Encoding.UTF8.GetBytes(document);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var proposalId = "proposal-" + digest[..12];
        Proposals.Start(Tenant, proposalId, Principal, Frozen);
        var signed = await Node.Signer.SignAsync(new ReleasedPackageSubject(digest, ReleaseKey, "1.1.0",
            "saved-" + digest, "baseline-" + digest, Tenant.Value, Principal), Frozen, Guid.NewGuid());
        await using var context = Harness.Store.CreatePacksContext();
        context.ReleasedPackages.Add(new ConfigurationReleasedPackageRow
        {
            Tenant = Tenant.Value, Digest = digest, ProposalId = proposalId,
            SavedVersionDigest = "saved-" + digest, BaselineDigest = "baseline-" + digest,
            PackageKey = ReleaseKey, Revision = "1.1.0", Document = bytes,
            SignatureJson = JsonSerializer.Serialize(signed, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ReleasedBy = Principal, CheckReceiptId = "receipt-433", ReleasedAt = Frozen,
        });
        await context.SaveChangesAsync();
        return digest;
    }

    internal ValueTask<ReleasedPackInstallOutcome> InstallAsync(string digest, CancellationToken cancellationToken = default) =>
        Releases.InstallAsync(Tenant, digest, TestAuthorization.Write(Tenant, Principal, Frozen), cancellationToken);

    /// <summary>An independent, legitimate narrowing of acme.finance through the ordinary (unjoined) engine.</summary>
    internal Task<PackNarrowingOutcome> NarrowConcurrentlyAsync(string contentKey, string overlay) =>
        Engine.NarrowAsync(_concurrent, Finance, contentKey, JsonNode.Parse(overlay)!,
            TestAuthorization.AllowedDecision(Tenant, Finance, "pack", Permission.PackagesOperate, Principal, Frozen));

    /// <summary>The business state as this process reads it: the store's packs and overlays, and the projection.</summary>
    internal string LiveState() => JsonSerializer.Serialize(new
    {
        Installed = Store.ListInstalled(Tenant),
        Watermarks = new[] { ReleaseKey, Finance, Defaults }.Select(key => Store.GetWatermark(Tenant, key)),
        Overrides = new[] { ReleaseKey, Finance, Defaults }.SelectMany(key => Store.GetOverrides(Tenant, key)
            .Select(row => $"{key}/{row.ContentKey}={row.OverlayPatch.ToJsonString()}")),
        Ownership = Store.GetKeyOwnership(Tenant),
        Pending = ((IPackProjectionAdmissionStore)Store).ListIncompleteProjectionAdmissions().Count,
        Projected = Projector.Published,
        Authorization = AuthorizationConfiguration.ListAsync(Tenant).GetAwaiter().GetResult(),
        CascadeDefaults = DefaultsProjection.List(Tenant),
    });

    /// <summary>The business rows as a restart reads them, from new objects over the same encrypted file.</summary>
    internal async Task<string> DurableStateAsync()
    {
        await using var restart = SearchTestStore.Reopen(Harness.Store);
        await using var packs = restart.CreatePacksContext();
        await using var definitions = restart.CreateContext();
        var configuration = new NodeEfAuthorizationConfigurationStore(restart.Factory, Roles);
        return string.Join("\n--\n", Rows(packs.InstalledVersions), Rows(packs.Watermarks), Rows(packs.Overrides),
            Rows(packs.KeyOwnership), Rows(packs.ProjectionAdmissions),
            JsonSerializer.Serialize(await configuration.ListAsync(Tenant)),
            Rows(definitions.AuthorizationDefinitions), Rows(definitions.AuthorizationOfferedRoles),
            Rows(definitions.AuthorizationBindingRevisions), Rows(definitions.AuthorizationBindingRoles),
            Rows(definitions.AuthorizationCatalogVersions), Rows(definitions.AuthorizationTenantVersions),
            Rows(definitions.AuthorizationClosureEntries), Rows(definitions.AuthorizationClosureStates));

        static string Rows<T>(IQueryable<T> rows) where T : class => string.Join("\n",
            rows.AsNoTracking().AsEnumerable().Select(row => JsonSerializer.Serialize(row)).Order(StringComparer.Ordinal));
    }

    /// <summary>The pack audit outbox rows a restart reads; a staged success row exists only once committed.</summary>
    internal async Task<int> OutboxCountAsync()
    {
        await using var restart = SearchTestStore.Reopen(Harness.Store);
        await using var db = restart.CreateContext();
        return await db.AuditOutbox.CountAsync(row => row.EventType == "PackComposerInstall");
    }

    public async ValueTask DisposeAsync()
    {
        _projectionServices.Dispose();
        Node.Dispose();
        await Harness.DisposeAsync();
    }
}

/// <summary>Counts the install engine's mutating calls and delegates every one, joined or not, to the real engine.</summary>
internal sealed class RecordingInstaller(IPackInstaller inner, IDbContextFactory<NodeLocalPacksDbContext> factory)
    : IPackInstaller, IPackProjectionParticipant
{
    internal Action<SqliteConnection>? OnJoinedConnection { get; set; }

    public void StageProjection(PackProjectionTransaction transaction)
    {
        transaction.Enlist(inner);
        if (OnJoinedConnection is not { } configure) return;
        // Join the already-owned real durable unit. The test hook never replaces its Commit/Dispose behavior.
        var unit = transaction.Durable(() => new PackProjectionSqliteUnit(factory.CreateDbContext()));
        using var context = unit.Join(factory.CreateDbContext());
        configure((SqliteConnection)context.Database.GetDbConnection());
    }

    private int _installs;
    private int _activations;
    private int _narrowings;

    internal int Installs => _installs;
    internal int Activations => _activations;
    internal int Narrowings => _narrowings;

    public PackInstallPreview Preview(ReadOnlySpan<byte> packBytes, PackInstallContext context) => inner.Preview(packBytes, context);

    public PackInstallPreview Check(ReadOnlySpan<byte> packBytes, PackInstallContext context) => inner.Check(packBytes, context);

    public Task<PackInstallOutcome> InstallAsync(ReadOnlyMemory<byte> packBytes, PackInstallContext context,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _installs);
        return inner.InstallAsync(packBytes, context, cancellationToken);
    }

    public Task<PackInstallOutcome> InstallAsync(ReadOnlyMemory<byte> packBytes, PackInstallContext context,
        PackProjectionTransaction transaction, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _installs);
        return inner.InstallAsync(packBytes, context, transaction, cancellationToken);
    }

    public Task<PackActivationOutcome> ActivateAsync(PackInstallContext context, string packKey, string version,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activations);
        return inner.ActivateAsync(context, packKey, version, cancellationToken);
    }

    public Task<PackActivationOutcome> ActivateAsync(PackInstallContext context, string packKey, string version,
        PackProjectionTransaction transaction, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activations);
        return inner.ActivateAsync(context, packKey, version, transaction, cancellationToken);
    }

    public Task<PackDeactivationOutcome> DeactivateAsync(PackInstallContext context, string packKey, string version,
        CancellationToken cancellationToken = default) => inner.DeactivateAsync(context, packKey, version, cancellationToken);

    public Task<PackNarrowingOutcome> NarrowAsync(PackInstallContext context, string packKey, string contentKey,
        JsonNode overlayPatch, AuthorizationDecision decision, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _narrowings);
        return inner.NarrowAsync(context, packKey, contentKey, overlayPatch, decision, cancellationToken);
    }

    public Task<PackNarrowingOutcome> NarrowAsync(PackInstallContext context, string packKey, string contentKey,
        JsonNode overlayPatch, AuthorizationDecision decision, PackProjectionTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _narrowings);
        return inner.NarrowAsync(context, packKey, contentKey, overlayPatch, decision, transaction, cancellationToken);
    }
}

/// <summary>
/// Delegates to the host's kernel audit and records what was published to it. <see cref="BeforePrepare"/> injects
/// a fault or cancellation into a write's commit stage; <see cref="FailAppend"/> fails a post-commit delivery.
/// </summary>
internal sealed class RecordingAudit(IPackInstallAudit inner) : IPackInstallAudit
{
    internal ConcurrentQueue<PackInstallAuditEntry> Published { get; } = new();
    internal Func<PackInstallAuditEntry, CancellationToken, ValueTask>? BeforePrepare { get; set; }
    internal Func<PackInstallAuditEntry, Exception?>? FailAppend { get; set; }

    /// <summary>Every published entry that announces a mutation rather than a refusal.</summary>
    internal IReadOnlyList<PackInstallAuditEntry> Successes() =>
        Published.Where(entry => entry.Action != PackInstallAuditAction.Refused).ToArray();

    public void Append(PackInstallAuditEntry entry)
    {
        inner.Append(entry);
        Published.Enqueue(entry);
    }

    public void AppendAuthorized(PackInstallAuditEntry entry, AuthorizationDecision decision)
    {
        if (FailAppend?.Invoke(entry) is { } failure) throw failure;
        inner.AppendAuthorized(entry, decision);
        Published.Enqueue(entry);
    }

    public async ValueTask PrepareAuthorizedAsync(PackInstallAuditEntry entry, AuthorizationDecision decision,
        CancellationToken ct = default)
    {
        if (BeforePrepare is { } hook) await hook(entry, ct).ConfigureAwait(false);
        await inner.PrepareAuthorizedAsync(entry, decision, ct).ConfigureAwait(false);
    }

    public async ValueTask AppendAuthorizedAsync(PackInstallAuditEntry entry, AuthorizationDecision decision,
        CancellationToken ct = default)
    {
        if (FailAppend?.Invoke(entry) is { } failure) throw failure;
        await inner.AppendAuthorizedAsync(entry, decision, ct).ConfigureAwait(false);
        Published.Enqueue(entry);
    }

    public IReadOnlyList<PackInstallAuditEntry> Query(TenantId tenant) => inner.Query(tenant);
}

/// <summary>
/// The host's cascade-defaults admission (<see cref="PackWorkflowAdmissionAdapter"/>) for CascadeDefaults items and
/// the fail-closed default for everything else, recording each composed cascade batch it was asked to admit. An
/// optional sibling rule refuses a batch in which BOTH named siblings differ from their seeds, which only a batch
/// composed over the earlier staged sibling can show.
/// </summary>
internal sealed class ComposedCascadeAdmission : IPackContentAdmission, IPackCascadeDefaultsAdmission
{
    internal const string SiblingConflictCode = "test.defaults.sibling_conflict";

    private readonly WorkflowRefusingPackContentAdmission _other = new();
    private readonly PackWorkflowAdmissionAdapter _defaults =
        new(Substitute.For<IWorkflowAdmissionValidator>(), defaults: new ActiveCascadeDefaultsProjection());

    internal ConcurrentQueue<IReadOnlyList<PackComposedItem>> Batches { get; } = new();
    internal (string First, string Second)? SiblingConflict { get; set; }

    public bool ConsumesCascadeDefaults => true;

    public PackAdmissionResult Admit(IReadOnlyList<PackComposedItem> composed, TenantId tenant)
    {
        var cascade = composed.Where(item => item.Kind == PackContentKind.CascadeDefaults).ToArray();
        var refusals = _other.Admit(composed.Where(item => item.Kind != PackContentKind.CascadeDefaults).ToArray(), tenant)
            .Refusals.ToList();
        if (cascade.Length > 0)
        {
            Batches.Enqueue(cascade);
            refusals.AddRange(_defaults.Admit(cascade, tenant).Refusals);
            if (SiblingConflict is { } pair && Changed(cascade, pair.First) && Changed(cascade, pair.Second))
                refusals.Add(new PackAdmissionRefusal(pair.Second, SiblingConflictCode,
                    "Both sibling defaults may not be narrowed together."));
        }
        return refusals.Count == 0 ? PackAdmissionResult.Admissible : new PackAdmissionResult(refusals);
    }

    /// <summary>True when the batch carries <paramref name="key"/> composed differently from its seed.</summary>
    internal static bool Changed(IEnumerable<PackComposedItem> batch, string key) =>
        batch.FirstOrDefault(item => item.Key == key) is { SeedCanonicalJson: { } seed } item
        && !JsonNode.DeepEquals(JsonNode.Parse(item.CanonicalJson), JsonNode.Parse(seed));
}

/// <summary>
/// A wrapper that enlists and delegates the real projector, then records the activated
/// pack, and notifies an observer only after the projection commits. <see cref="OnProject"/> refuses, faults or
/// cancels the projection; <see cref="ObserverFailure"/> fails the post-commit observer.
/// </summary>
internal sealed class StagedProjector(PackSeedProjector real, Func<PackProjectionAuthority, int> persistedBindings) : IPackProjectionDispatcher, IPackProjectionParticipant
{
    private int _notified;

    internal IReadOnlyList<string> Published { get; private set; } = [];
    internal int Notified => _notified;
    internal Func<PackProjectionAuthority, CancellationToken, object?>? OnProject { get; set; }
    internal Exception? ObserverFailure { get; set; }

    public void StageProjection(PackProjectionTransaction transaction)
    {
        transaction.Enlist(real);
        transaction.Stage(this, () =>
        {
        var original = Published;
        transaction.AfterCommit(() =>
        {
            Interlocked.Increment(ref _notified);
            return ObserverFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        });
        return () => Published = original;
        });
    }

    public object? Project(PackProjectionAuthority authority, CancellationToken cancellationToken = default)
    {
        var actual = ((IPackProjectionDispatcher)real).Project(authority, cancellationToken);
        // Prove the seam runs AFTER a real durable authorization write, including on refused/faulted calls.
        Assert.False(actual is IPackProjectionRefusalReport { ProjectionRefused: true });
        Assert.Equal(1, persistedBindings(authority));
        var result = OnProject?.Invoke(authority, cancellationToken) ?? actual;
        Published = [.. Published, $"{authority.PackId}@{authority.PackVersion}"];
        return result;
    }
}

/// <summary>A projection pass that refused the pack's definitions.</summary>
internal sealed class RefusedProjection : IPackProjectionRefusalReport
{
    public bool ProjectionRefused => true;
}

/// <summary>Counts every kernel write that reaches React, after its commit stage has staged its rows.</summary>
internal sealed class StageObserver : IWritePipelineObserver
{
    private int _reacts;

    internal Action<int>? OnReact { get; set; }

    public void OnStage(WritePipelineStage stage)
    {
        if (stage == WritePipelineStage.React) OnReact?.Invoke(Interlocked.Increment(ref _reacts));
    }
}

/// <summary>The real exporter, whose signer can be suspended so a concurrent write lands after admission.</summary>
internal sealed class SuspendableExporter(IPackExporter inner) : IPackExporter
{
    private int _exports;
    private Suspension? _suspension;

    internal int Exports => _exports;

    internal Suspension Suspend() => _suspension = new Suspension();

    public ValueTask<PackValidationResult> ValidateAsync(PackExportRequest request, CancellationToken ct = default) =>
        inner.ValidateAsync(request, ct);

    public ValueTask<PackExportOutcome> ExportAsync(PackExportRequest request, IOperationSigner signer, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _exports);
        return inner.ExportAsync(request, _suspension is { } suspension ? new SuspendedSigner(signer, suspension) : signer, ct);
    }

    internal sealed class Suspension
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SuspendedSigner(IOperationSigner inner, Suspension suspension) : IOperationSigner
    {
        public PrincipalId IssuerId => inner.IssuerId;

        public async ValueTask<SignedOperation<T>> SignAsync<T>(T payload, DateTimeOffset issuedAt, Guid nonce,
            CancellationToken ct = default)
        {
            suspension.Entered.TrySetResult();
            await suspension.Release.Task.WaitAsync(ct);
            return await inner.SignAsync(payload, issuedAt, nonce, ct);
        }
    }
}

internal sealed class AtomicActiveTeam(TeamContext? active) : IActiveTeamAccessor
{
    public TeamContext? Active { get; set; } = active;
    public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
    public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
    internal void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
}
