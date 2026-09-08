using System;
using System.Linq;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.LocalNodeHost.Data.Drafts;
using Harborline.Api.LocalNodeHost.Data.Financial;

using Xunit;

using MultiTenancyContext = Harborline.Api.Foundation.MultiTenancy.ITenantContext;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Ticket 205 slice 5 — the contract of the two ambient context adapters (slice 1 inventory rows 8 and 9)
/// after they stopped resolving current authorization without a record.
/// </summary>
/// <remarks>
/// <para>
/// Neither adapter adapts an ACT. <see cref="NodeAuthorizationTenantContext"/> exists to give
/// <see cref="PartyContext"/> the UserId + TenantId it derives a party from; the web-plane fence existed to
/// refuse a hosted-web member the desktop operator's grants at the one seam that ASKED an authorization
/// question by resolving <see cref="IAuthorizationContext"/>. Slices 3 and 4 converted every route that
/// asked at that seam to resolve its own <see cref="AuthorizationGate"/> decision, keyed by the request's
/// canonical principal, so what is left is a question nobody asks and an answer nobody can scope. This
/// class pins that both are now unreachable rather than merely unused.
/// </para>
/// </remarks>
public sealed class AmbientContextAdapterPointOfUseTests
{
    /// <summary>Row 8 — the party adapter refuses to answer, and says where the answer belongs.</summary>
    [Fact]
    public void ThePartyAdapterRefusesARecordLessPermissionAndNamesTheGate()
    {
        var adapter = new NodeAuthorizationTenantContext(
            new StubCurrentUser(),
            new StubTenant());

        var refusal = Assert.Throws<NotSupportedException>(
            () => adapter.HasPermission("financial:period-override-soft-close"));

        Assert.Contains("AuthorizationGate.DecideAsync", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("financial:period-override-soft-close", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Row 8 — and it still does the job it exists for. Refusing the permission question must not cost the
    /// party derivation the same-token UserId + TenantId it reads off this one instance.
    /// </summary>
    [Fact]
    public void ThePartyAdapterStillCarriesTheIdentityAndTenantPartyDerivationReads()
    {
        var adapter = new NodeAuthorizationTenantContext(
            new StubCurrentUser(),
            new StubTenant());

        Assert.Equal(StubCurrentUser.Id, adapter.UserId);
        Assert.Equal(StubTenant.Tenant, adapter.Tenant?.Id);
    }

    /// <summary>
    /// Row 9 — the node's composition root no longer offers a record-less authorization answer AT ALL. The
    /// web-plane fence was a blanket refusal over this registration; with the registration gone there is no
    /// seam left for a future route to resolve an ambient verdict from, which is a stronger closing than
    /// the decorator was.
    /// </summary>
    [Fact]
    public void TheNodeCompositionRegistersNoAmbientAuthorizationContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddNodeFinancialPosting();

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IAuthorizationContext));
    }

    /// <summary>
    /// Row 9 — and the deleted decorator is really gone from the shipped assembly, so nothing can
    /// re-register it.
    /// </summary>
    [Fact]
    public void TheWebPlaneAmbientFenceTypeIsNotInTheProductionAssembly()
    {
        var production = typeof(NodeAuthorizationTenantContext).Assembly;

        Assert.DoesNotContain(
            production.GetTypes(),
            type => type.Name == "WebPlaneFencedAuthorizationContext");
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        internal const string Id = "local";

        public string UserId => Id;

        public System.Collections.Generic.IReadOnlyList<string> Roles => Array.Empty<string>();
    }

    private sealed class StubTenant : MultiTenancyContext
    {
        internal static TenantId Tenant { get; } = new("tenant:adapter-contract");

        public TenantMetadata? Tenant_ => null;

        TenantMetadata? MultiTenancyContext.Tenant => new() { Id = Tenant, Name = Tenant.Value };
    }
}
