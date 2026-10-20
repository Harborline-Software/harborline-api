using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Configuration;
using Harborline.Blocks.BuilderDefinitions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Goal 3 phase P2, the Records install-closure gate at its enforcing point (T-615, DES-0015 records-ck-41). Install runs
/// the composed Records gate in its mutate stage, after the plan is built from verified bytes and before validate seals
/// anything for commit. A refused Record Type is a hard admission refusal: no seed layer, watermark, override, key
/// ownership or Installed audit entry, exactly one Refused audit entry, and no break-glass. On the Released path the
/// release transaction never commits. The gate runs only where a host sets <see cref="PackInstaller.RecordsAdmission"/>;
/// production composition does not (G-HOST), which <see cref="Without_a_composed_gate_install_is_unchanged"/> pins.
/// </summary>
/// <remarks>
/// Oracles: install codes and content pointers are literals from the installer's existing admission refusal shape
/// (<c>pack.install.refused.admission</c>, <c>/contents/{index}/contentBase64</c> plus the gate's body pointer); Records
/// codes and body pointers are literals from the Platform install gate contract at Platform
/// 2c7bef291c6f5adc6d4a7a68885bb7627f20d68c (RecordTypeDefinitionStore.InstallEdges), unchanged at Platform
/// c3df8c33b8de2812c8a72f5ec40e2df73c656062 and at the current pin 5ec3cf53. Bodies and the pin digest are the
/// literals of <see cref="RecordsInstallClosureGateTests"/>. The Released case reuses
/// <see cref="ReleasedPackAtomicFixture"/> unchanged and composes its own gated engine beside the fixture's.
/// Goal 3 P3: a producer's signed exposed-definition entries reach the gate through the install engine. The P3 cases use
/// the P0 Meter literals on the signed path and the Platform Goal 3 P1 asset-class literals (catalogue version 3.1.4,
/// its store digest, producer revision 7.0.0) on the Released path.
/// </remarks>
public sealed class RecordsInstallEnforcementTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant-b");
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private const string Meter =
        """{"class_id":"eam.equipment","envelope":{"contract":{"major":1,"minor":0},"exposes":{"interface_version":1},"package_id":"eam-core","section":"eam","tenant":"tenant-a"},"fields":[{"display_name":"Reading","field_key":"reading"}],"name":"Meter","record_class":"master","record_type_id":"eam.meter"}""";

    private const string Route =
        """{"class_id":"fleet-ops.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"fleet-ops","section":"fleet-ops","tenant":"tenant-a"},"fields":[{"display_name":"Name","field_key":"name"}],"name":"Route","record_class":"master","record_type_id":"fleet-ops.route"}""";

    private const string Dispatch =
        """{"class_id":"fleet-ops.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"fleet-ops","section":"fleet-ops","tenant":"tenant-a"},"fields":[{"display_name":"Route","field_key":"route","reference":{"cardinality":"one","on_delete":"block","parent":false,"target_type_id":"fleet-ops.route"}}],"name":"Dispatch","record_class":"transactional","record_type_id":"fleet-ops.dispatch"}""";

    private const string MeterDigest = "sha256:a0eda87a3a13a042813b0a8f3b09ede94811258877e5629a944766ea6160a919";

    private static string WorkOrder(int pinnedInterface) =>
        $$$"""{"class_id":"fleet-ops.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"fleet-ops","requires":[{"capability":"eam-core@1"}],"section":"fleet-ops","tenant":"tenant-a"},"fields":[{"display_name":"Meter","field_key":"meter","reference":{"cardinality":"one","on_delete":"block","parent":false,"pin":{"definition_id":"eam.meter","digest":"sha256:a0eda87a3a13a042813b0a8f3b09ede94811258877e5629a944766ea6160a919","interface_version":{{{pinnedInterface}}},"package_id":"eam-core","version":"1.0.0"},"target_type_id":"eam.meter"}},{"display_name":"Route","field_key":"route","reference":{"cardinality":"one","on_delete":"block","parent":false,"target_type_id":"fleet-ops.route"}}],"name":"Work Order","record_class":"transactional","record_type_id":"fleet-ops.work-order"}""";

    // A Released Record Type of the release package pinning acme.finance's invoice. The pin's version is the release
    // revision 1.1.0, the version every released item carries, so a version taken from the item would satisfy it.
    private const string ReleasedWorkOrder =
        """{"class_id":"release.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"tenant.release","requires":[{"capability":"acme.finance@1"}],"section":"release","tenant":"tenant-a"},"fields":[{"display_name":"Invoice","field_key":"invoice","reference":{"cardinality":"one","on_delete":"block","parent":false,"pin":{"definition_id":"finance.invoice","digest":"sha256:a0eda87a3a13a042813b0a8f3b09ede94811258877e5629a944766ea6160a919","interface_version":1,"package_id":"acme.finance","version":"1.1.0"},"target_type_id":"finance.invoice"}}],"name":"Work Order","record_class":"transactional","record_type_id":"release.work-order"}""";

    private readonly KeyPair _keyPair = KeyPair.Generate();
    private readonly PackFileCodec _codec = new();
    private readonly InMemoryPackInstallStore _store = new();
    private readonly InMemoryPackInstallAudit _audit = new();
    private readonly PackInstallContext _context;
    private int _gateCalls;

    public RecordsInstallEnforcementTests()
    {
        _context = new PackInstallContext(
            Tenant,
            new InMemoryPackTrustStore(
            [
                new PackTrustRoot(TrustScope.OwnRoster, _keyPair.PrincipalId, 1, TrustRootStatus.Current),
            ]),
            PackRevocationList.Empty,
            AdmittedInstant.FromRecordedAct(Now),
            TimeSpan.FromDays(30),
            Principal: "test-operator");
    }

    public void Dispose() => _keyPair.Dispose();

    [Fact(DisplayName = "goal 3 T-POS: a signed pack whose Record Type edges stay inside it installs through the gate")]
    public async Task In_pack_record_types_install()
    {
        var outcome = await Installer().InstallAsync(
            await ExportAsync("fleet-ops", [("fleet-ops.dispatch", Dispatch), ("fleet-ops.route", Route)]), _context);

        Assert.True(outcome.Installed, string.Join("; ", outcome.RefusalCodes));
        Assert.Equal(2, _gateCalls);
        var installed = Assert.Single(_store.ListInstalled(Tenant));
        Assert.Equal(("fleet-ops", "1.0.0", PackLifecycleState.Draft), (installed.PackKey, installed.Version, installed.Lifecycle));
        Assert.Equal(["fleet-ops.dispatch", "fleet-ops.route"], installed.SeedItems.Select(item => item.Key));
    }

    [Fact(DisplayName = "goal 3 T-MISS-V/T-ATOM: a cross-package pin refuses with no effect even beside its exact signed exposing producer")]
    public async Task Cross_package_pin_refuses_with_no_effect()
    {
        var installer = Installer();
        Assert.True((await installer.InstallAsync(await ExportProducerAsync(), _context)).Installed);
        var before = Snapshot();

        var outcome = await installer.InstallAsync(await ExportConsumerAsync(pinnedInterface: 1), _context);

        Assert.False(outcome.Installed);
        Assert.False(outcome.BrokeGlass);
        Assert.Equal(PackInstallVerdict.Refused, outcome.Preview.Verdict);
        Assert.Equal(["pack.install.refused.admission"], outcome.RefusalCodes);
        Assert.Equal([("pack.install.refused.admission", "/contents/0/contentBase64/fields/0/reference/pin")],
            outcome.Preview.Refusals.Select(refusal => (refusal.Code, refusal.Pointer)));
        Assert.Equal([("fleet-ops.work-order", "records.reference.closure_missing", "/fields/0/reference/pin")],
            outcome.Preview.AdmissionRefusals.Select(refusal => (refusal.ContentKey, refusal.Code, refusal.Pointer)));
        AssertNoConsumerEffect(before);
    }

    [Fact(DisplayName = "goal 3 T-ATOM: a changed pin refuses every finding together with no effect")]
    public async Task Changed_pin_refuses_with_no_effect()
    {
        var installer = Installer();
        Assert.True((await installer.InstallAsync(await ExportProducerAsync(), _context)).Installed);
        var before = Snapshot();

        var outcome = await installer.InstallAsync(await ExportConsumerAsync(pinnedInterface: 2), _context);

        Assert.False(outcome.Installed);
        Assert.Equal(
            [("pack.install.refused.admission", "/contents/0/contentBase64/fields/0/reference"),
             ("pack.install.refused.admission", "/contents/0/contentBase64/fields/0/reference/pin")],
            outcome.Preview.Refusals.Select(refusal => (refusal.Code, refusal.Pointer)));
        Assert.Equal(["records.reference.interface_incompatible", "records.reference.closure_missing"],
            outcome.Preview.AdmissionRefusals.Select(refusal => refusal.Code));
        AssertNoConsumerEffect(before);
    }

    [Fact(DisplayName = "goal 3: a plan the installer already refuses keeps its own refusal and never reaches the gate")]
    public async Task An_already_refused_plan_is_not_regated()
    {
        var outcome = await Installer().InstallAsync(await ExportConsumerAsync(pinnedInterface: 1), _context);

        Assert.False(outcome.Installed);
        Assert.Equal(["pack.install.refused.unmet_dependency"], outcome.RefusalCodes);
        Assert.Empty(outcome.Preview.AdmissionRefusals);
        Assert.Equal(0, _gateCalls);
        Assert.Empty(_store.ListInstalled(Tenant));
    }

    [Fact(DisplayName = "goal 3 T-ATOM: break-glass cannot carry a Records refusal through a downgrade")]
    public async Task Break_glass_does_not_override_a_records_refusal()
    {
        var installer = Installer();
        Assert.True((await installer.InstallAsync(
            await ExportAsync("fleet-ops", [("fleet-ops.dispatch", Dispatch), ("fleet-ops.route", Route)], version: "2.0.0"), _context)).Installed);

        // 1.0.0 is below the 2.0.0 watermark, so it would install under break-glass, but its dispatch edge has no route.
        var outcome = await installer.InstallAsync(
            await ExportAsync("fleet-ops", [("fleet-ops.dispatch", Dispatch)], version: "1.0.0"),
            _context with { BreakGlass = new BreakGlass("goal 3 downgrade rehearsal", "test-approver") });

        Assert.False(outcome.Installed);
        Assert.False(outcome.BrokeGlass);
        Assert.Equal(PackInstallVerdict.Refused, outcome.Preview.Verdict);
        Assert.Equal(["pack.install.refused.admission"], outcome.RefusalCodes);
        Assert.Equal([("fleet-ops.dispatch", "records.reference.target_unresolved", "/fields/0/reference/target_type_id")],
            outcome.Preview.AdmissionRefusals.Select(refusal => (refusal.ContentKey, refusal.Code, refusal.Pointer)));
        Assert.Equal([("fleet-ops", "2.0.0")], _store.ListInstalled(Tenant).Select(pack => (pack.PackKey, pack.Version)));
        Assert.Equal("2.0.0", _store.GetWatermark(Tenant, "fleet-ops")!.Version);
        Assert.DoesNotContain(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.BreakGlassOverride);
    }

    [Fact(DisplayName = "goal 3 G-DISC/T-ATOM: a cross-package reference in a body without record_type_id refuses with no effect")]
    public async Task Cross_package_reference_without_record_type_id_refuses_with_no_effect()
    {
        var installer = Installer();
        Assert.True((await installer.InstallAsync(await ExportProducerAsync(), _context)).Installed);
        var before = Snapshot();
        var calls = _gateCalls;
        var unidentified = WorkOrder(pinnedInterface: 1)
            .Replace(",\"record_type_id\":\"fleet-ops.work-order\"", "", StringComparison.Ordinal);
        Assert.DoesNotContain("record_type_id", unidentified, StringComparison.Ordinal);

        var outcome = await installer.InstallAsync(
            await ExportAsync("fleet-ops", [("fleet-ops.work-order", unidentified), ("fleet-ops.route", Route)],
                [new PackDependencyRef("eam-core", "1.0.0")]), _context);

        // Gated under the empty id (owner decision P10), the body stops at the Platform's structural refusal
        // records.identity.record_type_id_required, which refuses under the declared fallback; the Route is gated too.
        Assert.False(outcome.Installed);
        Assert.False(outcome.BrokeGlass);
        Assert.Equal(["pack.install.refused.admission"], outcome.RefusalCodes);
        Assert.Equal([("pack.install.refused.admission", "/contents/0/contentBase64/record_type_id")],
            outcome.Preview.Refusals.Select(refusal => (refusal.Code, refusal.Pointer)));
        Assert.Equal([("fleet-ops.work-order", "pack.install.refused.records_unpublished_refusal", "/record_type_id")],
            outcome.Preview.AdmissionRefusals.Select(refusal => (refusal.ContentKey, refusal.Code, refusal.Pointer)));
        Assert.Equal(calls + 2, _gateCalls);
        AssertNoConsumerEffect(before);
    }

    [Fact(DisplayName = "goal 3 (G-HOST): with no Records gate composed, as in production today, install keeps its existing behaviour")]
    public async Task Without_a_composed_gate_install_is_unchanged()
    {
        var installer = Installer(composeGate: false);
        Assert.True((await installer.InstallAsync(await ExportProducerAsync(), _context)).Installed);

        var outcome = await installer.InstallAsync(await ExportConsumerAsync(pinnedInterface: 1), _context);

        // The cross-package pin installs unchecked: the gate is opt-in and default-off until a host composes it.
        Assert.True(outcome.Installed, string.Join("; ", outcome.RefusalCodes));
        Assert.Equal(0, _gateCalls);
        Assert.Null(new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), _codec), _store, new WorkflowRefusingPackContentAdmission(), _audit,
            TestAuthorization.AllowGate()).RecordsAdmission);
    }

    [Fact(DisplayName = "goal 3 T-LEGACY: a legacy asset type of the same transport kind installs without reaching the gate")]
    public async Task Legacy_asset_types_are_not_gated()
    {
        var outcome = await Installer().InstallAsync(
            await ExportAsync("legacy-assets", [("legacy.route", """{"displayName":"Route"}""")]), _context);

        Assert.True(outcome.Installed, string.Join("; ", outcome.RefusalCodes));
        Assert.Equal(0, _gateCalls);
    }

    [Fact(DisplayName = "goal 3 G-CHECK: Check and Preview never promise an install the composed gate has not evaluated")]
    public async Task Check_and_preview_never_promise_an_install_the_gate_has_not_evaluated()
    {
        var installer = Installer();
        Assert.True((await installer.InstallAsync(await ExportProducerAsync(), _context)).Installed);
        var consumer = await ExportConsumerAsync(pinnedInterface: 1);
        var legacy = await ExportAsync("legacy-assets", [("legacy.route", """{"displayName":"Route"}""")]);
        var calls = _gateCalls;

        var check = installer.Check(consumer, _context);
        var preview = installer.Preview(consumer, _context);

        // Install refuses this pack (Cross_package_pin_refuses_with_no_effect); the synchronous dry runs cannot run the
        // asynchronous gate, so they refuse with the not-evaluated code at each Record Type rather than say WouldInstall.
        Assert.Equal(PackInstallVerdict.Refused, check.Verdict);
        Assert.Equal(["pack.install.refused.records_gate_not_evaluated"], check.RefusalCodes);
        Assert.Equal(
            [("pack.install.refused.records_gate_not_evaluated", "/contents/0/contentBase64"),
             ("pack.install.refused.records_gate_not_evaluated", "/contents/1/contentBase64")],
            check.Refusals.Select(refusal => (refusal.Code, refusal.Pointer)));
        Assert.Equal(PackInstallVerdict.Refused, preview.Verdict);
        Assert.Equal(["pack.install.refused.records_gate_not_evaluated"], preview.RefusalCodes);
        Assert.Equal(calls, _gateCalls);
        // No Record Type, or no gate composed: the dry runs are unchanged.
        Assert.Equal(PackInstallVerdict.WouldInstall, installer.Check(legacy, _context).Verdict);
        Assert.Equal(PackInstallVerdict.WouldInstall, Installer(composeGate: false).Check(consumer, _context).Verdict);
        Assert.Equal(PackInstallVerdict.WouldInstall, Installer(composeGate: false).Preview(consumer, _context).Verdict);
        Assert.Equal(["eam-core"], _store.ListInstalled(Tenant).Select(pack => pack.PackKey));
    }

    [Fact(DisplayName = "goal 3 G-CHECK: dry runs append the not-evaluated refusal to existing ones and keep an already refused preview")]
    public async Task Dry_runs_keep_existing_refusals_beside_the_not_evaluated_gate()
    {
        var installer = Installer();
        var consumer = await ExportConsumerAsync(pinnedInterface: 1);

        // (a) Check appends to what it collected: no producer is installed, so the dependency is unmet too.
        var check = installer.Check(consumer, _context);
        Assert.Equal(PackInstallVerdict.Refused, check.Verdict);
        Assert.Equal(["pack.install.refused.unmet_dependency", "pack.install.refused.records_gate_not_evaluated"], check.RefusalCodes);
        Assert.Equal(
            [("pack.install.refused.unmet_dependency", "/dependencies/0"),
             ("pack.install.refused.records_gate_not_evaluated", "/contents/0/contentBase64"),
             ("pack.install.refused.records_gate_not_evaluated", "/contents/1/contentBase64")],
            check.Refusals.Select(refusal => (refusal.Code, refusal.Pointer)));

        // (b) Preview leaves an already refused plan exactly as it was.
        var refused = installer.Preview(consumer, _context);
        Assert.Equal(PackInstallVerdict.Refused, refused.Verdict);
        Assert.Equal(["pack.install.refused.unmet_dependency"], refused.RefusalCodes);
        Assert.DoesNotContain(refused.Refusals, refusal => refusal.Code == "pack.install.refused.records_gate_not_evaluated");

        // (c) A break-glass downgrade keeps its watermark refusal and code beside the not-evaluated ones (F1).
        Assert.True((await installer.InstallAsync(
            await ExportAsync("fleet-ops", [("fleet-ops.dispatch", Dispatch), ("fleet-ops.route", Route)], version: "2.0.0"), _context)).Installed);
        var downgrade = await ExportAsync("fleet-ops", [("fleet-ops.dispatch", Dispatch), ("fleet-ops.route", Route)], version: "1.0.0");
        var calls = _gateCalls;
        Assert.Equal(PackInstallVerdict.RequiresBreakGlass, Installer(composeGate: false).Preview(downgrade, _context).Verdict);

        foreach (var preview in new[] { installer.Preview(downgrade, _context), installer.Check(downgrade, _context) })
        {
            Assert.Equal(PackInstallVerdict.Refused, preview.Verdict);
            Assert.Equal(["pack.install.refused.downgrade", "pack.install.refused.records_gate_not_evaluated"], preview.RefusalCodes);
            Assert.Equal(
                [("pack.install.refused.downgrade", "/"),
                 ("pack.install.refused.records_gate_not_evaluated", "/contents/0/contentBase64"),
                 ("pack.install.refused.records_gate_not_evaluated", "/contents/1/contentBase64")],
                preview.Refusals.Select(refusal => (refusal.Code, refusal.Pointer)));
        }
        Assert.Equal(calls, _gateCalls);
        Assert.Equal("2.0.0", _store.GetWatermark(Tenant, "fleet-ops")!.Version);
    }

    [Fact(DisplayName = "goal 3 T-ATOM: a Released cross-package pin commits nothing, audits one refusal and never breaks glass")]
    public async Task A_released_cross_package_pin_commits_nothing()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var records = RecordsInstallClosureGateTests.Records();
        var gateCalls = 0;
        // The fixture's own composition, with the Records gate composed into a second engine over the same stores.
        var engine = new PackInstaller(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), fixture.Reader,
            fixture.Store, fixture.Store, fixture.Admission, fixture.Audit, TestAuthorization.AllowGate(), fixture.Projector)
        {
            RecordsAdmission = (tenant, entry, closure, cancellationToken) =>
            {
                Interlocked.Increment(ref gateCalls);
                return records.AdmitInstallAsync(tenant, entry, closure, cancellationToken);
            },
        };
        var installer = new RecordingInstaller(engine, fixture.Harness.Store.PacksFactory);
        var releases = new ReleasedPackInstaller(fixture.Proposals, fixture.Exporter, installer, fixture.Node,
            new Ed25519Verifier(), HostedPackInstallApiEndpoint.BuildTrustStore(fixture.Node, NullLogger.Instance),
            PackRevocationList.Empty, fixture.Reader, TestPackGate.AllowAll());
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("records/work-order", ReleasedPackAtomicFixture.ReleaseKey, "AssetTypeDefinition", ReleasedWorkOrder)],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();
        var outbox = await fixture.OutboxCountAsync();

        var outcome = await releases.InstallAsync(fixture.Tenant, digest,
            TestAuthorization.Write(fixture.Tenant, ReleasedPackAtomicFixture.Principal, ReleasedPackAtomicFixture.Frozen));

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Installed);
        // G-DETAIL (held): the Released route reports the engine's code only; the Records code stays in the engine.
        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal(("pack.install.refused.admission", "releasedPackage"), (refusal.Code, refusal.Target));
        Assert.Equal(1, gateCalls);
        Assert.Equal((1, 0, 0), (installer.Installs, installer.Activations, installer.Narrowings));
        // The release transaction did not commit: nothing it staged persists, in memory or on a restart.
        Assert.Null(fixture.Store.GetVersion(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey, "1.1.0"));
        Assert.Null(fixture.Store.GetWatermark(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey));
        Assert.Empty(fixture.Projector.Published);
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
        Assert.Equal(outbox, await fixture.OutboxCountAsync());
        Assert.Empty(fixture.Audit.Successes());
        var refused = Assert.Single(fixture.Audit.Published, entry => entry.Action == PackInstallAuditAction.Refused);
        Assert.Equal((ReleasedPackAtomicFixture.ReleaseKey, "1.1.0", "pack.install.refused.admission"),
            (refused.PackKey, refused.Version, refused.Detail));
        Assert.DoesNotContain(fixture.Audit.Published, entry => entry.Action == PackInstallAuditAction.BreakGlassOverride);
    }

    [Fact(DisplayName = "goal 3 P3 T-ATOM: a pin at the exact producer's signed facts installs, and a pin at its package revision commits nothing")]
    public async Task A_pin_at_the_exact_producers_signed_facts_installs_and_a_changed_one_commits_nothing()
    {
        var installer = Installer();
        var signed = new PackExposedDefinition("eam.meter", "1.0.0", MeterDigest);
        // eam-core at package revision 7.0.0 signs the meter's catalogue version 1.0.0 and its store digest.
        Assert.True((await installer.InstallAsync(await ExportAsync("eam-core", [("eam.meter", Meter)], exposes: ["eam.meter"],
            interfaceVersion: 1, version: "7.0.0", exposedDefinitions: [signed]), _context)).Installed);
        // The installed pack persists the entries exactly as signed.
        Assert.Equal([signed], Assert.Single(_store.ListInstalled(Tenant)).ExposedDefinitions);
        var before = Snapshot();
        PackDependencyRef[] pinned = [new("eam-core", "7.0.0")];

        var changed = await installer.InstallAsync(await ExportAsync("fleet-ops",
            [("fleet-ops.work-order", WorkOrder(1).Replace("\"version\":\"1.0.0\"", "\"version\":\"7.0.0\"", StringComparison.Ordinal)),
             ("fleet-ops.route", Route)], pinned), _context);

        Assert.False(changed.Installed);
        Assert.False(changed.BrokeGlass);
        Assert.Equal(["pack.install.refused.admission"], changed.RefusalCodes);
        Assert.Equal([("fleet-ops.work-order", "records.reference.closure_changed", "/fields/0/reference/pin")],
            changed.Preview.AdmissionRefusals.Select(refusal => (refusal.ContentKey, refusal.Code, refusal.Pointer)));
        AssertNoConsumerEffect(before);

        var genuine = await installer.InstallAsync(await ExportAsync("fleet-ops",
            [("fleet-ops.work-order", WorkOrder(1)), ("fleet-ops.route", Route)], pinned), _context);

        Assert.True(genuine.Installed, string.Join("; ", genuine.RefusalCodes));
        Assert.Empty(genuine.Preview.AdmissionRefusals);
        Assert.Equal([("eam-core", "7.0.0"), ("fleet-ops", "1.0.0")],
            _store.ListInstalled(Tenant).Select(pack => (pack.PackKey, pack.Version)).OrderBy(pair => pair.PackKey, StringComparer.Ordinal));
    }

    [Fact(DisplayName = "goal 3 P3 T-ATOM: a Released pin at the producer's signed facts installs, and a changed one commits nothing")]
    public async Task A_released_pin_at_the_carried_facts_installs_and_a_changed_one_commits_nothing()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        // eam-core 7.0.0 as a signed install persists it: its asset class bound to catalogue version 3.1.4.
        var producer = RecordsInstallClosureGateTests.AssetClassProducer() with { Lifecycle = PackLifecycleState.Draft };
        fixture.Store.Commit(new PackInstallTransaction(fixture.Tenant, producer,
            new PackInstallWatermark("eam-core", RecordsInstallClosureGateTests.ProducerRevision, new Dictionary<string, int>()), []));
        fixture.Store.Activate(fixture.Tenant, "eam-core", RecordsInstallClosureGateTests.ProducerRevision);
        var records = RecordsInstallClosureGateTests.Records();
        var reports = new List<string>();
        // The fixture's stores and admission with the Records gate, and no projector, as the signed-path case above
        // and the projector-less Released route tests compose it. This case is about the gate, which decides at
        // install. Projection is out of its scope: at this tree the seed projector reads every AssetTypeDefinition
        // as a legacy asset type, so it refuses a Records-grammar body at activation
        // (pack.install.activate.projection_refused). That limitation predates P3 and is tracked on its own.
        var engine = new PackInstaller(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), fixture.Reader,
            fixture.Store, fixture.Store, fixture.Admission, fixture.Audit, TestAuthorization.AllowGate())
        {
            RecordsAdmission = async (tenant, entry, closure, cancellationToken) =>
            {
                var report = await records.AdmitInstallAsync(tenant, entry, closure, cancellationToken);
                lock (reports) reports.Add(string.Join(",", report.Refusals.Select(refusal => refusal.Code)));
                return report;
            },
        };
        var installer = new RecordingInstaller(engine, fixture.Harness.Store.PacksFactory);
        var releases = new ReleasedPackInstaller(fixture.Proposals, fixture.Exporter, installer, fixture.Node,
            new Ed25519Verifier(), HostedPackInstallApiEndpoint.BuildTrustStore(fixture.Node, NullLogger.Instance),
            PackRevocationList.Empty, fixture.Reader, TestPackGate.AllowAll());
        Task<string> Offer(string pinnedVersion) => fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.OwnNote,
             ("records/work-order", ReleasedPackAtomicFixture.ReleaseKey, "AssetTypeDefinition", ReleasedAssetWorkOrder(pinnedVersion))],
            ("eam-core", RecordsInstallClosureGateTests.ProducerRevision)));
        var authority = TestAuthorization.Write(fixture.Tenant, ReleasedPackAtomicFixture.Principal, ReleasedPackAtomicFixture.Frozen);

        // The pin names the producer's package revision, never its definition version.
        var changedDigest = await Offer(RecordsInstallClosureGateTests.ProducerRevision);
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();
        var outbox = await fixture.OutboxCountAsync();
        var changed = await releases.InstallAsync(fixture.Tenant, changedDigest, authority);

        Assert.False(changed.Succeeded);
        Assert.Equal(("pack.install.refused.admission", "releasedPackage"),
            (Assert.Single(changed.Refusals).Code, Assert.Single(changed.Refusals).Target));
        Assert.Equal(["records.reference.closure_changed"], reports);
        Assert.Null(fixture.Store.GetVersion(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey, "1.1.0"));
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
        Assert.Equal(outbox, await fixture.OutboxCountAsync());
        Assert.Empty(fixture.Audit.Successes());

        var genuine = await releases.InstallAsync(fixture.Tenant,
            await Offer(RecordsInstallClosureGateTests.AssetClassVersion), authority);

        Assert.Equal(["records.reference.closure_changed", ""], reports);
        Assert.True(genuine.Succeeded, string.Join("; ", genuine.Refusals.Select(refusal => $"{refusal.Code}: {refusal.Message}")));
        Assert.True(genuine.Installed);
        Assert.Equal((2, 1, 0), (installer.Installs, installer.Activations, installer.Narrowings));
        Assert.NotNull(fixture.Store.GetVersion(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey, "1.1.0"));
        // The release transaction committed: its own pack is Active at the release revision with the gated Record Type.
        var active = fixture.Store.GetActive(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey)!;
        Assert.Equal(("1.1.0", PackLifecycleState.Active), (active.Version, active.Lifecycle));
        Assert.Contains(active.SeedItems, item => item.Key == "records/work-order");
        Assert.NotEqual(durable, await fixture.DurableStateAsync());
    }

    // A Released Record Type of the release package pinning eam-core's asset class at a given version and its store digest.
    private static string ReleasedAssetWorkOrder(string version) =>
        $$$"""{"class_id":"release.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"tenant.release","requires":[{"capability":"eam-core@1"}],"section":"release","tenant":"tenant-a"},"fields":[{"display_name":"Asset","field_key":"asset","reference":{"cardinality":"one","on_delete":"block","parent":false,"pin":{"definition_id":"eam.asset-class","digest":"{{{RecordsInstallClosureGateTests.AssetClassDigest}}}","interface_version":1,"package_id":"eam-core","version":"{{{version}}}"},"target_type_id":"eam.asset-class"}}],"name":"Work Order","record_class":"transactional","record_type_id":"release.work-order"}""";

    private string Snapshot() => string.Join("\n",
        string.Join(",", _store.ListInstalled(Tenant).Select(pack => $"{pack.PackKey}@{pack.Version}:{pack.Lifecycle}")),
        string.Join(",", _store.GetKeyOwnership(Tenant).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}")));

    private void AssertNoConsumerEffect(string before)
    {
        Assert.Equal(before, Snapshot());
        Assert.Equal(["eam-core"], _store.ListInstalled(Tenant).Select(pack => pack.PackKey));
        Assert.Null(_store.GetWatermark(Tenant, "fleet-ops"));
        Assert.Empty(_store.GetOverrides(Tenant, "fleet-ops"));
        var consumer = Assert.Single(_audit.Query(Tenant), entry => entry.PackKey == "fleet-ops");
        Assert.Equal((PackInstallAuditAction.Refused, "pack.install.refused.admission"), (consumer.Action, consumer.Detail));
    }

    private PackInstaller Installer(bool composeGate = true)
    {
        var records = RecordsInstallClosureGateTests.Records();
        return new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), _codec),
            _store,
            new WorkflowRefusingPackContentAdmission(),
            _audit,
            TestAuthorization.AllowGate())
        {
            RecordsAdmission = composeGate ? new RecordsInstallClosureGate.AdmitInstall(Counted) : null,
        };

        ValueTask<DefinitionRefusalReport> Counted(string tenant, RecordTypeDefinitionPackageEntry entry,
            RecordsInstallClosure closure, CancellationToken cancellationToken)
        {
            _gateCalls++;
            return records.AdmitInstallAsync(tenant, entry, closure, cancellationToken);
        }
    }

    private Task<byte[]> ExportProducerAsync() =>
        ExportAsync("eam-core", [("eam.meter", Meter)], exposes: ["eam.meter"], interfaceVersion: 1);

    // The work order is the first content item, so its body pointer lands under /contents/0.
    private Task<byte[]> ExportConsumerAsync(int pinnedInterface) =>
        ExportAsync("fleet-ops", [("fleet-ops.work-order", WorkOrder(pinnedInterface)), ("fleet-ops.route", Route)],
            [new PackDependencyRef("eam-core", "1.0.0")]);

    private async Task<byte[]> ExportAsync(string key, (string Key, string Body)[] contents,
        PackDependencyRef[]? dependencies = null, IReadOnlyList<string>? exposes = null, int? interfaceVersion = null,
        string version = "1.0.0", IReadOnlyList<PackExposedDefinition>? exposedDefinitions = null)
    {
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            _codec, timeProvider: TimeProvider.System);
        var export = await exporter.ExportAsync(
            new PackExportRequest(
                Key: key,
                Version: version,
                Name: $"Records install closure test pack {key}",
                Description: "Exercises the goal 3 Records install gate.",
                ScopeTier: PackScopeTier.Horizontal,
                Contents: contents
                    .Select(content => new PackContentSource(content.Key, PackContentKind.AssetTypeDefinition, version,
                        JsonNode.Parse(content.Body)!))
                    .ToArray(),
                Dependencies: dependencies ?? [],
                CapabilityRequirements: Array.Empty<string>(),
                Epoch: 1,
                Dcp: DomainComplianceProfile.General("test-author"),
                Exposes: exposes,
                InterfaceVersion: interfaceVersion,
                ExposedDefinitions: exposedDefinitions),
            new Ed25519Signer(_keyPair));
        Assert.True(export.Succeeded, string.Join(
            "; ", export.Validation.Errors.Select(error => $"{error.Code}: {error.Message}")));
        return export.FileBytes!;
    }
}
