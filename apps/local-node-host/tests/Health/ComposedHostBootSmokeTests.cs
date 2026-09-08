using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Boots the compiled application entry point and probes its real listener. Unlike the focused
/// <see cref="HostBootSmokeTests"/> fixture, this test consumes the service graph composed by
/// <c>Program.cs</c>; it therefore catches catalog, constructor, and root-registration drift.
/// </summary>
/// <remarks>
/// <para>
/// It runs once PER DEPLOYED PROFILE, not once. <c>LocalNodeHostedComponentProfile</c> has three
/// switches (web client, LLM proxy, scheduling dogfood) and they gate real root-container
/// registrations. A single-profile boot proves only the profile it happened to pick — and the profile
/// this test originally picked was NOT the one any deployed host runs: the committed
/// <c>tooling/preview-host/dogfood-appsettings.Production.json</c> turns all three on. Covering one
/// of eight profiles while the deployed one goes unchecked is how a gate passes for the wrong reason.
/// </para>
/// <para>
/// The three rows are the deployed COMPONENT-PROFILE shapes — the axis
/// <c>LocalNodeHostedComponentProfile</c> actually switches on. They are not the full cross-product: the
/// remaining combinations are deployed by nothing, and a boot test costs a real process each.
/// </para>
/// <para>
/// The desktop row reproduces the sidecar's <c>MultiTeam__Enabled=false</c> default; web rows exercise
/// the multi-team branch used by dogfood. MultiTeam is not one of the component-profile switches, but
/// the distinction is load-bearing for the desktop permissions regression this fixture now guards.
/// </para>
/// </remarks>
public sealed partial class ComposedHostBootSmokeTests
{
    [Fact]
    public async Task Program_Composition_ClosesTheShippingAuditSameInstanceInvariant()
    {
        // This is intentionally a real compiled-entry-point boot, not a test ServiceCollection.
        // LocalNodeFinalGraphServiceProviderFactory resolves the final Program graph and refuses
        // startup unless IAuditTrail, IAuthorizedAuditTrail, and the shipping reader backing are
        // reference-equal. Readiness proves that exact assertion ran on the composed host provider.
        await using var host = ComposedHost.Start(
            "shipping audit same-instance invariant",
            webClient: false,
            llmProxy: false,
            schedulingDogfood: false,
            multiTeam: false,
            environment: "Production");

        Assert.NotNull(await host.AwaitReadinessAsync());
    }

