using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.LocalNodeHost.Tests.ArchTests;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Contracts.Fields;
using Harborline.Foundation.Definitions;
using Harborline.Foundation.FieldRuntime;
using Harborline.Kernel.SchemaValidation;
using Harborline.Kernel.SchemaValidation.Records;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Goal 3 phases P2 and P3, the API-side Records install-closure gate (T-615, DES-0015 records-ck-41). A verified seed
/// layer reaches the landed Platform gate <see cref="RecordTypeDefinitionStore.AdmitInstallAsync"/> through
/// <see cref="RecordsInstallClosureGate"/>. Every Record Type is identified by its body's <c>record_type_id</c>, never by
/// its content key, and admitted under the installing host's tenant. The closure's exposures come only from the exact
/// pinned producer's signed exposed-definition entries (P3, owner decision P10 Option A); a pack that pins no such
/// producer (<see cref="NoPins"/>, <see cref="NoProducers"/>, the P2 cases) still fails closed with
/// <c>records.reference.closure_missing</c>.
/// </summary>
/// <remarks>
/// Oracles: the refusal codes and pointers are literals from the Platform Records install gate contract at Platform
/// 2c7bef291c6f5adc6d4a7a68885bb7627f20d68c (RecordTypeDefinitionStore.InstallEdges, records-ck-41). Every one of them
/// is still emitted at Platform c3df8c33b8de2812c8a72f5ec40e2df73c656062 and at the current pin 5ec3cf53, but that install
/// vocabulary is larger: Platform PR 279 adds records.creation_role.required and records.creation_role.unexpected
/// (structural, every phase but Author) and records.creation_role.unknown (the install edge for a role gate with no
/// role vocabulary). The API does not publish those three, so they refuse under the fail-closed
/// pack.install.refused.records_unpublished_refusal fallback, which G-CODES pins. Whether to publish them is owner
/// question O-7, PENDING OWNER DECISION. The
/// bodies are literal snake_case documents in the published Records grammar (RecordTypeDocument,
/// RecordReferenceDefinition). <see cref="MeterDigest"/> and <see cref="MeterTransportDigest"/> were computed by a
/// separate reference implementation of the stored-body digest (SHA-256 of the canonical body with and without its
/// trailing LF), not by this code. No expected value is read from the adapter.
/// P3 oracles are literals from the Platform Goal 3 P1 commit (Platform main c3df8c33;
/// RecordInstallClosureContractTests): <see cref="ExposedAssetClassJson"/>, its catalogue version
/// <see cref="AssetClassVersion"/> and store digest <see cref="AssetClassDigest"/>, its LF-less digest
/// <see cref="AssetClassTransportDigest"/>, interface 1 and the producer package revision 7.0.0. The Meter literals
/// above are the P0 fixture's.
/// </remarks>
public sealed class RecordsInstallClosureGateTests
{
    private static readonly TenantId InstallingTenant = new("tenant-b");

    // The producer eam-core's stored eam.meter body ends in one LF; its sha256: digest is what an author's pin carries.
    private const string MeterDigest = "sha256:a0eda87a3a13a042813b0a8f3b09ede94811258877e5629a944766ea6160a919";

    // The same body without the LF: what a re-serialized transport copy (JCS seed bytes) hashes to (records-ck-40).
    private const string MeterTransportDigest = "sha256:c8673e0347e4a18a671d23f1e4fb9e59e129ec675d3ec13ac3b47a7ce074832c";

    // The producer's Meter exactly as eam-core stores it (without the LF), exposed at interface 1.
    private const string Meter =
        """{"class_id":"eam.equipment","envelope":{"contract":{"major":1,"minor":0},"exposes":{"interface_version":1},"package_id":"eam-core","section":"eam","tenant":"tenant-a"},"fields":[{"display_name":"Reading","field_key":"reading"}],"name":"Meter","record_class":"master","record_type_id":"eam.meter"}""";

    // Authored under tenant-a: the envelope tenant is the author's, and install does not compare it.
    private const string Route =
        """{"class_id":"fleet-ops.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"fleet-ops","section":"fleet-ops","tenant":"tenant-a"},"fields":[{"display_name":"Name","field_key":"name"}],"name":"Route","record_class":"master","record_type_id":"fleet-ops.route"}""";

    private const string Dispatch =
        """{"class_id":"fleet-ops.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"fleet-ops","section":"fleet-ops","tenant":"tenant-a"},"fields":[{"display_name":"Route","field_key":"route","reference":{"cardinality":"one","on_delete":"block","parent":false,"target_type_id":"fleet-ops.route"}}],"name":"Dispatch","record_class":"transactional","record_type_id":"fleet-ops.dispatch"}""";

    // A Class-shaped body under the Records transport kind: it names a class_id and no record_type_id.
    private const string AssetsClass =
        """{"class_id":"fleet-ops.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"fleet-ops","section":"fleet-ops","tenant":"tenant-a"},"name":"Assets"}""";

    // Field 0 targets the pack's own Class unpinned; field 1 targets eam-core's Class through a pin.
    private static readonly string Inspection =
        $$$"""{"class_id":"fleet-ops.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"fleet-ops","requires":[{"capability":"eam-core@1"}],"section":"fleet-ops","tenant":"tenant-a"},"fields":[{"display_name":"Asset","field_key":"asset","reference":{"cardinality":"one","on_delete":"block","parent":false,"target_class_id":"fleet-ops.assets"}},{"display_name":"Equipment","field_key":"equipment","reference":{"cardinality":"one","on_delete":"block","parent":false,"pin":{"definition_id":"eam.equipment","digest":"{{{MeterDigest}}}","interface_version":1,"package_id":"eam-core","version":"1.0.0"},"target_class_id":"eam.equipment"}}],"name":"Inspection","record_class":"transactional","record_type_id":"fleet-ops.inspection"}""";

