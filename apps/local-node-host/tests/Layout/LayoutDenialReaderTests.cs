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
/// T-731 slice 2 — DES-0052 layout-run-5 (host half): the host owns the only reader of Layout's
/// denial records. It finds one by authored binding plus request, and returns it only to a reader the
/// REAL gate, over the seeded production definitions, allows both the trace (<c>audit:read</c>) and the
/// denied record (<c>records:read</c> on that record).
/// </summary>
public sealed class LayoutDenialReaderTests
{
    private static readonly ActorId Both = new("auditor-member-731");
    private static readonly ActorId AuditorOnly = new("auditor-731");
    private static readonly ActorId MemberOnly = new("member-731");
    private static readonly ActorId AuditorOfAnotherRecord = new("auditor-other-record-731");

    [Fact(DisplayName = "layout-run-5: a reader authorized for the trace and the record finds the denial by binding plus request")]
    public async Task AReaderAuthorizedForBothFindsTheDenialByBindingPlusRequest()
    {
        await using var h = await Harness.CreateAsync();
        await h.DenyAsync("request-7");
        await h.DenyAsync("request-8");

        var read = await h.Reader.ReadAsync(Tenant, Both, "owner-card", "invoice.owner", "request-7", At);

        Assert.Equal(
            new LayoutRelatedDenial("request-7", "principal.clerk-4", "owner-card", LayoutBindingKinds.Static,
                "invoice.owner", Owner, "authorization.permission_required", "/records/party-19"),
            Assert.Single(read));
        Assert.Empty(await h.Reader.ReadAsync(Tenant, Both, "owner-card", "invoice.owner", "request-9", At));
        Assert.Empty(await h.Reader.ReadAsync(Tenant, Both, "owner-card", "invoice.other", "request-7", At));
        Assert.Empty(await h.Reader.ReadAsync(Tenant, Both, "other-card", "invoice.owner", "request-7", At));
    }

    [Theory(DisplayName = "layout-run-5: a reader lacking the trace or the record authorization gets nothing")]
    [InlineData("auditor-731")]
    [InlineData("member-731")]
    [InlineData("auditor-other-record-731")]
    [InlineData("stranger-731")]
    public async Task AReaderLackingEitherAuthorizationGetsNothing(string reader)
    {
        await using var h = await Harness.CreateAsync();
        await h.DenyAsync("request-7");

        Assert.Empty(await h.Reader.ReadAsync(Tenant, new ActorId(reader), "owner-card", "invoice.owner", "request-7", At));
        // The control: the same entry is there for a reader holding both.
        Assert.Single(await h.Reader.ReadAsync(Tenant, Both, "owner-card", "invoice.owner", "request-7", At));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly KeyPair _keys = KeyPair.Generate();

        private Harness(ServiceProvider provider)
        {
            _provider = provider;
            // The REAL gate, constructed as AuthorizationTraceReadTests does: the container's registered
            // gate is the writer's allow-all authority.
            var gate = new AuthorizationGate(
                provider.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
                provider.GetRequiredService<IRecordStandingResolver>(),
                provider.GetRequiredService<IAuthorizationDefinitionAtomReader>());
            Reader = new LayoutDenialReader(Trail, gate);
        }

        public InMemoryAuditTrail Trail { get; } = new();
        public LayoutDenialReader Reader { get; }

        public static async Task<Harness> CreateAsync()
        {
            var services = new ServiceCollection();
            services.AddSingleton(TestAuthorization.AllowGate());
            services.AddAccessGrantModule();
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
                .InstallAsync(Tenant, At, AuthorizationSeedProfile.Production);
            var grants = provider.GetRequiredService<IGrantStore>();
            var member = AccessGrantAuthorizationSeed.MemberRole;
            await grants.AppendAsync(Tenant, Grant(Both, RoleReference.Auditor, "/"));
            await grants.AppendAsync(Tenant, Grant(Both, member, "/"));
            await grants.AppendAsync(Tenant, Grant(AuditorOnly, RoleReference.Auditor, "/"));
            await grants.AppendAsync(Tenant, Grant(MemberOnly, member, "/"));
            // Trace authorization, and records:read on a DIFFERENT record only.
            await grants.AppendAsync(Tenant, Grant(AuditorOfAnotherRecord, RoleReference.Auditor, "/"));
            await grants.AppendAsync(Tenant, Grant(AuditorOfAnotherRecord, member, "/records/party-other"));
            return new Harness(provider);
        }

        /// <summary>Resolves the surface for principal.clerk-4 with the owner denied, through the host trace.</summary>
        public Task DenyAsync(string requestId)
        {
            var trace = new LayoutDenialGateLog(Trail, new Ed25519Signer(_keys), Tenant, new FixedTime(At), NullLogger.Instance);
            Resolve(new OutcomeSources(LayoutRelatedResult.Denied("authorization.permission_required", "/records/party-19", Owner)),
                trace, new LayoutResolutionRequest(requestId, "principal.clerk-4"));
            return trace.WrittenAsync();
        }

        private static AccessGrant Grant(ActorId subject, RoleReference role, string scope) => new(
            GrantId.New(), Tenant, subject, role, ScopeExpression.Parse(scope), GrantResidency.Cache,
            new GrantValidity(At.AddHours(-1)), GranterKind.Person, new ActorId("tenant-admin"),
            At.AddHours(-1),
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual),
                new ActorId("tenant-admin")), At.AddHours(-1));

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            _keys.Dispose();
        }
    }
}
