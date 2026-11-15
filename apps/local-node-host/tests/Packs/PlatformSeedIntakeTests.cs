using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Blocks.BuilderDefinitions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// The test-built platform release bundle (ADR-0144): the platform's own 2.0.0 export at the pin, a
/// SHA256SUMS computed here, and a test-only release receipt in the release workflow's shape. The
/// referent is recorded only by the tests that need one; no node outside these tests trusts it.
/// </summary>
internal static class PlatformSeedTestBundle
{
    // The platform commit the pin names (eng/platform-pin.json); the receipt is test-only.
    internal const string Commit = "8149a57920a94eb478642115ded4f357b2dc1107";

    internal static byte[] Export() => PlatformPackageSeed.LoadCheckedInExport();

    /// <summary>The bundle and referent a test host records as published, for tests that need a seeded node.</summary>
    internal static PlatformSeedBundle Published { get; } = Build();

    internal static PlatformSeedReferent PublishedReferent { get; } = ReferentFor(Published);

    /// <summary>The platform export as the node converts it.</summary>
    internal static Harborline.Api.Foundation.Packs.Export.PackExportRequest Request(string authoringPrincipal) =>
        PlatformPackPreloadHostedService.ReadExportRequest(Export(), authoringPrincipal);

    internal static PlatformSeedBundle Build() => Build(Export());

    internal static PlatformSeedBundle Build(byte[] export)
    {
        var receipt = Receipt(export);
        return new PlatformSeedBundle(export, Sums(export, receipt), receipt);
    }

    internal static byte[] Sums(byte[] export, byte[] receipt) => Encoding.UTF8.GetBytes(
        $"{Sha256(export)}  platform-pack.export.json\n{Sha256(receipt)}  release-receipt.json\n");