    private static string WorkOrder(string pin, string requires = "eam-core@1") =>
        $$$"""{"class_id":"fleet-ops.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"fleet-ops","requires":[{"capability":"{{{requires}}}"}],"section":"fleet-ops","tenant":"tenant-a"},"fields":[{"display_name":"Meter","field_key":"meter","reference":{"cardinality":"one","on_delete":"block","parent":false,"pin":{{{pin}}},"target_type_id":"eam.meter"}},{"display_name":"Route","field_key":"route","reference":{"cardinality":"one","on_delete":"block","parent":false,"target_type_id":"fleet-ops.route"}}],"name":"Work Order","record_class":"transactional","record_type_id":"fleet-ops.work-order"}""";

    private static readonly string GenuinePin =
        $$"""{"definition_id":"eam.meter","digest":"{{MeterDigest}}","interface_version":1,"package_id":"eam-core","version":"1.0.0"}""";

    // A legacy asset type: the same transport kind, camelCase grammar, no record_type_id.
    private const string LegacyAssetType = """{"displayName":"Route","parentType":"fleet-ops.base"}""";

    // P2 shapes: the pack pins no installed producer, so the closure offers no exposure.
    private static readonly PackDependencyRef[] NoPins = [];
    private static readonly InstalledPack[] NoProducers = [];

    // Goal 3 P1 (Platform main c3df8c33): eam-core's asset class exposed at interface 1, exactly as the store holds it.
    private const string ExposedAssetClassJson =
        """{"class_id":"eam.equipment","envelope":{"contract":{"major":1,"minor":0},"exposes":{"interface_version":1},"package_id":"eam-core","section":"eam","tenant":"tenant-a"},"fields":[{"binding":{"constraints":{"maximum_count":1,"minimum_count":0,"read_role_ids":[],"required":true},"kind":{"kind_id":"text","parameters":{},"version":"1.0.0"}},"defaults_provenance":{"kind_id":"text","kind_version":"1.0.0"},"display_name":"Asset tag","field_key":"asset_tag","governance":{"classification":"internal","confidential":false,"masked":true,"personal_data":true}},{"binding":{"constraints":{"maximum_count":1,"minimum_count":0,"read_role_ids":[],"required":false},"kind":{"kind_id":"count","parameters":{},"version":"1.0.0"}},"defaults_provenance":{"kind_id":"count","kind_version":"1.0.0"},"display_name":"Quantity","field_key":"quantity"},{"display_name":"Notes","field_key":"notes"}],"name":"Asset Class","record_class":"master","record_type_id":"eam.asset-class"}""" + "\n";

    // The bound revision's catalogue version, which appears nowhere in the released bytes but the signed entry.
    internal const string AssetClassVersion = "3.1.4";

    // The sha256: store digest of ExposedAssetClassJson as stored (with its LF), and of the same bytes without the LF.
    internal const string AssetClassDigest = "sha256:ca09327e39bb416e6a5e7025adf6e6c2ecf9967bcc2b48122ed14d1eb99e6caf";
    private const string AssetClassTransportDigest = "sha256:132e1732454a8d09720b388199664f316b4c6744c80ef300ec3f9f279db1c78d";

    // The producer's package revision, which is never a definition version.
    internal const string ProducerRevision = "7.0.0";

    private static string AssetWorkOrder(string version, string digest) =>
        $$$"""{"class_id":"fleet-ops.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"fleet-ops","requires":[{"capability":"eam-core@1"}],"section":"fleet-ops","tenant":"tenant-a"},"fields":[{"display_name":"Asset","field_key":"asset","reference":{"cardinality":"one","on_delete":"block","parent":false,"pin":{"definition_id":"eam.asset-class","digest":"{{{digest}}}","interface_version":1,"package_id":"eam-core","version":"{{{version}}}"},"target_type_id":"eam.asset-class"}}],"name":"Work Order","record_class":"transactional","record_type_id":"fleet-ops.work-order"}""";

    private static readonly PackExposedDefinition AssetClassEntry = new("records/eam.asset-class", AssetClassVersion, AssetClassDigest);

    private static readonly PinnedExposure AssetClassExposure =
        new("eam-core", DefinitionKind.Records, "eam.asset-class", AssetClassVersion, AssetClassDigest, 1);

    [Fact(DisplayName = "goal 3 T-POS: a pack whose edges stay inside it installs, from a closure of the Record Types it ships")]
    public async Task In_pack_edges_install_from_the_shipped_record_types()
    {
        var admit = new RecordingGate();

        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/dispatch", Dispatch), Seed("records/route", Route)], NoPins, NoProducers, admit.AdmitAsync);

        Assert.True(result.IsAdmissible, Describe(result));
        Assert.Equal(["tenant-b", "tenant-b"], admit.Tenants);
        var closure = admit.Closures[0];
        Assert.Equal(
            [new InstallDefinitionTarget(DefinitionKind.Records, "fleet-ops.dispatch"),
             new InstallDefinitionTarget(DefinitionKind.Records, "fleet-ops.route")],
            closure.PackDefinitions);
        Assert.Empty(closure.Exposures);
        Assert.Equal(["fleet-ops.dispatch", "fleet-ops.route"], admit.EntryIds);
    }

