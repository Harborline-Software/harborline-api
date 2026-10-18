using System.Text.Json;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Blocks.FinancialPeriods.Services;
using Harborline.Api.Blocks.Reports;
using Harborline.Api.Blocks.Reports.Cartridges.TrialBalance;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Foundation.ReportDefinitions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using NodeOperatorCommand = global::Harborline.Api.NodeOperatorCli.OperatorCli;
using TenantId = Harborline.Api.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.LocalNodeHost.Tests.OperatorCli;

[Collection("Harborline process environment")]
public sealed class ReportCliPackRoundTripTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string ForeignTenant = "33333333-3333-3333-3333-333333333333";
    private const string PackKey = "acme.cli-reports";
    private const string ReportKey = "acme.cli-reports/trial-balance";
    private const string ProvenanceJson =
        """{"derivedAt":"2026-07-15T09:30:00+00:00","exportingActor":"acme-report-admin","originPackKey":"acme.cli-reports","tier":"Civilian"}""";

    [Fact]
    public async Task Signed_report_round_trips_through_cli_verify_install_and_activation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"harborline-report-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddTestDesktopOperator();
            builder.Services.AddTestKernelClock();
            builder.Services.AddInMemoryAssetTypeSystem();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            await using var app = builder.Build();
            using var key = KeyPair.Generate();
            await using var team = new TeamContext(
                new TeamId(Guid.Parse(Tenant)),
                "Report CLI tenant",
                new ServiceCollection().BuildServiceProvider(),
                TimeProvider.System);
            var activeTeam = new FixedActiveTeamAccessor(team);
            var tenant = new TenantId(Tenant);
            var store = new InMemoryPackInstallStore();
            PlatformPackTestPreload.Activate(store, tenant);

            var unused = new UnusedFinancialPorts();
            var cartridges = new ReportCartridgeRegistry();
            cartridges.Register(new TrialBalanceCartridge(unused, unused, unused, unused));
            var reports = new InMemoryReportDefinitionRegistry(new HostReportKindDescriptorRegistry(cartridges));
            var projector = new PackSeedProjector(
                store,
                app.Services.GetRequiredService<IEntityTypeRegistry>(),
                NullLogger<PackSeedProjector>.Instance,
                time: TimeProvider.System,
                reportDefinitions: reports);
            var codec = new PackFileCodec();
            var exporter = new PackExporter(
                new PackContentCanonicalizer(),
                new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                codec,
                timeProvider: TimeProvider.System);
            var verifier = new PackVerifier(new Ed25519Verifier(), codec);
            var trust = new InMemoryPackTrustStore(
                [new PackTrustRoot(TrustScope.OwnRoster, key.PrincipalId, 1, TrustRootStatus.Current)]);
            var installer = new PackInstaller(
                verifier,
                store,
                new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
                new InMemoryPackInstallAudit(),
                TestAuthorization.AllowGate(),
                new PackPlatformCompatibility("2.0.0", PackSeedProjector.RegisteredCases));
            var gate = TestPackGate.AllowAll();

            app.Use(async (http, next) =>
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                await next(http);
            });
            PackComposerRoutes.Map(
                app, exporter, verifier, trust, new Ed25519Signer(key), activeTeam,
                gate, TimeProvider.System, NullLogger.Instance);
            PackInstallRoutes.Map(
                app, installer, store, trust, PackRevocationList.Empty, activeTeam,
                gate, TimeProvider.System, NullLogger.Instance, key.PrincipalId.ToBase64Url(), projector);

            var requestPath = Path.Combine(directory, "report-request.json");
            var packPath = Path.Combine(directory, "report.pack");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(
                new
                {
                    key = "acme.cli-reports",
                    version = "1.0.0",
                    name = "CLI Reports",
                    description = "One pinned Report definition.",
                    scopeTier = "Horizontal",
                    contents = new[]
                    {
                        new
                        {
                            key = "acme.cli-reports/trial-balance",
                            kind = "ReportDefinition",
                            version = "1.0.0",
                            content = CreateReportDefinition(),
                        },
                    },
                    dependencies = Array.Empty<object>(),
                    capabilityRequirements = Array.Empty<string>(),
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            await app.StartAsync();
            try
            {
                var address = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var client = new HttpClient();

                await AssertReportAbsentAsync(reports);
                Assert.Null(store.GetVersion(tenant, PackKey, "1.0.0"));
                using var exported = await RunCliAsync(
                    client, address, "pack", "export", "--request", requestPath, "--out", packPath);
                Assert.Equal(packPath, exported.RootElement.GetProperty("file").GetString());
                Assert.True(exported.RootElement.GetProperty("bytes").GetInt32() > 0);
                var exportedBytes = await File.ReadAllBytesAsync(packPath);
                Assert.NotEmpty(exportedBytes);
                Assert.Equal(exportedBytes.Length, exported.RootElement.GetProperty("bytes").GetInt32());
                Assert.Null(store.GetVersion(tenant, PackKey, "1.0.0"));
                await AssertReportAbsentAsync(reports);

                using var verified = await RunCliAsync(client, address, "pack", "verify", "--file", packPath);
                Assert.Equal("Verified", verified.RootElement.GetProperty("verdict").GetString());
                Assert.Equal("acme.cli-reports", verified.RootElement.GetProperty("manifestKey").GetString());
                Assert.Equal(1, verified.RootElement.GetProperty("epoch").GetInt64());
                Assert.Equal("OwnRoster", verified.RootElement.GetProperty("vouchingScope").GetString());
                Assert.Null(store.GetVersion(tenant, PackKey, "1.0.0"));
                await AssertReportAbsentAsync(reports);

                using var installed = await RunCliAsync(client, address, "pack", "install", "--file", packPath);
                Assert.True(installed.RootElement.GetProperty("installed").GetBoolean());
                Assert.Equal("acme.cli-reports", installed.RootElement.GetProperty("packKey").GetString());
                Assert.Equal("1.0.0", installed.RootElement.GetProperty("version").GetString());
                var draft = store.GetVersion(tenant, PackKey, "1.0.0");
                Assert.NotNull(draft);
                Assert.Equal("Draft", draft.Lifecycle.ToString());
                Assert.Null(store.GetActive(tenant, PackKey));
                await AssertReportAbsentAsync(reports);

                using var activated = await RunCliAsync(
                    client, address, "pack", "activate", "--pack-key", "acme.cli-reports", "--version", "1.0.0");
                Assert.True(activated.RootElement.GetProperty("activated").GetBoolean());
                Assert.Equal("acme.cli-reports", activated.RootElement.GetProperty("packKey").GetString());
                Assert.Equal("1.0.0", activated.RootElement.GetProperty("version").GetString());
                Assert.Empty(activated.RootElement.GetProperty("projectionRefusals").EnumerateArray());
                Assert.Empty(activated.RootElement.GetProperty("platformRefusals").EnumerateArray());
                var active = store.GetActive(tenant, PackKey);
                Assert.NotNull(active);
                Assert.Equal("Active", active.Lifecycle.ToString());
                Assert.Equal("1.0.0", active.Version);

                var retained = await reports.GetDefinitionAsync(Tenant, ReportKey, "1.0.0");
                Assert.NotNull(retained);
                Assert.Equal("acme.cli-reports/trial-balance", retained.Key);
                Assert.Equal("1.0.0", retained.Version);
                Assert.Equal("11111111-1111-1111-1111-111111111111", retained.Tenant);
                Assert.Equal(1, retained.SchemaVersion);
                Assert.Equal("trial-balance", retained.ReportKind);
                Assert.Equal("CLI trial balance", retained.Title);
                Assert.Equal("acme.cli-reports/trial-balance", retained.Envelope.Identity);
                Assert.Equal("1.0.0", retained.Envelope.Version);
                Assert.Equal("11111111-1111-1111-1111-111111111111", retained.Envelope.Tenant);
                Assert.Equal(CascadeLayer.Tenant, retained.Envelope.CascadeLayer);
                Assert.Empty(retained.Envelope.Requires);
                Assert.NotNull(retained.Envelope.Contract);
                Assert.Equal(1, retained.Envelope.Contract.Major);
                Assert.Equal(0, retained.Envelope.Contract.Minor);
                Assert.Equal(
                    """{"derivedAt":"2026-07-15T09:30:00+00:00","exportingActor":"acme-report-admin","originPackKey":"acme.cli-reports","tier":"Civilian"}""",
                    retained.Provenance.GetRawText());
                Assert.Equal("22222222-2222-2222-2222-222222222222",
                    retained.Parameters.GetProperty("chartId").GetString());
                Assert.Equal("2026-07-15", retained.Parameters.GetProperty("asOfDate").GetString());
                Assert.Equal(JsonValueKind.Null, retained.Parameters.GetProperty("fiscalPeriodId").ValueKind);
                Assert.False(retained.Parameters.GetProperty("includeZeroBalanceAccounts").GetBoolean());
                Assert.False(retained.Parameters.GetProperty("includeInactiveAccounts").GetBoolean());
                Assert.Single(await reports.ListDefinitionsAsync(Tenant));
                Assert.Null(await reports.GetDefinitionAsync(ForeignTenant, ReportKey, "1.0.0"));
                Assert.Empty(await reports.ListDefinitionsAsync(ForeignTenant));
                Assert.Equal(exportedBytes, await File.ReadAllBytesAsync(packPath));
                Assert.Equal(0, unused.Calls);
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ReportDefinition CreateReportDefinition()
    {
        using var provenance = JsonDocument.Parse(ProvenanceJson);
        return new ReportDefinition
        {
            Key = "acme.cli-reports/trial-balance",
            Version = "1.0.0",
            Tenant = "11111111-1111-1111-1111-111111111111",
            Envelope = new(
                "acme.cli-reports/trial-balance", "1.0.0", "11111111-1111-1111-1111-111111111111",
                CascadeLayer.Tenant, provenance.RootElement.Clone(), [], new(1, 0)),
            SchemaVersion = 1,
            ReportKind = "trial-balance",
            Title = "CLI trial balance",
            Parameters = JsonSerializer.SerializeToElement(
                new TrialBalanceParameters
                {
                    ChartId = new ChartOfAccountsId("22222222-2222-2222-2222-222222222222"),
                    AsOfDate = new DateOnly(2026, 7, 15),
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
    }

    private static async Task<JsonDocument> RunCliAsync(HttpClient client, string address, params string[] command)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await NodeOperatorCommand.RunAsync(
            new[] { "--url", address, "--json" }.Concat(command).ToArray(),
            client, stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Empty(stderr.ToString());
        return JsonDocument.Parse(stdout.ToString());
    }

    private static async Task AssertReportAbsentAsync(IReportDefinitionRegistry reports)
    {
        Assert.Null(await reports.GetDefinitionAsync(Tenant, ReportKey, "1.0.0"));
        Assert.Empty(await reports.ListDefinitionsAsync(Tenant));
        Assert.Null(await reports.GetDefinitionAsync(ForeignTenant, ReportKey, "1.0.0"));
        Assert.Empty(await reports.ListDefinitionsAsync(ForeignTenant));
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }

    private sealed class UnusedFinancialPorts :
        IChartRepository, IAccountResolver, IFiscalPeriodRepository, IGeneralLedgerReadModel
    {
        public int Calls { get; private set; }

        public Task<ChartOfAccounts?> GetAsync(ChartOfAccountsId chartId, CancellationToken cancellationToken = default)
            => throw Unexpected();
        public Task<GLAccount?> GetAsync(GLAccountId id, CancellationToken cancellationToken = default)
            => throw Unexpected();
        public Task<IReadOnlyList<GLAccount>> EnumerateForChartAsync(
            ChartOfAccountsId chartId, bool includeInactive = false, CancellationToken cancellationToken = default)
            => throw Unexpected();
        public Task<FiscalPeriod?> GetAsync(FiscalPeriodId id, CancellationToken cancellationToken = default)
            => throw Unexpected();
        public Task<IReadOnlyList<FiscalPeriod>> GetByFiscalYearAsync(
            FiscalYearId fiscalYearId, CancellationToken cancellationToken = default)
            => throw Unexpected();
        public Task<FiscalPeriod?> FindByChartAndDateAsync(
            ChartOfAccountsId chartId, DateOnly date, CancellationToken cancellationToken = default)
            => throw Unexpected();
        public Task InsertAsync(FiscalPeriod period, CancellationToken cancellationToken = default)
            => throw Unexpected();
        public Task<bool> UpdateAsync(FiscalPeriod period, CancellationToken cancellationToken = default)
            => throw Unexpected();
        public Task<FiscalPeriod?> GetByExternalRefAsync(string externalRef, CancellationToken cancellationToken = default)
            => throw Unexpected();
        public Task<IReadOnlyDictionary<GLAccountId, decimal>> GetAccountBalancesAsOfAsync(
            TenantId tenantId, ChartOfAccountsId chartId, DateOnly asOf, string snapshotMarker,
            CancellationToken cancellationToken = default)
            => throw Unexpected();
        public Task<IReadOnlyDictionary<GLAccountId, IReadOnlyDictionary<LegalEntityId, decimal>>> GetIntercompanyBalancesAsOfAsync(
            TenantId tenantId, ChartOfAccountsId chartId, DateOnly asOf, string snapshotMarker,
            CancellationToken cancellationToken = default)
            => throw Unexpected();

        private InvalidOperationException Unexpected()
        {
            Calls++;
            return new InvalidOperationException("Report transport must not query financial data.");
        }
    }
}