    /// <summary>The field order of the platform's release-receipt.mjs exportReceipt, marked test-only.</summary>
    internal static byte[] Receipt(byte[] export)
    {
        using var document = JsonDocument.Parse(export);
        var root = document.RootElement;
        var body = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["repository"] = "harborline-platform",
            ["commit"] = Commit,
            ["tree"] = new string('0', 40),
            ["artifact"] = new JsonObject
            {
                ["path"] = "_shared/packs/platform/platform-pack.export.json",
                ["digest"] = Sha256(export),
            },
            ["pack"] = new JsonObject
            {
                ["version"] = root.GetProperty("revision").GetString(),
                ["seedDigest"] = root.GetProperty("digest").GetProperty("value").GetString(),
            },
            ["evidence"] = new JsonObject
            {
                ["path"] = "test-only",
                ["digest"] = new string('0', 64),
                ["run"] = new JsonObject { ["baseHead"] = Commit, ["testedTree"] = new string('0', 40) },
            },
            ["transcript"] = new JsonObject
            {
                ["baseHead"] = Commit, ["testedTree"] = new string('0', 40), ["mode"] = "test-only",
            },
        };
        var unsigned = body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        body["digest"] = new JsonObject { ["algorithm"] = "sha256", ["value"] = Sha256(Encoding.UTF8.GetBytes(unsigned)) };
        return Encoding.UTF8.GetBytes(body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>The referent a test records for exactly this bundle.</summary>
    internal static PlatformSeedReferent ReferentFor(PlatformSeedBundle bundle) =>
        new("2.0.0", Sha256(bundle.Export!), Sha256(bundle.ReleaseReceipt!));

    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public sealed class PlatformSeedIntakeTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000670");

    [Fact]
    public void The_test_built_bundle_verifies_against_its_recorded_referent_and_names_its_identity()
    {
        var bundle = PlatformSeedTestBundle.Build();
        var exportSha256 = PlatformSeedTestBundle.Sha256(bundle.Export!);
        var receiptSha256 = PlatformSeedTestBundle.Sha256(bundle.ReleaseReceipt!);

        var seed = PlatformSeedIntake.Verify(bundle, PlatformSeedTestBundle.ReferentFor(bundle));

        Assert.Equal("2.0.0", seed.Revision);
        Assert.Equal(exportSha256, seed.ExportSha256);
        Assert.Equal($"{exportSha256}  platform-pack.export.json", seed.Sha256SumsLine);
        Assert.Equal($"harborline-platform@{PlatformSeedTestBundle.Commit} release-receipt.json sha256:{receiptSha256}",
            seed.ReleaseReceipt);
        Assert.Equal(bundle.Export, seed.Export);
    }

    [Fact]
    public void The_export_at_the_pin_is_the_platform_seed_2_0_0()
    {
        using var document = JsonDocument.Parse(PlatformSeedTestBundle.Export());
        Assert.Equal("harborline.platform", document.RootElement.GetProperty("packageKey").GetString());
        Assert.Equal("2.0.0", document.RootElement.GetProperty("revision").GetString());
    }

    public static TheoryData<string, string> Corruptions => new()
    {
        { "export-missing", "platform-seed-export-missing" },
        { "sums-missing", "platform-seed-sums-missing" },
        { "receipt-missing", "platform-seed-receipt-missing" },
        { "export-differs-from-sums", "platform-seed-export-sums-mismatch" },
        { "declared-digest-differs", "platform-seed-digest-mismatch" },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void A_corrupted_copy_of_the_test_bundle_is_refused_with_a_named_code(string corruption, string code)
    {
        var (bundle, referent) = Corrupt(corruption);

        var refused = Assert.Throws<PlatformSeedRefusedException>(() => PlatformSeedIntake.Verify(bundle, referent));

        Assert.Equal(code, refused.Code);
    }

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task A_corrupted_bundle_is_refused_before_any_item_is_converted_signed_or_installed(string corruption, string code)
    {
        var (bundle, referent) = Corrupt(corruption);
        var store = new InMemoryPackInstallStore();
        var (preload, calls) = Preload(store, bundle, referent);

        var refused = await Assert.ThrowsAsync<PlatformSeedRefusedException>(
            () => preload.PreloadAsync(Tenant, CancellationToken.None));

        Assert.Equal(code, refused.Code);
        Assert.Empty(calls);
        Assert.Empty(store.ListInstalled(Tenant));
    }

    [Fact]
    public async Task An_unpublished_stage_bundle_with_no_recorded_referent_is_not_signed_or_installed()
    {
        // The release workflow's staged bundle is complete and self-consistent; it is still unpublished,
        // so no referent is recorded for it (ADR-0102 decision 4).
        var store = new InMemoryPackInstallStore();
        var (preload, calls) = Preload(store, PlatformSeedTestBundle.Build(), referent: null);

        var refused = await Assert.ThrowsAsync<PlatformSeedRefusedException>(
            () => preload.PreloadAsync(Tenant, CancellationToken.None));

        Assert.Equal("platform-seed-referent-absent", refused.Code);
        Assert.Empty(calls);
        Assert.Empty(store.ListInstalled(Tenant));
    }

    [Fact]
    public async Task A_node_with_no_bundle_and_no_referent_installs_no_seed()
    {
        var store = new InMemoryPackInstallStore();
        var (preload, calls) = Preload(store, bundle: null, referent: null);

        var refused = await Assert.ThrowsAsync<PlatformSeedRefusedException>(
            () => preload.PreloadAsync(Tenant, CancellationToken.None));

        Assert.Equal("platform-seed-export-missing", refused.Code);
        Assert.Empty(calls);
        Assert.Empty(store.ListInstalled(Tenant));
    }

    [Fact]
    public void A_bundle_other_than_the_recorded_referent_is_refused()
    {
        var bundle = PlatformSeedTestBundle.Build();
        var other = new PlatformSeedReferent("2.0.0", new string('a', 64), PlatformSeedTestBundle.Sha256(bundle.ReleaseReceipt!));

        var refused = Assert.Throws<PlatformSeedRefusedException>(() => PlatformSeedIntake.Verify(bundle, other));

        Assert.Equal("platform-seed-referent-mismatch", refused.Code);
    }

    [Fact]
    public void A_receipt_that_names_another_export_is_refused()
    {
        var bundle = PlatformSeedTestBundle.Build();
        var receipt = JsonNode.Parse(bundle.ReleaseReceipt!)!;
        receipt["artifact"]!["digest"] = new string('b', 64);
        var forged = Encoding.UTF8.GetBytes(receipt.ToJsonString());
        var copy = new PlatformSeedBundle(bundle.Export, PlatformSeedTestBundle.Sums(bundle.Export!, forged), forged);

        var refused = Assert.Throws<PlatformSeedRefusedException>(
            () => PlatformSeedIntake.Verify(copy, PlatformSeedTestBundle.ReferentFor(copy)));

        Assert.Equal("platform-seed-receipt-mismatch", refused.Code);
    }

    [Theory]
    [InlineData("platform.detail.form")]
    [InlineData("platform.pack.author")]
    public void Released_form_text_is_validated_on_each_form_as_it_arrives_from_the_platform_export(string formKey)
    {
        var accepted = PlatformPackPreloadHostedService.ReadExportRequest(PlatformSeedTestBundle.Export(), "fixture-author");
        Assert.Contains(accepted.Contents, item => item.Key == formKey);
        var export = JsonNode.Parse(PlatformSeedTestBundle.Export())!;
        var payload = export["items"]!.AsArray()
            .Single(item => (string?)item!["content"]?["payload"]?["definitionKey"] == formKey)!["content"]!["payload"]!;
        payload["body"]!["overlay"]!["title"] = JsonNode.Parse("{\"unsupported\":\"text\"}");
        var mutated = Encoding.UTF8.GetBytes(export.ToJsonString());

        var refused = Assert.Throws<InvalidDataException>(
            () => PlatformPackPreloadHostedService.ReadExportRequest(mutated, "fixture-author"));

        Assert.Equal("overlay.title must be an InternationalizedTextDto with a populated default locale", refused.Message);
    }

    [Fact]
    public void The_intake_carries_the_package_level_capability_requirements_to_the_installed_pack()
    {
        // seed-released-navigation.md:58: catalogue-field-source-v1 declares the capability once, at package level.
        var request = PlatformSeedTestBundle.Request("fixture-author");

        Assert.Equal(["forms.catalogue-field-source"], request.CapabilityRequirements);
    }

    [Fact]
    public void The_workshop_pointers_resolve_to_the_api_navigation_shape()
    {
        // seed-released-navigation.md:59; the literal is the workshop the api installed at 1.6.1.
        var request = PlatformSeedTestBundle.Request("fixture-author");
        var workshop = request.Contents.Single(item => item.Key == "platform.workshop").Content;

        string[] pillars = ["asset-types", "forms", "workflows", "standards", "defaults", "terminology", "documents",
            "taxonomies", "reports", "data-exchanges", "standing-rules", "schedules", "views"];
        var expected = JsonNode.Parse($$"""
            {"seedWorkspaces":[{"id":"workshop","labelKey":"workshop.workspace","groups":[{"id":"definitions",
            "labelKey":"workshop.definitions","itemIds":{{JsonSerializer.Serialize(pillars)}},
            "items":{{JsonSerializer.Serialize(pillars.Select(id => new { id, labelKey = $"workshop.{id}" }))}}}]}],
            "panelSet":[{"id":"inspector","binding":"panels.inspector.toggle","shortcut":"mod+shift+i",
            "defaultWidth":360,"minimumHeight":180,"defaultOpen":false}]}
            """);
        Assert.True(JsonNode.DeepEquals(expected, workshop), workshop.ToJsonString());
    }

    [Theory]
    [InlineData("\"workspaceItem\":\"platform-package-ck-5\"", "\"workspaceItem\":\"platform-package-ck-99\"")]
    [InlineData("\"entriesItem\":\"platform-package-ck-6\"", "\"entriesItem\":\"platform-package-ck-99\"")]
    [InlineData("\"labelKey\":\"workshop.workspace\"", "\"title\":\"workshop.workspace\"")]
    [InlineData("{\"members\":[{\"id\":\"asset-types\"", "{\"entries\":[{\"id\":\"asset-types\"")]
    public void A_workshop_pointer_that_does_not_resolve_is_refused_not_dropped(string original, string corrupted)
    {
        var text = Encoding.UTF8.GetString(PlatformSeedTestBundle.Export());
        Assert.Equal(1, text.Split(original).Length - 1);

        var refused = Assert.Throws<PlatformSeedRefusedException>(() => PlatformPackPreloadHostedService.ReadExportRequest(
            Encoding.UTF8.GetBytes(text.Replace(original, corrupted, StringComparison.Ordinal)), "fixture-author"));

        Assert.Equal("platform-seed-navigation-unresolved", refused.Code);
    }

    [Fact]
    public void Acceptance_1_each_view_installs_at_its_own_body_version_not_the_seed_revision()
    {
        // DES-0017 views-ck-2/ck-7 and ADR-0102 decision 5: the body carries the view's version; 2.0.0 is the package's.
        // The expected versions are read here from the raw export, independently of conversion and the intake.
        using var export = JsonDocument.Parse(PlatformSeedTestBundle.Export());
        var bodyVersions = export.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("content").GetProperty("payload"))
            .Where(payload => payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("contentKind", out var kind) && kind.GetString() == "ViewDefinition")
            .ToDictionary(payload => payload.GetProperty("definitionKey").GetString()!,
                payload => payload.GetProperty("body").GetProperty("version").GetString()!);

        var views = PlatformSeedTestBundle.Request("fixture-author").Contents
            .Where(item => item.Kind == Harborline.Api.Foundation.Packs.Model.PackContentKind.ViewDefinition)
            .ToDictionary(item => item.Key, item => item.Version);

        Assert.Equal(39, views.Count);
        Assert.Equal(bodyVersions, views);
        Assert.All(views.Values, version => Assert.Equal("1.0.0", version));
    }

    [Theory]
    [InlineData("\"key\":\"platform.list.views\",\"version\":\"1.0.0\"", "\"key\":\"platform.list.views\"")]
    [InlineData("\"key\":\"platform.list.views\",\"version\":\"1.0.0\"", "\"key\":\"platform.list.views\",\"version\":\"\"")]
    [InlineData("\"key\":\"platform.list.views\",\"version\":\"1.0.0\"", "\"key\":\"platform.list.views\",\"version\":1")]
    public void A_view_body_with_no_usable_version_is_refused_not_defaulted(string original, string corrupted)
    {
        var text = Encoding.UTF8.GetString(PlatformSeedTestBundle.Export());
        Assert.Equal(1, text.Split(original).Length - 1);

        var refused = Assert.Throws<PlatformSeedRefusedException>(() => PlatformPackPreloadHostedService.ReadExportRequest(
            Encoding.UTF8.GetBytes(text.Replace(original, corrupted, StringComparison.Ordinal)), "fixture-author"));

        Assert.Equal("platform-seed-view-version-missing", refused.Code);
    }

    private static (PlatformSeedBundle Bundle, PlatformSeedReferent Referent) Corrupt(string corruption)
    {
        var good = PlatformSeedTestBundle.Build();
        var referent = PlatformSeedTestBundle.ReferentFor(good);
        return corruption switch
        {
            "export-missing" => (good with { Export = null }, referent),
            "sums-missing" => (good with { Sha256Sums = null }, referent),
            "receipt-missing" => (good with { ReleaseReceipt = null }, referent),
            // One item's kind is changed, so conversion would refuse it with its own code if it ran first.
            "export-differs-from-sums" => (good with { Export = CorruptItem(good.Export!) }, referent),
            "declared-digest-differs" => SumsRecomputed(CorruptItem(good.Export!), good.ReleaseReceipt!, referent),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
    }

    // The export's SHA256SUMS line matches the corrupted bytes, so only the declared digest disagrees.
    private static (PlatformSeedBundle, PlatformSeedReferent) SumsRecomputed(byte[] export, byte[] receipt, PlatformSeedReferent referent) =>
        (new PlatformSeedBundle(export, PlatformSeedTestBundle.Sums(export, receipt), receipt),
         referent with { ExportSha256 = PlatformSeedTestBundle.Sha256(export) });

    private static byte[] CorruptItem(byte[] export)
    {
        var text = Encoding.UTF8.GetString(export);
        const string kind = "\"contentKind\":\"FormDefinition\"";
        var at = text.IndexOf(kind, StringComparison.Ordinal);
        Assert.True(at > 0, "the export carries a FormDefinition item");
        return Encoding.UTF8.GetBytes(text[..at] + "\"contentKind\":\"NotAKind\"" + text[(at + kind.Length)..]);
    }

    private static (PlatformPackPreloadHostedService Preload, List<string> Calls) Preload(
        InMemoryPackInstallStore store, PlatformSeedBundle? bundle, PlatformSeedReferent? referent)
    {
        var calls = new List<string>();
        var signer = new NodePrincipalSigner(RandomNumberGenerator.GetBytes(32));
        var trust = new InMemoryPackTrustStore(
            [new PackTrustRoot(TrustScope.OwnRoster, signer.Signer.IssuerId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current)]);
        var preload = new PlatformPackPreloadHostedService(
            Recording<IPackExporter>.Create(calls), signer, Recording<IPackInstaller>.Create(calls), store, trust,
            PackRevocationList.Empty, Recording<IActiveTeamAccessor>.Create(calls), TimeProvider.System,
            NullLogger<PlatformPackPreloadHostedService>.Instance, TestDesktopOperator.Identity(), bundle, referent);
        return (preload, calls);
    }

    /// <summary>Records every call into the export, sign and install path, and refuses to perform it.</summary>
    public class Recording<T> : DispatchProxy where T : class
    {
        private List<string> calls = null!;

        internal static T Create(List<string> calls)
        {
            var proxy = DispatchProxy.Create<T, Recording<T>>();
            ((Recording<T>)(object)proxy).calls = calls;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            calls.Add($"{typeof(T).Name}.{targetMethod?.Name}");
            throw new InvalidOperationException($"{typeof(T).Name}.{targetMethod?.Name} was reached.");
        }
    }
}