    [Fact]
    public async Task Program_Composition_AdmitsPackNavigationThroughTheSharedRoleGate()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"s229-pack-navigation-composition-{Guid.NewGuid():N}");
        var tenant = new TenantId("aaaaaaaa-0000-0000-0000-000000002229");
        try
        {
            await Assert.ThrowsAsync<PackAdmissionCompositionProbeCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    ["--LocalNode:RootSeedHex=" + new string('2', 64)],
                    sessionTokenOverride: "s229-pack-navigation-probe",
                    dataDirectory: dataDirectory,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        var resolved = factory.CreateServiceProvider(factory.CreateBuilder(services));
                        using var providerLifetime = Assert.IsAssignableFrom<IDisposable>(resolved);
                        var admission = resolved.GetRequiredService<IPackContentAdmission>();
                        var roleGate = resolved.GetRequiredService<IRoleGateAdmission>();
                        Assert.IsType<PackWorkflowAdmissionAdapter>(admission);
                        Assert.Same(roleGate, resolved.GetRequiredService<RoleGateAdmission>());

                        var owned = admission.Admit(
                            [RoleBearingNavigation(AccessGrantAuthorizationSeed.PackageId)], tenant);
                        Assert.True(owned.IsAdmissible);

                        var foreign = admission.Admit(
                            [RoleBearingNavigation("foreign.navigation")], tenant);
                        var refusal = Assert.Single(foreign.Refusals);
                        Assert.Equal(
                            $"authorization.role_gate.{RoleGateAdmissionRules.VendorOwnOrTenantRoleOnly}",
                            refusal.Code);
                        throw new PackAdmissionCompositionProbeCompleteException();
                    },
                    installFootprintRootOverride: dataDirectory));
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Program_Composition_InstallsAndServesPackNavigationThroughTheShippingHost()
    {
        const string rootSeedHex =
            "1111111111111111111111111111111111111111111111111111111111111111";
        await using var bootstrapHost = ComposedHost.Start(
            "pack navigation shipping route",
            webClient: false,
            llmProxy: false,
            schedulingDogfood: false,
            multiTeam: false,
            environment: "Production",
            rootSeedHex: rootSeedHex);
        await bootstrapHost.AwaitReadinessAsync();
        await bootstrapHost.StopAsync();
        await GrantPackOperationToHostInstallerAsync(bootstrapHost, rootSeedHex);

        await using var host = ComposedHost.Start(
            "pack navigation shipping route after grant",
            webClient: false,
            llmProxy: false,
            schedulingDogfood: false,
            multiTeam: false,
            environment: "Production",
            rootSeedHex: rootSeedHex,
            dataDirectoryOverride: bootstrapHost.DataDirectory);
        var baseUri = await host.AwaitReadinessAsync();
        using var client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "composed-host-smoke-token");

        // The ordinary shipping preload now contributes Access navigation alongside installed packs.
        // Pin the active version independently of the declaration, so a stale preload cannot pass.
        using var installed = await client.GetAsync(PackInstallRoutes.ListInstalledRoute, host.Deadline);
        Assert.Equal(HttpStatusCode.OK, installed.StatusCode);
        using var installedDocument = JsonDocument.Parse(
            await installed.Content.ReadAsStringAsync(host.Deadline));
        var accessPack = Assert.Single(installedDocument.RootElement.EnumerateArray(), pack =>
            pack.GetProperty("packKey").GetString() == "harborline.access-administration");
        Assert.Equal("1.1.1", accessPack.GetProperty("version").GetString());
        Assert.Equal("Active", accessPack.GetProperty("lifecycle").GetString());

        using var export = await client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute,
            new
            {
                key = "ticket229.navigation",
                version = "1.0.0",
                name = "Ticket 229 Navigation",
                description = "real-composition navigation route proof",
                scopeTier = "Horizontal",
                contents = new[]
                {
                    new
                    {
                        key = "chrome",
                        kind = "NavWorkspaceConfig",
                        version = "1.0.0",
                        content = new
                        {
                            seedWorkspaces = new[]
                            {
                                new
                                {
                                    id = "ticket-229-workspace",
                                    labelKey = "workspaces.ticket-229",
                                },
                            },
                        },
                    },
                },
                dependencies = Array.Empty<object>(),
            },
            host.Deadline);
        var packBytes = await export.Content.ReadAsByteArrayAsync(host.Deadline);
        Assert.True(export.IsSuccessStatusCode, Encoding.UTF8.GetString(packBytes));

        using var packContent = new ByteArrayContent(packBytes);
        packContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var install = await client.PostAsync(
            PackInstallRoutes.InstallRoute, packContent, host.Deadline);
        var installBody = await install.Content.ReadAsStringAsync(host.Deadline);
        Assert.True(install.IsSuccessStatusCode, installBody);

        using var activate = await client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = "ticket229.navigation", version = "1.0.0" },
            host.Deadline);
        var activateBody = await activate.Content.ReadAsStringAsync(host.Deadline);
        Assert.True(activate.IsSuccessStatusCode, activateBody);

        using var navigation = await client.GetAsync(
            PackNavigationRoutes.NavigationRoute, host.Deadline);
        var navigationBody = await navigation.Content.ReadAsStringAsync(host.Deadline);
        Assert.Equal(HttpStatusCode.OK, navigation.StatusCode);
        using var document = JsonDocument.Parse(navigationBody);
        Assert.True(document.RootElement.GetProperty("configured").GetBoolean());
        var pack = document.RootElement.GetProperty("pack");
        Assert.Equal("harborline.active-pack-composition", pack.GetProperty("packId").GetString());
        var workspaces = pack.GetProperty("seedWorkspaces").EnumerateArray().ToArray();
        Assert.Equal(new[] { "access", "ticket-229-workspace" },
            workspaces.Select(workspace => workspace.GetProperty("id").GetString()));
        var access = workspaces[0];
        Assert.Equal("access.workspace", access.GetProperty("labelKey").GetString());
        var group = Assert.Single(access.GetProperty("groups").EnumerateArray());
        Assert.Equal("access-inspection", group.GetProperty("id").GetString());
        Assert.Equal("access.holders", group.GetProperty("labelKey").GetString());
        Assert.Equal("access.holders", Assert.Single(group.GetProperty("itemIds").EnumerateArray()).GetString());
        var panel = Assert.Single(pack.GetProperty("panelSet").EnumerateArray());
        Assert.Equal("access-details", panel.GetProperty("id").GetString());
        Assert.Equal("access.details", panel.GetProperty("labelKey").GetString());
        Assert.Equal("panels.access-details.toggle", panel.GetProperty("binding").GetString());
        Assert.Equal("mod+shift+a", panel.GetProperty("shortcut").GetString());
        Assert.Equal(400, panel.GetProperty("defaultWidth").GetInt32());
        Assert.Equal(300, panel.GetProperty("minimumHeight").GetInt32());
        Assert.False(panel.GetProperty("defaultOpen").GetBoolean());
    }

    private static async Task GrantPackOperationToHostInstallerAsync(
        ComposedHost host,
        string rootSeedHex,
        string? principalOverride = null)
    {
        var rootSeed = Convert.FromHexString(rootSeedHex);
        using var signer = new NodePrincipalSigner(rootSeed);
        var principal = new ActorId(principalOverride ?? signer.Signer.IssuerId.ToBase64Url());
        var tenant = ActiveTeamTenantContext.ProjectTenantId(GenesisTeamId.Derive(rootSeed));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlCipherLocalNodeDbContext(
            rootSeed,
            Path.Combine(host.DataDirectory, "local-node.db"),
            new SqlCipherKeyDerivation());
        await using var provider = services.BuildServiceProvider();
        var grants = new NodeEfGrantStore(new OpenedSearchContextFactory(
            provider.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>()));
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        await grants.AppendAsync(tenant, new AccessGrant(
            GrantId.New(),
            tenant,
            principal,
            RoleReference.Administrator,
            ScopeExpression.Parse("/"),
            GrantResidency.Cache,
            new GrantValidity(at, at.AddHours(1)),
            GranterKind.Person,
            principal,
            at,
            new GrantProvenance(
                GrantSourceKind.Manual,
                new GrantReason(GrantReasonCodes.Manual, "ticket-229-composed-host"),
                principal),
            at));
    }

    private sealed class OpenedSearchContextFactory(
        IDbContextFactory<NodeLocalSearchDbContext> inner)
        : IDbContextFactory<NodeLocalSearchDbContext>
    {
        public NodeLocalSearchDbContext CreateDbContext()
        {
            var context = inner.CreateDbContext();
            context.Database.OpenConnection();
            return context;
        }

        public async Task<NodeLocalSearchDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            var context = await inner.CreateDbContextAsync(cancellationToken);
            await context.Database.OpenConnectionAsync(cancellationToken);
            return context;
        }
    }

    private static PackComposedItem RoleBearingNavigation(string packageKey) => new(
        packageKey,
        "chrome",
        PackContentKind.NavWorkspaceConfig,
        "1.0.0",
        """
        {
          "seedWorkspaces": [{
            "id": "operations",
            "labelKey": "workspaces.operations",
            "createActions": [{
              "id": "create",
              "verbKey": "actions.create",
              "icon": "plus",
              "binding": "records.create",
              "shortcut": "mod+n",
              "permittedRoles": ["tax.roles/member"]
            }]
          }]
        }
        """);

    private sealed class PackAdmissionCompositionProbeCompleteException : Exception;

    /// <summary>Deployed composition shapes: label, web client, LLM proxy, scheduling dogfood.</summary>
    public static TheoryData<string, bool, bool, bool> DeployedProfiles() => new()
    {
        // ADR 0161 Path A: this row also proves unconditional hosted services resolve without any
        // web-only registrations, including IWebSelectedSessionIdentityAuthority.
        { "web client off (ADR 0161 Path-A desktop-only install)", false, false, false },
        { "web host", true, false, false },
        { "customer-zero (dogfood-appsettings.Production.json)", true, true, true },
    };

    [Theory]
    [MemberData(nameof(DeployedProfiles))]
    public async Task Program_Composition_Starts_And_Serves_Health(
        string profile,
        bool webClient,
        bool llmProxy,
        bool schedulingDogfood)
    {
        await using var host = ComposedHost.Start(
            profile, webClient, llmProxy, schedulingDogfood, multiTeam: webClient,
            environment: "Production");

        var baseUri = await host.AwaitReadinessAsync();
        using var client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(10),
        };
        using var health = await client.GetAsync("/health", host.Deadline);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        if (!webClient)
        {
            using var permissions = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/session/permissions");
            permissions.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", "composed-host-smoke-token");
            using var response = await client.SendAsync(permissions, host.Deadline);
            var body = await response.Content.ReadAsStringAsync(host.Deadline);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(body);
            var served = document.RootElement.GetProperty("permissions")
                .EnumerateArray().Select(value => value.GetString()).ToArray();
            // NOT NotEmpty. The raw roster set satisfied NotEmpty while 13 of 20
            // destinations were invisible, so that assertion could not see the defect it
            // was positioned to catch. Name destinations the desktop rail depends on.
            Assert.Contains("inbox:read", served);
            Assert.Contains("calendar:read", served);
            Assert.Contains("members:manage", served);
            Assert.Contains("org:manage-settings", served);
        }
    }

    /// <summary>
    /// CONTAINER VALIDATION (ticket 091). The same deployed profiles, booted in Development — the
    /// only environment in which <c>Program.cs</c> hands the provider factory
    /// <c>ValidateOnBuild = true</c> and <c>ValidateScopes = true</c>. Reaching readiness under
    /// those options IS the assertion: the container has walked every descriptor in the real
    /// production graph and built a call site for each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the sibling theory above could not catch this: it boots in Production, where both
    /// validation options are off. It proves the graph COMPOSES — every registration callback ran —
    /// not that the graph RESOLVES. <c>RecoveryCoordinator</c> was registered with an
    /// <c>IDisputerValidator</c> that nothing supplied, and every Production boot went green over
    /// it. That descriptor would have thrown at the first resolution of <c>IRecoveryCoordinator</c>
    /// instead — which is to say, at whatever moment a real recovery was attempted.
    /// </para>
    /// <para>
    /// It runs per profile for the same reason the sibling does: the three switches gate real root
    /// registrations, so an unbuildable descriptor reachable only from the desktop profile is
    /// invisible to a customer-zero-only check.
    /// </para>
    /// <para>
    /// This is the fence behind the constructor check in <see cref="DiConstructibilityTests"/>,
    /// which can see a registered type with no public constructor but explicitly cannot see a
    /// MISSING registration. Only the container's own validation does that.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(DeployedProfiles))]
    public async Task Program_Composition_Passes_Container_Validation(
        string profile,
        bool webClient,
        bool llmProxy,
        bool schedulingDogfood)
    {
        await using var host = ComposedHost.Start(
            profile, webClient, llmProxy, schedulingDogfood, multiTeam: webClient,
            environment: "Development");

        // No HTTP probe. A host that binds a port under ValidateOnBuild has already proven the
        // whole graph resolvable; whether it serves is the sibling theory's question, not this one.
        await host.AwaitReadinessAsync();
    }

    [Fact]
    public async Task Program_Composition_Failure_Exits_Without_A_Dialog()
    {
        await using var host = ComposedHost.Start(
            "malformed composition input",
            webClient: false,
            llmProxy: false,
            schedulingDogfood: false,
            multiTeam: false,
            environment: "Production",
            rootSeedHex: "not-hex");

        var result = await host.AwaitExitAsync();

        Assert.Equal(70, result.ExitCode);
        Assert.Contains(
            "local-node.fatal: InvalidOperationException: LocalNode:RootSeedHex is set but is not valid hex.",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_composition_real_authorization_seed_never_requests_dev_indexer_grant()
    {
        const string rootSeedHex =
            "2222222222222222222222222222222222222222222222222222222222222222";
        await using var host = ComposedHost.Start(
            "production authorization seed",
            webClient: false,
            llmProxy: false,
            schedulingDogfood: false,
            multiTeam: false,
            environment: "Production",
            rootSeedHex: rootSeedHex);
        await host.AwaitReadinessAsync();
        await host.StopAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlCipherLocalNodeDbContext(
            Convert.FromHexString(rootSeedHex),
            Path.Combine(host.DataDirectory, "local-node.db"),
            new SqlCipherKeyDerivation());
        await using var provider = services.BuildServiceProvider();
        var grants = new NodeEfGrantStore(
            provider.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>());
        var team = GenesisTeamId.Derive(Convert.FromHexString(rootSeedHex));
        var tenant = ActiveTeamTenantContext.ProjectTenantId(team);

        Assert.NotNull(await grants.FindBySourceReferenceAsync(
            tenant, AccessGrantAuthorizationSeed.SchedulerGrantSource));
        Assert.Null(await grants.FindBySourceReferenceAsync(
            tenant, AccessGrantAuthorizationSeed.DevIndexerGrantSource));
    }

    /// <summary>
    /// One booted composed host: the real compiled entry point, its captured streams, and the
    /// post-bind readiness handshake. Shared by both theories so neither can drift from the other's
    /// idea of what "the deployed host" means.
    /// </summary>
    private sealed class ComposedHost : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _standardOutput = new();
        private readonly StringBuilder _standardError = new();
        private readonly object _outputGate = new();
        private readonly TaskCompletionSource<Uri> _readiness =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _deadline =
            new(TimeSpan.FromMinutes(3));
        private readonly string _dataDirectory;
        private readonly string _profile;
        private readonly string _environment;

        private ComposedHost(Process process, string dataDirectory, string profile, string environment)
        {
            _process = process;
            _dataDirectory = dataDirectory;
            _profile = profile;
            _environment = environment;
        }

        public CancellationToken Deadline => _deadline.Token;
        public string DataDirectory => _dataDirectory;

        public static ComposedHost Start(
            string profile,
            bool webClient,
            bool llmProxy,
            bool schedulingDogfood,
            bool multiTeam,
            string environment,
            string? rootSeedHex = null,
            string? dataDirectoryOverride = null,
            int healthPort = 0)
        {
            var hostDll = LocateHostDll();
            var dataDirectory = dataDirectoryOverride ?? Path.Combine(
                Path.GetTempPath(),
                $"harborline-composed-host-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);

            var process = StartHost(
                hostDll,
                dataDirectory,
                webClient,
                llmProxy,
                schedulingDogfood,
                multiTeam,
                environment,
                rootSeedHex,
                out var readinessMarker, healthPort);

            var host = new ComposedHost(process, dataDirectory, profile, environment);
            host.Attach(readinessMarker);
            return host;
        }

        private void Attach(string readinessMarker)
        {
            _process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null)
                {
                    return;
                }

                lock (_outputGate)
                {
                    _standardOutput.AppendLine(args.Data);
                }

                if (!args.Data.StartsWith(readinessMarker, StringComparison.Ordinal))
                {
                    return;
                }

                var address = args.Data[readinessMarker.Length..].Trim();
                if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
                {
                    _readiness.TrySetResult(uri);
                }
                else
                {
                    _readiness.TrySetException(new InvalidOperationException(
                        $"The composed host emitted an invalid readiness URL: '{address}'."));
                }
            };
            _process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is null)
                {
                    return;
                }

                lock (_outputGate)
                {
                    _standardError.AppendLine(args.Data);
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        /// <summary>
        /// Waits for the post-bind readiness line, or fails naming the profile and dumping both
        /// streams. When the container refused to build the graph, that verdict is stated first —
        /// the descriptor error is otherwise buried in a long provider stack trace.
        /// </summary>
        public async Task<Uri> AwaitReadinessAsync()
        {
            var exited = _process.WaitForExitAsync();
            var timeout = Task.Delay(Timeout.InfiniteTimeSpan, _deadline.Token);
            var completed = await Task.WhenAny(_readiness.Task, exited, timeout);
            if (completed == _readiness.Task)
            {
                return await _readiness.Task;
            }

            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            await WaitForExitAndDrainAsync(_process);

            var stdout = Snapshot(_standardOutput, _outputGate);
            var stderr = Snapshot(_standardError, _outputGate);
            var verdict =
                $"The composed local-node host did not signal readiness for profile '{_profile}' " +
                $"in the {_environment} environment.";

            // The container names the unresolvable service in this sentence. Surfacing it above the
            // stack trace is the difference between a test that goes red and a test that says why.
            const string validationFailure = "Some services are not able to be constructed";
            if (stderr.Contains(validationFailure, StringComparison.Ordinal) ||
                stdout.Contains(validationFailure, StringComparison.Ordinal))
            {
                verdict +=
                    "\nThe CONTAINER REFUSED TO BUILD THE GRAPH: a registered service depends on " +
                    "something nothing registers. The failing descriptor is named below. Note that " +
                    "validation reports only the FIRST failure — fix it and re-run, because there " +
                    "may be more behind it.";
            }

            Assert.Fail($"{verdict}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            throw new InvalidOperationException("unreachable");
        }

        public async Task<ProcessExit> AwaitExitAsync()
        {
            var exited = _process.WaitForExitAsync();
            var timeout = Task.Delay(Timeout.InfiniteTimeSpan, _deadline.Token);
            if (await Task.WhenAny(exited, timeout) != exited)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                await WaitForExitAndDrainAsync(_process);
                Assert.Fail(
                    $"The composed local-node host did not exit on its own for profile '{_profile}'.");
            }

            await WaitForExitAndDrainAsync(_process);
            return new ProcessExit(
                _process.ExitCode,
                Snapshot(_standardError, _outputGate));
        }

        public async Task StopAsync()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await WaitForExitAndDrainAsync(_process);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            await WaitForExitAndDrainAsync(_process);
            _process.Dispose();
            _deadline.Dispose();
            TryDelete(_dataDirectory);
        }
    }

    /// <summary>
    /// Resolves the dotnet muxer for the CURRENT machine: DOTNET_HOST_PATH when the SDK
    /// provides it, the running muxer when the test host was launched by one, else PATH.
    /// A hard-coded install path breaks on any other OS (Mac vs Linux CI).
    /// </summary>
    private static string ResolveDotnetMuxer()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
    }

    /// <summary>
    /// Starts the host on Kestrel's own ephemeral port and returns the marker used by the
    /// post-bind readiness handshake.
    /// </summary>
    private static Process StartHost(
        string hostDll,
        string dataDirectory,
        bool webClient,
        bool llmProxy,
        bool schedulingDogfood,
        bool multiTeam,
        string environment,
        string? rootSeedHex,
        out string readinessMarker,
        int healthPort = 0)
    {
        readinessMarker = "harborline-composed-host-ready-" +
            Guid.NewGuid().ToString("N") + ":";
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetMuxer(),
            WorkingDirectory = Path.GetDirectoryName(hostDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(hostDll);
        // BOTH keys. WebApplicationBuilder reads ASPNETCORE_ENVIRONMENT in preference to
        // DOTNET_ENVIRONMENT, so setting only one leaves the other free to contradict it from the
        // inherited parent environment — and the whole point of the Development row is that
        // IsDevelopment() decides whether the container validates.
        startInfo.Environment["DOTNET_ENVIRONMENT"] = environment;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{healthPort}";
        startInfo.Environment["LocalNode__HealthPort"] = healthPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["LocalNode__DataDirectory"] = dataDirectory;
        startInfo.Environment["LocalNode__RootSeedHex"] = rootSeedHex ?? new string('1', 64);
        startInfo.Environment["LocalNode__SessionToken"] = "composed-host-smoke-token";
        startInfo.Environment["HARBORLINE_TEST_READINESS_MARKER"] = readinessMarker;
        startInfo.Environment["HARBORLINE_TEST_INSTALL_FOOTPRINT_ROOT"] = dataDirectory;
        startInfo.Environment["Logging__EventLog__LogLevel__Default"] = "None";

        startInfo.Environment["LocalNode__WebClient__Enabled"] =
            webClient ? "true" : "false";

        // The LLM proxy is selected by a NON-EMPTY upstream base, not by a bool (Program.cs feeds
        // string.IsNullOrWhiteSpace(LlmUpstreamBase) into the profile). The address need not be
        // listening: this gate is about whether the graph COMPOSES, not whether the upstream answers.
        if (llmProxy)
        {
            startInfo.Environment["LocalNode__WebClient__LlmUpstreamBase"] = "http://127.0.0.1:11434";
        }
        else
        {
            // REMOVE, do not merely skip. ProcessStartInfo.Environment is SEEDED FROM THE PARENT, so a
            // runner whose environment already carries this key would silently boot the "web host" row as
            // the customer-zero profile -- the gate would report three profiles green while covering two.
            // Every other switch here is written in both directions; this one was the outlier.
            startInfo.Environment.Remove("LocalNode__WebClient__LlmUpstreamBase");
        }

        startInfo.Environment["LocalNode__SchedulingDogfood__Enabled"] =
            schedulingDogfood ? "true" : "false";

        startInfo.Environment["LocalNode__MultiTeam__Enabled"] = multiTeam ? "true" : "false";
        if (multiTeam)
        {
            startInfo.Environment["LocalNode__MultiTeam__TeamBootstraps__0__TeamId"] =
                "00000000-0000-0000-0000-0000000000a1";
            startInfo.Environment["LocalNode__MultiTeam__TeamBootstraps__0__DisplayName"] =
                "Composed host smoke";
        }
        else
        {
            startInfo.Environment.Remove("LocalNode__MultiTeam__TeamBootstraps__0__TeamId");
            startInfo.Environment.Remove("LocalNode__MultiTeam__TeamBootstraps__0__DisplayName");
        }

        return Process.Start(startInfo) ??
            throw new InvalidOperationException("Failed to start the composed local-node host.");
    }

    private static string LocateHostDll()
    {
        var adjacentHostDll = Path.Combine(
            AppContext.BaseDirectory,
            "Harborline.Api.LocalNodeHost.dll");
        if (File.Exists(adjacentHostDll))
        {
            return adjacentHostDll;
        }

        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = testOutput.Parent?.Name ??
            throw new DirectoryNotFoundException("Could not resolve the test build configuration.");
        var sourceRoot = LocateHostSourceRoot();
        var hostDll = Path.Combine(
            sourceRoot,
            "bin",
            configuration,
            "net11.0",
            "Harborline.Api.LocalNodeHost.dll");

        // THROW, never skip. A skip here would restore exactly the vacuous pass this test exists to
        // prevent -- the gate would go green on a host that was never built.
        return File.Exists(hostDll)
            ? hostDll
            : throw new FileNotFoundException("The composed local-node host was not built.", hostDll);
    }

    private static string LocateHostSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Harborline.LocalNodeHost.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the local-node host source root.");
    }

    private static async Task WaitForExitAndDrainAsync(Process process)
    {
        await process.WaitForExitAsync();
        process.WaitForExit();
    }

    private static string Snapshot(StringBuilder output, object gate)
    {
        lock (gate)
        {
            return output.ToString();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; the assertion result is the gate.
        }
    }

    private sealed record ProcessExit(int ExitCode, string StandardError);
}
