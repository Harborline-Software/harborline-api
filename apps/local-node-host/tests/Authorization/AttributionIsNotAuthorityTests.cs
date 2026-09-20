using System.Reflection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// T-585 item 5 — the first-authority and Access provenance boundary: attribution never establishes
/// authority, asserted where the two meet.
/// </summary>
/// <remarks>
/// <para>
/// DES-0032 <c>access-cc-18</c>: "Access owns attribution and the authorized source… Ambient selection is
/// not authorization; transport may carry selection, but the gate authorizes from the acting principal and
/// tenant-scoped clip." The seam is <see cref="SelectedSessionRequestPrincipal"/>: the ONE per-request object
/// from which both the gate's principal/tenant and the audit attribution are derived. Everything below asks
/// the same question of that seam from a different side — can a fact about WHO an act is attributed to move
/// a verdict, or confer authority, by itself?
/// </para>
/// <para>
/// The neighbouring <c>AuthoritySnapshotTests</c> proves the audit side does not read ambient attribution.
/// This file proves the other direction and the boundary itself: the deciding side cannot read attribution
/// at all, and the first-authority provenance an attributed act could be mistaken for is a different value
/// written by a different owner.
/// </para>
/// </remarks>
public sealed class AttributionIsNotAuthorityTests
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-585");
    private static readonly ActorId Principal = new("principal-585");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-20T12:00:00Z");

    private static AuthorizationGateRequest FullyAttributed() =>
        new(PermissionAtom.Parse("records:write@/records/a"),
            Principal,
            Tenant,
            new AuthorizationTarget("record", "a", ScopeExpression.Parse("/records/a")),
            At)
        {
            // Everything a caller can say about who is acting, said as loudly as the shape allows.
            CorrelationId = Guid.Parse("58500000-0000-0000-0000-000000000001"),
            Roster = new AuthorizationRosterInputs("party-585", Member: true, Ejected: false)
            {
                RegistryMember = true,
                ProspectiveAdministratorGrant = true,
            },
            GrantRefusal = null,
        };

    [Fact(DisplayName = "T-585 item 5: a fully attributed caller with no conferred grant is denied")]
    public async Task Attribution_Alone_Never_Establishes_Authority()
    {
        // The roster the gate itself reads says the same flattering things the caller said, so the only
        // difference between the two runs below is whether a grant confers the act.
        var attributed = new AuthorizationRosterInputs("party-585", Member: true, Ejected: false)
        {
            RegistryMember = true,
        };

        var withoutGrant = await TestAuthorization
            .GateWithRoster(allowed: false, attributed)
            .DecideAsync(FullyAttributed());
        var withGrant = await TestAuthorization
            .GateWithRoster(allowed: true, attributed)
            .DecideAsync(FullyAttributed());

        Assert.Equal(AuthorizationVerdict.Denied, withoutGrant.Verdict);
        Assert.Equal(AuthorizationVerdict.Allowed, withGrant.Verdict);
    }

    [Fact(DisplayName = "T-585 item 5: a caller-asserted roster cannot outvote the roster the gate reads")]
    public async Task Caller_Asserted_Membership_Does_Not_Move_The_Verdict()
    {
        // The request asserts an unejected member; the roster authority says ejected. If the caller's own
        // attribution were an input, the conferred grant below would be enough to allow the act.
        var ejected = new AuthorizationRosterInputs("party-585", Member: true, Ejected: true)
        {
            RegistryMember = true,
            RequireMember = true,
        };

        var decision = await TestAuthorization
            .GateWithRoster(allowed: true, ejected)
            .DecideAsync(FullyAttributed());

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
    }

    [Fact(DisplayName = "T-585 item 5: the deciding assembly cannot see the attribution envelope")]
    public void The_Gate_Cannot_Reach_Attribution_At_All()
    {
        // The strongest form of "attribution is not an input": the assembly that decides does not reference
        // the assembly that attributes, so no future field can quietly become one. A dependency in this
        // direction is the change this assertion exists to stop.
        var deciding = typeof(AuthorizationGate).Assembly;
        var attributing = typeof(NodeCallerAttribution).Assembly.GetName().Name;

        Assert.NotEqual(attributing, deciding.GetName().Name);
        Assert.DoesNotContain(
            deciding.GetReferencedAssemblies(),
            reference => reference.Name == attributing);
    }

    [Fact(DisplayName = "T-585 item 5: no attribution fact is a gate input")]
    public void No_Attribution_Fact_Is_A_Gate_Input()
    {
        // The two records meet at SelectedSessionRequestPrincipal, which projects into both. What crosses
        // must be the acting principal and its tenant and nothing else: membership ids, owner versions,
        // correlation ids and the authorization epoch are attribution, and the gate decides without them.
        var attribution = typeof(NodeCallerAttribution)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var gateInputs = typeof(AuthorizationGateRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(attribution);
        Assert.NotEmpty(gateInputs);
        Assert.Empty(attribution.Intersect(gateInputs, StringComparer.Ordinal));
    }

    [Fact(DisplayName = "T-585 item 5: attributed acts cannot carry first authority")]
    public void First_Authority_Is_A_Provenance_No_Attributed_Act_Can_Write()
    {
        // The first-authority half of the boundary. An installation's first authority is a provenance value
        // on the administrator log, not a property of whoever an act is attributed to: the attribution
        // envelope carries no provenance at all, so "this act was attributed to the founder" can never be
        // read as "this act was performed with first authority".
        var attributionTypes = typeof(NodeCallerAttribution)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType)
            .ToArray();

        Assert.DoesNotContain(typeof(AdministratorProvenance), attributionTypes);
        Assert.NotEqual(AdministratorProvenance.Bootstrap, AdministratorProvenance.None);
    }
}