    [Fact(DisplayName = "goal 3 T-MISS-V: a genuine cross-package pin refuses closure_missing until the producer signs version and digest")]
    public async Task A_genuine_cross_package_pin_is_held_by_the_missing_producer_facts()
    {
        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", WorkOrder(GenuinePin)), Seed("records/route", Route)], NoPins, NoProducers, Records().AdmitInstallAsync);

        Assert.Equal(["records/work-order|records.reference.closure_missing|/fields/0/reference/pin"], Triples(result));
    }

    [Fact(DisplayName = "goal 3 T-MISS-V: the producer's exposed body beside the pin, at the pinned version, signs no version and still refuses")]
    public async Task A_producer_that_exposes_the_key_without_a_signed_version_refuses_closure_missing()
    {
        var admit = new RecordingGate();

        // The meter ships at the pin's version and exposes its key, so a version read from the item, its CID or the
        // pin itself would satisfy the pin; nothing signed states the producer's catalogue version.
        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", WorkOrder(GenuinePin)), Seed("records/route", Route), Seed("records/meter", Meter, "1.0.0")], NoPins, NoProducers,
            admit.AdmitAsync);

        Assert.Equal(["records/work-order|records.reference.closure_missing|/fields/0/reference/pin"], Triples(result));
        Assert.All(admit.Closures, closure => Assert.Empty(closure.Exposures));
    }

    [Fact(DisplayName = "goal 3: a content key or a legacy asset type cannot stand in for the Record Type a reference names")]
    public async Task Membership_is_the_body_identity_not_the_content_key()
    {
        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
        [
            Seed("records/dispatch", Dispatch),
            Seed("fleet-ops.route", LegacyAssetType),
            Seed("records/other", Route.Replace("\"fleet-ops.route\"", "\"fleet-ops.other\"", StringComparison.Ordinal)),
        ], NoPins, NoProducers, Records().AdmitInstallAsync);

        Assert.Equal(["records/dispatch|records.reference.target_unresolved|/fields/0/reference/target_type_id"], Triples(result));
    }

    [Fact(DisplayName = "goal 3: a pin that names the installing pack's own package is forged and refuses")]
    public async Task An_own_package_pin_refuses()
    {
        var forged = GenuinePin.Replace("\"package_id\":\"eam-core\"", "\"package_id\":\"fleet-ops\"", StringComparison.Ordinal);

        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", WorkOrder(forged)), Seed("records/route", Route)], NoPins, NoProducers, Records().AdmitInstallAsync);

        Assert.Equal(["records/work-order|records.reference.pin_unexpected|/fields/0/reference/pin"], Triples(result));
    }

    public static TheoryData<string, string, string, string[]> ChangedPins() => new()
    {
        {
            "pin interface changed", GenuinePin.Replace("\"interface_version\":1", "\"interface_version\":2", StringComparison.Ordinal), "eam-core@1",
            ["records/work-order|records.reference.interface_incompatible|/fields/0/reference",
             "records/work-order|records.reference.closure_missing|/fields/0/reference/pin"]
        },
        {
            "requirement changed", GenuinePin, "eam-core@2",
            ["records/work-order|records.reference.interface_incompatible|/fields/0/reference",
             "records/work-order|records.reference.closure_missing|/fields/0/reference/pin"]
        },
        {
            "requirement missing", GenuinePin, "other-pack@1",
            ["records/work-order|records.reference.dependency_undeclared|/fields/0/reference",
             "records/work-order|records.reference.closure_missing|/fields/0/reference/pin"]
        },
        {
            "digest changed", GenuinePin.Replace(MeterDigest, "sha256:" + new string('0', 64), StringComparison.Ordinal), "eam-core@1",
            ["records/work-order|records.reference.closure_missing|/fields/0/reference/pin"]
        },
        {
            "target changed", GenuinePin.Replace("\"definition_id\":\"eam.meter\"", "\"definition_id\":\"eam.pump\"", StringComparison.Ordinal), "eam-core@1",
            ["records/work-order|records.reference.pin_stale|/fields/0/reference"]
        },
    };

    [Theory(DisplayName = "goal 3 T-DIG(now): a changed pin or requirement refuses with every finding reported together")]
    [MemberData(nameof(ChangedPins))]
    public async Task A_changed_pin_refuses(string _, string pin, string requires, string[] expected)
    {
        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", WorkOrder(pin, requires)), Seed("records/route", Route)], NoPins, NoProducers, Records().AdmitInstallAsync);

        Assert.Equal(expected, Triples(result));
    }

    [Fact(DisplayName = "goal 3 T-DIG(after): a pin to the digest of the transport bytes refuses closure_changed; no digest is rebuilt from the seed")]
    public async Task A_producer_digest_of_the_transport_bytes_refuses_closure_changed()
    {
        var transport = GenuinePin.Replace(MeterDigest, MeterTransportDigest, StringComparison.Ordinal);
        // eam-core 1.0.0 signs the stored-body digest; its seed carries the LF-less transport bytes.
        var producer = Producer("eam-core", "1.0.0", [Seed("records/meter", Meter, "1.0.0")], ["records/meter"],
            [new PackExposedDefinition("records/meter", "1.0.0", MeterDigest)]);

        // The producer's body also ships beside the pin, so a digest re-derived from either copy would equal this pin.
        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", WorkOrder(transport)), Seed("records/route", Route), Seed("records/meter", Meter, "1.0.0")],
            [new PackDependencyRef("eam-core", "1.0.0")], [producer], Records().AdmitInstallAsync);

        Assert.Equal(["records/work-order|records.reference.closure_changed|/fields/0/reference/pin"], Triples(result));
    }

    [Fact(DisplayName = "goal 3 T-CLASS: no Class definition or exposure is ever produced; a Class target never resolves through the adapter")]
    public async Task No_class_definition_or_exposure_is_ever_produced()
    {
        // Every other transport kind carrying a Records-shaped body, and a Class-shaped body under the Records kind.
        var seeds = Enum.GetValues<PackContentKind>()
            .Where(kind => kind != PackContentKind.AssetTypeDefinition)
            .Select(kind => Seed($"other/{kind}",
                Route.Replace("\"fleet-ops.route\"", "\"fleet-ops.other\"", StringComparison.Ordinal), kind: kind))
            .Append(Seed("classes/assets", AssetsClass))
            .Append(Seed("records/route", Route))
            .ToArray();

        var closure = RecordsInstallClosureGate.ReadClosure(seeds, NoPins, NoProducers);

        Assert.Equal([new InstallDefinitionTarget(DefinitionKind.Records, "fleet-ops.route")], closure.PackDefinitions);
        Assert.Empty(closure.Exposures);

        // The pack ships a Class-shaped fleet-ops.assets, yet the unpinned Class edge is unresolved and the pinned one is
        // held: the adapter never declares a Classes definition or exposure (T-724 ruling 51).
        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/inspection", Inspection), Seed("classes/assets", AssetsClass)], NoPins, NoProducers, Records().AdmitInstallAsync);

        Assert.Equal(
            ["records/inspection|records.reference.target_unresolved|/fields/0/reference/target_class_id",
             "records/inspection|records.reference.closure_missing|/fields/1/reference/pin"],
            Triples(result));
    }

    [Fact(DisplayName = "goal 3 T-NOCAT: the adapter reads no catalogue, registry or installed state; it reaches Records only through the bound gate")]
    public void The_gate_reads_no_catalogue()
    {
        const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic;
        var calls = TypeAndNested(typeof(RecordsInstallClosureGate))
            .SelectMany(type => type.GetMethods(declared).Cast<MethodBase>().Concat(type.GetConstructors(declared)))
            .SelectMany(AuthorizationGateArchTests.CalledMethods)
            .ToArray();

        // Not vacuous: the scan sees the adapter's call into the bound gate.
        Assert.Contains(calls, method => method.DeclaringType == typeof(RecordsInstallClosureGate.AdmitInstall)
            && method.Name == "Invoke");
        var reads = calls
            .Where(method => method.DeclaringType is { } type && ReadsLiveState(type))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(reads.Length == 0, "The install closure is read from the verified pack only:\n" + string.Join("\n", reads));

        static bool ReadsLiveState(Type type) =>
            typeof(IVersionedDefinitionStore).IsAssignableFrom(type)
            || typeof(ISchemaRegistry).IsAssignableFrom(type)
            || typeof(IPackInstallStore).IsAssignableFrom(type)
            || type == typeof(RecordTypeDefinitionStore)
            || type == typeof(RecordTypeDefinitionPackageExporter)
            || type == typeof(InstalledPlatformCatalogue);

        static IEnumerable<Type> TypeAndNested(Type type) =>
            type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(TypeAndNested).Prepend(type);
    }

    [Fact(DisplayName = "goal 3 T-INERT: the own-definition entry version is passed through but never reaches the closure or the verdict")]
    public async Task The_entry_version_never_reaches_the_closure()
    {
        // 1.0.0 is the pin's version (and, on a Released install, the package revision every item carries); 7.3.1 is not.
        var atPinVersion = new RecordingGate();
        var elsewhere = new RecordingGate();

        var first = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", WorkOrder(GenuinePin), "1.0.0"), Seed("records/route", Route, "1.0.0")], NoPins, NoProducers,
            atPinVersion.AdmitAsync);
        var second = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", WorkOrder(GenuinePin), "7.3.1"), Seed("records/route", Route, "7.3.1")], NoPins, NoProducers,
            elsewhere.AdmitAsync);

        // The version is handed to the Platform gate as is (the pass-through the owner ruling is asked to scope) ...
        Assert.Equal(["1.0.0", "1.0.0"], atPinVersion.EntryVersions);
        Assert.Equal(["7.3.1", "7.3.1"], elsewhere.EntryVersions);
        // ... yet the closure each entry is checked against is the same and declares no exposure ...
        Assert.All(atPinVersion.Closures.Concat(elsewhere.Closures), closure =>
        {
            Assert.Equal(
                [new InstallDefinitionTarget(DefinitionKind.Records, "fleet-ops.work-order"),
                 new InstallDefinitionTarget(DefinitionKind.Records, "fleet-ops.route")],
                closure.PackDefinitions);
            Assert.Empty(closure.Exposures);
        });
        // ... and the Platform gate's reports, stage included, do not depend on it.
        Assert.Equal(atPinVersion.Reports, elsewhere.Reports);
        Assert.Equal(["records/work-order|records.reference.closure_missing|/fields/0/reference/pin"], Triples(first));
        Assert.Equal(Triples(first), Triples(second));

        // P3: exposures come only from the exact pinned producer's signed entry (2.4.0 here), whatever version the
        // consumer's own items or the producer's package carry.
        var producer = Producer("eam-core", "1.0.0", [Seed("records/meter", Meter, "1.0.0")], ["records/meter"],
            [new PackExposedDefinition("records/meter", "2.4.0", MeterDigest)]);
        foreach (var version in new[] { "1.0.0", "7.3.1" })
        {
            var closure = RecordsInstallClosureGate.ReadClosure(
                [Seed("records/work-order", WorkOrder(GenuinePin), version), Seed("records/route", Route, version)],
                [new PackDependencyRef("eam-core", "1.0.0")], [producer]);
            Assert.Equal([new PinnedExposure("eam-core", DefinitionKind.Records, "eam.meter", "2.4.0", MeterDigest, 1)],
                closure.Exposures);
        }
    }

    [Fact(DisplayName = "goal 3 T-LEGACY: legacy asset types pass through without reaching the Records gate")]
    public async Task Legacy_asset_types_are_not_gated()
    {
        var admit = new RecordingGate();

        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("legacy/route", LegacyAssetType), Seed("legacy/broken", "[1]")], NoPins, NoProducers, admit.AdmitAsync);

        Assert.True(result.IsAdmissible);
        Assert.Empty(admit.Tenants);
    }

    [Fact(DisplayName = "goal 3 G-DISC: a Records-shaped body with a field reference and no record_type_id is gated and refused, never skipped")]
    public async Task A_records_shaped_body_without_a_record_type_id_is_gated_and_refused()
    {
        // Owner decision P10 (G-DISC, fail closed). The Platform gate's own refusal for the absent id is
        // records.identity.record_type_id_required at /record_type_id (RecordsIntentValidator at the pin 2c7bef29), which the
        // API does not publish, so it refuses under the declared fallback with the Platform code in the message.
        var unidentified = Dispatch.Replace(",\"record_type_id\":\"fleet-ops.dispatch\"", "", StringComparison.Ordinal);
        Assert.DoesNotContain("record_type_id", unidentified, StringComparison.Ordinal);
        PackSeedItem[] seeds = [Seed("records/dispatch", unidentified), Seed("records/route", Route)];
        var admit = new RecordingGate();

        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant, seeds, NoPins, NoProducers, admit.AdmitAsync);

        Assert.Equal(["records/dispatch", "records/route"], RecordsInstallClosureGate.GatedItems(seeds).Select(item => item.Key));
        Assert.Equal(["", "fleet-ops.route"], admit.EntryIds);
        Assert.Equal(["records/dispatch|pack.install.refused.records_unpublished_refusal|/record_type_id"], Triples(result));
        Assert.Contains("(records.identity.record_type_id_required)", Assert.Single(result.Refusals).Message, StringComparison.Ordinal);
        // Without an id the body is never a pack definition the closure offers to other edges.
        Assert.All(admit.Closures, closure => Assert.Equal(
            [new InstallDefinitionTarget(DefinitionKind.Records, "fleet-ops.route")], closure.PackDefinitions));
    }

    [Fact(DisplayName = "goal 3 G-DISC/T-LEGACY: a legacy asset type, verbatim from the general pack, and fields without references stay ungated")]
    public async Task A_legacy_asset_type_without_references_is_still_not_gated()
    {
        // general.vehicle exactly as _shared/packs/general/general-pack.export.json carries it, compact.
        const string Vehicle =
            """{"id":"general.vehicle","displayName":"Vehicle","traits":["Movable","Maintainable"],"expectedUsefulLifeYears":8,"conditionScaleMax":5}""";
        PackSeedItem[] seeds =
        [
            Seed("general.vehicle", Vehicle),
            Seed("legacy/fields", """{"displayName":"Route","fields":[{"name":"plate"},"reference",{"references":[]}]}"""),
        ];
        var admit = new RecordingGate();

        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant, seeds, NoPins, NoProducers, admit.AdmitAsync);

        Assert.Empty(RecordsInstallClosureGate.GatedItems(seeds));
        Assert.True(result.IsAdmissible);
        Assert.Empty(admit.Tenants);
    }

    [Fact(DisplayName = "goal 3: a body that claims the Records grammar with a malformed id is refused, not skipped")]
    public async Task A_malformed_record_type_id_is_refused()
    {
        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/route", Route.Replace("\"record_type_id\":\"fleet-ops.route\"", "\"record_type_id\":7", StringComparison.Ordinal))], NoPins, NoProducers,
            Records().AdmitInstallAsync);

        Assert.Equal(["records/route|records.document_invalid|"], Triples(result));
    }

    [Fact(DisplayName = "goal 3 G-CODES: a Platform code the API does not publish still refuses, under the declared fail-closed fallback")]
    public async Task An_unpublished_platform_code_refuses_under_the_fail_closed_fallback()
    {
        // A duplicated field key is a Records validator refusal the API does not publish.
        var duplicated = Route.Replace(
            "[{\"display_name\":\"Name\",\"field_key\":\"name\"}]",
            "[{\"display_name\":\"Name\",\"field_key\":\"name\"},{\"display_name\":\"Name\",\"field_key\":\"name\"}]",
            StringComparison.Ordinal);

        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant, [Seed("records/route", duplicated)], NoPins, NoProducers,
            Records().AdmitInstallAsync);

        Assert.False(result.IsAdmissible);
        Assert.All(result.Refusals, refusal => Assert.Equal("pack.install.refused.records_unpublished_refusal", refusal.Code));
        Assert.Contains(result.Refusals, refusal => refusal.Pointer == "/fields/1/field_key"
            && refusal.Message.Contains("(records.identity.duplicate_field_key)", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "goal 3 G-CODES: the published Records install codes are a subset of the Platform install vocabulary; the creation-role codes fall back (O-7 pending)")]
    public void Published_codes_are_a_subset_of_the_platform_install_vocabulary()
    {
        // Literals from the Platform contract at 2c7bef29 (Structural/CheckEnvelope/DefinitionContractWindow and
        // InstallEdges), all still emitted at c3df8c33. They are NOT the whole install vocabulary at c3df8c33: the three
        // records.creation_role.* codes below are emitted there too and stay unpublished until the owner rules on O-7.
        string[] published =
        [
            "records.document_invalid", "records.envelope_required", "definition.contract.missing",
            "definition.contract.out_of_window", "records.identity.section_invalid", "records.identity.name_required",
            "records.identity.record_type_id_immutable", "records.identity.section_mismatch", "records.package.required",
            "records.package.requirement_invalid", "records.package.exposure_invalid", "records.class.required",
            "records.record_class.required", "records.reference.target_unresolved", "records.reference.pin_unexpected",
            "records.reference.pin_stale", "records.reference.dependency_undeclared",
            "records.reference.interface_incompatible", "records.reference.closure_missing",
            "records.reference.closure_changed",
        ];

        Assert.Equal(published, published.Select(PackRecordsInstallCodes.PublishedCode));
        Assert.Equal("pack.install.refused.records_unpublished_refusal", PackRecordsInstallCodes.PublishedCode("records.identity.duplicate_field_key"));
        Assert.Equal("pack.install.refused.records_unpublished_refusal", PackRecordsInstallCodes.PublishedCode(null));
        // Today's fail-closed behaviour for the Platform PR 279 install codes (O-7, pending owner decision).
        Assert.All(
            new[] { "records.creation_role.required", "records.creation_role.unexpected", "records.creation_role.unknown" },
            code => Assert.Equal("pack.install.refused.records_unpublished_refusal", PackRecordsInstallCodes.PublishedCode(code)));
        var declared = typeof(PackRecordsInstallCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            published.Append("pack.install.refused.records_unpublished_refusal").Append("pack.install.refused.records_gate_not_evaluated")
                .Order(StringComparer.Ordinal),
            declared);
    }

    [Theory(DisplayName = "goal 3 G-CODES: a creation-role refusal at the current pin refuses under the unpublished fallback (O-7 pending)")]
    [InlineData("a role gate whose role this host does not define", "records.creation_role.unknown")]
    [InlineData("a role gate without a creation role", "records.creation_role.required")]
    [InlineData("a creation role without a role gate", "records.creation_role.unexpected")]
    public async Task A_creation_role_refusal_refuses_under_the_unpublished_fallback(string shape, string platformCode)
    {
        // Platform PR 279 (D1, records-ck-22) at c3df8c33. The gate is composed without a role vocabulary, as Records()
        // is, so even a named role is undefined at install (F5). O-7 decides whether the API publishes these codes.
        var gated = shape switch
        {
            "a role gate whose role this host does not define" => "\"creation_gate\":\"role\",\"creation_role_id\":\"dispatcher\",",
            "a role gate without a creation role" => "\"creation_gate\":\"role\",",
            "a creation role without a role gate" => "\"creation_role_id\":\"dispatcher\",",
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };
        var body = Route.Replace("{\"class_id\":", "{" + gated + "\"class_id\":", StringComparison.Ordinal);

        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant, [Seed("records/route", body)], NoPins, NoProducers,
            Records().AdmitInstallAsync);

        Assert.Equal(["records/route|pack.install.refused.records_unpublished_refusal|/creation_role_id"], Triples(result));
        Assert.Contains($"({platformCode})", Assert.Single(result.Refusals).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "goal 3 P3 T-POS-X: a cross-package pin admits when the exact pinned producer signs its version and digest")]
    public async Task A_cross_package_pin_admits_when_the_exact_pinned_producer_declares_its_version_and_digest()
    {
        var admit = new RecordingGate();

        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", AssetWorkOrder(AssetClassVersion, AssetClassDigest))],
            [new PackDependencyRef("eam-core", ProducerRevision)], [AssetClassProducer()], admit.AdmitAsync);

        Assert.True(result.IsAdmissible, Describe(result));
        var closure = Assert.Single(admit.Closures);
        Assert.Equal([AssetClassExposure], closure.Exposures);
        Assert.Equal([new InstallDefinitionTarget(DefinitionKind.Records, "fleet-ops.work-order")], closure.PackDefinitions);
    }

    [Fact(DisplayName = "goal 3 P3 T-MISS-V: a producer that exposes the key but signs no exposed definitions refuses closure_missing")]
    public async Task A_producer_without_signed_exposed_definitions_refuses_closure_missing()
    {
        var admit = new RecordingGate();
        // The exact pinned producer ships and exposes the asset class at interface 1 but carries no signed entry.
        var unbound = AssetClassProducer() with { ExposedDefinitions = null };

        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", AssetWorkOrder(AssetClassVersion, AssetClassDigest))],
            [new PackDependencyRef("eam-core", ProducerRevision)], [unbound], admit.AdmitAsync);

        Assert.Equal(["records/work-order|records.reference.closure_missing|/fields/0/reference/pin"], Triples(result));
        Assert.Empty(Assert.Single(admit.Closures).Exposures);
    }

    [Fact(DisplayName = "goal 3 P3 T-MISS-V: an installed pack persisted before P3 decodes without exposed definitions and offers no exposure")]
    public async Task A_pre_P3_installed_payload_decodes_without_exposed_definitions()
    {
        // The durable store keeps an installed pack as its JSON payload under the general serializer defaults. A row
        // written before P3 has no ExposedDefinitions member at all.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General);
        var current = JsonNode.Parse(JsonSerializer.Serialize(AssetClassProducer() with { ExposedDefinitions = null }, options))!.AsObject();
        Assert.True(current.ContainsKey("ExposedDefinitions"));
        Assert.True(current.Remove("ExposedDefinitions"));
        var prior = JsonSerializer.Deserialize<InstalledPack>(current.ToJsonString(), options)!;

        Assert.Null(prior.ExposedDefinitions);
        Assert.Equal(["records/eam.asset-class"], prior.Exposes);
        Assert.Equal(1, prior.InterfaceVersion);
        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", AssetWorkOrder(AssetClassVersion, AssetClassDigest))],
            [new PackDependencyRef("eam-core", ProducerRevision)], [prior], Records().AdmitInstallAsync);
        Assert.Equal(["records/work-order|records.reference.closure_missing|/fields/0/reference/pin"], Triples(result));
    }

    [Fact(DisplayName = "goal 3 P3 T-FLOOR: only a newer producer version installed refuses closure_missing; the latest installed is never read")]
    public async Task Only_a_newer_producer_version_installed_refuses_closure_missing()
    {
        // 7.1.0 signs exactly the facts the pin carries, but the consumer pins 7.0.0, which is not installed.
        var newer = AssetClassProducer() with { Version = "7.1.0" };

        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", AssetWorkOrder(AssetClassVersion, AssetClassDigest))],
            [new PackDependencyRef("eam-core", ProducerRevision)], [newer], Records().AdmitInstallAsync);

        Assert.Equal(["records/work-order|records.reference.closure_missing|/fields/0/reference/pin"], Triples(result));
        // Beside the exact version, a newer one with other facts changes nothing.
        var beside = RecordsInstallClosureGate.ReadClosure([], [new PackDependencyRef("eam-core", ProducerRevision)],
            [newer with { ExposedDefinitions = [AssetClassEntry with { Version = "9.9.9" }] }, AssetClassProducer()]);
        Assert.Equal([AssetClassExposure], beside.Exposures);
    }

    [Fact(DisplayName = "goal 3 P3 T-REV: a pin at the producer's package revision refuses closure_changed; the entry version is the catalogue's")]
    public async Task A_pin_at_the_package_revision_refuses_closure_changed()
    {
        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", AssetWorkOrder(ProducerRevision, AssetClassDigest))],
            [new PackDependencyRef("eam-core", ProducerRevision)], [AssetClassProducer()], Records().AdmitInstallAsync);

        Assert.Equal(["records/work-order|records.reference.closure_changed|/fields/0/reference/pin"], Triples(result));
    }

    [Fact(DisplayName = "goal 3 P3 T-DIG(after): a pin at the LF-less digest of the exposed body refuses closure_changed")]
    public async Task A_pin_at_the_transport_digest_of_the_exposed_body_refuses_closure_changed()
    {
        var result = await RecordsInstallClosureGate.AdmitAsync(InstallingTenant,
            [Seed("records/work-order", AssetWorkOrder(AssetClassVersion, AssetClassTransportDigest))],
            [new PackDependencyRef("eam-core", ProducerRevision)], [AssetClassProducer()], Records().AdmitInstallAsync);

        Assert.Equal(["records/work-order|records.reference.closure_changed|/fields/0/reference/pin"], Triples(result));
    }

    [Theory(DisplayName = "goal 3 P3 (O-5, provisional): the exact pinned producer declares its exposure in any installed lifecycle")]
    [InlineData(PackLifecycleState.Draft)]
    [InlineData(PackLifecycleState.Active)]
    [InlineData(PackLifecycleState.Superseded)]
    [InlineData(PackLifecycleState.Inactive)]
    public void The_exact_pinned_producer_declares_its_exposure_in_any_lifecycle(PackLifecycleState lifecycle)
    {
        // Open question O-5, implemented as the plan recommends: the signed declaration is immutable, so the lifecycle
        // of the exact pinned version does not change what it declares.
        var closure = RecordsInstallClosureGate.ReadClosure([], [new PackDependencyRef("eam-core", ProducerRevision)],
            [AssetClassProducer() with { Lifecycle = lifecycle }]);

        Assert.Equal([AssetClassExposure], closure.Exposures);
    }

    [Fact(DisplayName = "goal 3 P3 T-CLASS: a signed entry never exposes a Class, an unexposed key, another kind or an ambiguous declaration")]
    public void A_signed_entry_never_exposes_a_class_an_unexposed_key_or_an_ambiguous_declaration()
    {
        PackDependencyRef[] pinned = [new("eam-core", ProducerRevision)];
        var producer = AssetClassProducer();
        PackExposedDefinition Entry(string key) => new(key, AssetClassVersion, AssetClassDigest);
        var cases = new (string Case, InstalledPack Producer, PackDependencyRef[] Pins)[]
        {
            // A Class-shaped body under the Records kind names no record_type_id: no Class is ever exposed.
            ("class", Producer("eam-core", ProducerRevision, [Seed("classes/equipment", AssetsClass)], ["classes/equipment"],
                [Entry("classes/equipment")]), pinned),
            ("unexposed key", producer with { Exposes = ["records/other"] }, pinned),
            ("no interface version", producer with { InterfaceVersion = null }, pinned),
            ("other kind", Producer("eam-core", ProducerRevision,
                [Seed("records/eam.asset-class", ExposedAssetClassJson, kind: PackContentKind.FormDefinition)],
                ["records/eam.asset-class"], [AssetClassEntry]), pinned),
            ("no seed item", Producer("eam-core", ProducerRevision, [], ["records/eam.asset-class"], [AssetClassEntry]), pinned),
            // A seed whose record_type_id is present but not a string is no Record Type id: nothing is exposed.
            ("non-string id", Producer("eam-core", ProducerRevision,
                [Seed("records/eam.asset-class", ExposedAssetClassJson.Replace("\"record_type_id\":\"eam.asset-class\"",
                    "\"record_type_id\":7", StringComparison.Ordinal))],
                ["records/eam.asset-class"], [AssetClassEntry]), pinned),
            // Two entries under one key: the repeated-entry rule alone refuses it, whatever their versions.
            ("repeated entry", producer with { ExposedDefinitions = [AssetClassEntry, AssetClassEntry with { Version = "3.1.5" }] }, pinned),
            ("two keys, one id", Producer("eam-core", ProducerRevision,
                [Seed("records/eam.asset-class", ExposedAssetClassJson), Seed("records/eam.asset-class-copy", ExposedAssetClassJson)],
                ["records/eam.asset-class", "records/eam.asset-class-copy"], [AssetClassEntry, Entry("records/eam.asset-class-copy")]), pinned),
            ("pinned twice", producer, new PackDependencyRef[] { pinned[0], new("eam-core", "7.1.0") }),
        };

        foreach (var (name, candidate, pins) in cases)
        {
            var closure = RecordsInstallClosureGate.ReadClosure([], pins, [candidate]);
            Assert.True(closure.Exposures.Count == 0, name);
        }
        // The control: the unaltered producer declares its exposure.
        Assert.Equal([AssetClassExposure], RecordsInstallClosureGate.ReadClosure([], pinned, [producer]).Exposures);
    }

    private static PackSeedItem Seed(string key, string body, string version = "1.0.0",
        PackContentKind kind = PackContentKind.AssetTypeDefinition) =>
        new(key, kind, version, body, Cid.FromBytes(Encoding.UTF8.GetBytes(key + version + body)));

    // An installed producer as the durable store returns it after a signed install: its signed exposure and entries.
    internal static InstalledPack Producer(string key, string version, PackSeedItem[] seeds, string[] exposes,
        PackExposedDefinition[]? exposedDefinitions, int? interfaceVersion = 1) =>
        new(key, version, PackScopeTier.Horizontal, PackLifecycleState.Active, seeds, new Dictionary<string, int>(),
            new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero), PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, [], Exposes: exposes, InterfaceVersion: interfaceVersion, ExposedDefinitions: exposedDefinitions);

    // eam-core at package revision 7.0.0, exposing its asset class bound to catalogue version 3.1.4 (Goal 3 P1).
    internal static InstalledPack AssetClassProducer() =>
        Producer("eam-core", ProducerRevision, [Seed("records/eam.asset-class", ExposedAssetClassJson, ProducerRevision)],
            ["records/eam.asset-class"], [AssetClassEntry]);

    // content key|Records code|body pointer, so theory rows stay serializable.
    private static string[] Triples(PackAdmissionResult result) =>
        result.Refusals.Select(refusal => $"{refusal.ContentKey}|{refusal.Code}|{refusal.Pointer}").ToArray();

    private static string Describe(PackAdmissionResult result) =>
        string.Join("; ", result.Refusals.Select(refusal => $"{refusal.ContentKey} {refusal.Code} {refusal.Pointer}"));

    // The landed Platform gate over its public composition; install admission reads neither the catalogue nor the registry.
    internal static RecordTypeDefinitionStore Records()
    {
        var kinds = new FieldKindRuntime(new FieldKindRegistry([new AdmittedFieldKind("text", "1.0.0", null)]));
        var validator = new RecordsIntentValidator();
        var window = new DefinitionContractWindow(1, 0, 1);
        var catalogue = new InMemoryVersionedDefinitionStore(new Dictionary<DefinitionKind, DefinitionAdmission>
        {
            [DefinitionKind.Records] = RecordTypeDefinitionStore.Admission(window, validator),
        });
        return new RecordTypeDefinitionStore(catalogue, new RecordTypeSchemaCompiler(validator, kinds, new SharedValueDomainAdmission()),
            new RecordFieldDefaults(kinds), new InMemorySchemaRegistry(fieldKindRuntime: kinds), window);
    }

    // Records what the adapter hands the real gate, then delegates to it.
    private sealed class RecordingGate
    {
        private readonly RecordTypeDefinitionStore _records = Records();

        public List<string> Tenants { get; } = [];
        public List<string> EntryIds { get; } = [];
        public List<string> EntryVersions { get; } = [];
        public List<RecordsInstallClosure> Closures { get; } = [];
        public List<string> Reports { get; } = [];

        public async ValueTask<DefinitionRefusalReport> AdmitAsync(string tenant, RecordTypeDefinitionPackageEntry entry,
            RecordsInstallClosure closure, CancellationToken cancellationToken)
        {
            Tenants.Add(tenant);
            EntryIds.Add(entry.DefinitionId);
            EntryVersions.Add(entry.Version);
            Closures.Add(closure);
            var report = await _records.AdmitInstallAsync(tenant, entry, closure, cancellationToken);
            Reports.Add($"{entry.DefinitionId} {report.Stage}: " + string.Join(", ",
                report.Refusals.Select(refusal => $"{refusal.Code} {refusal.Pointer}")));
            return report;
        }
    }
}
