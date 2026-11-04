using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class RecordsTypeReferenceHostedSnapshotTests
{
    [Fact(DisplayName = "T-615 Q7: the production hosted endpoint obtains the snapshot through outer-container injection")]
    public async Task Hosted_endpoint_injects_the_snapshot_into_validate_and_sign()
    {
        var store = new InMemoryPackInstallStore();
        var team = new TeamId(Guid.Parse("61500000-0000-0000-0000-000000000615"));
        var active = Substitute.For<IActiveTeamAccessor>();
        using var teamServices = new ServiceCollection().BuildServiceProvider();
        active.Active.Returns(new TeamContext(team, "Records snapshot", teamServices, TimeProvider.System));
        var tenant = NodeTenant.Resolve(active);
        store.Commit(new PackInstallTransaction(tenant, RecordsInstallClosureGateTests.AssetClassProducer(),
            new PackInstallWatermark("eam-core", "7.0.0", new Dictionary<string, int>()), []));
        var codec = new PackFileCodec();
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, TimeProvider.System);
        using var signer = new NodePrincipalSigner(RandomNumberGenerator.GetBytes(32));
        var bearer = Guid.NewGuid().ToString("N");
        using var services = new ServiceCollection().AddTestKernelClock().AddLogging().AddTestDesktopOperator()
            .AddSingleton<IActiveTeamAccessor>(active).AddSingleton<IPackInstallStore>(store)
            .AddSingleton<IPackExporter>(exporter).AddSingleton<IPackVerifier>(new PackVerifier(new Ed25519Verifier(), codec))
            .AddSingleton(signer).AddSingleton<AuthorizationGate>(TestPackGate.AllowAll())
            .AddSingleton(new NodeCallerSessionToken(bearer)).BuildServiceProvider();
        await using var app = new SharedHostedWebApp(services, Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(), services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            services.GetRequiredService<TimeProvider>());
        var endpoint = ActivatorUtilities.CreateInstance<HostedPackComposerApiEndpoint>(services, app);
        await endpoint.StartAsync(CancellationToken.None);
        await app.StartAsync(CancellationToken.None);
        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var body = JsonNode.Parse(RecordsTypeReferenceExtractionTests.Body())!;
        body["envelope"]!["tenant"] = "61500000-0000-0000-0000-000000000615";
        var request = new
        {
            key = "consumer", version = "1.0.0", name = "Consumer", scopeTier = "Horizontal",
            contents = new[] { new { key = "consumer.work", kind = "AssetTypeDefinition", version = "1.0.0",
                content = body } },
            dependencies = new[] { new { key = "eam-core", version = "7.0.0" } },
        };
        using var validation = await client.PostAsJsonAsync(PackComposerRoutes.ExportRoute + "?validateOnly=true", request);
        Assert.Equal(HttpStatusCode.OK, validation.StatusCode);
        using var exported = await client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, request);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        var file = codec.TryDecode(await exported.Content.ReadAsByteArrayAsync());
        var edge = Assert.Single(file!.Envelope!.Payload.Manifest.ContentReferences!);
        Assert.Equal(("eam-core", "records/eam.asset-class", "References"), (edge.ToPackKey, edge.ToContentKey, edge.Relation.ToString()));
    }
}
