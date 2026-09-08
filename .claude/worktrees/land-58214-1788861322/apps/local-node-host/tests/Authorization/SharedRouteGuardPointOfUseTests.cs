using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Contract tests for the shared route guard's point-of-use resolution (ticket 205 slice 4; ledger
/// L592/L600/L671).
/// </summary>
/// <remarks>
/// <para>
/// <c>RequestAuthorization.RefusalAsync</c> is the ONE junction the record-scoped route families go
/// through — contacts, invoices, bank accounts, entities, the ledger, form definitions, scheduling,
/// spatial frames and the authorization-admin surface. Testing it here tests all of them at the place
/// they share, which is where the resolution actually happens; each family's own route tests still
/// assert its wire.
/// </para>
/// <para>
/// The gate is real (<see cref="TestRouteGate"/>): a grant at one record's scope genuinely does not cover
/// an act at another's, so an out-of-scope refusal here is the production scope containment refusing and
/// not a double's opinion.
/// </para>
/// </remarks>
public sealed class SharedRouteGuardPointOfUseTests
{
    private static readonly TenantId Tenant = new("tenant-shared-route-guard");

    /// <summary>Every record-scoped route family, as its routes address a record: the operation the guard
    /// resolves and the record id the route supplies.</summary>
    public static TheoryData<string, string> RecordScopedFamilies() => new()
    {
        { Permission.ContactsWrite, "contact-1" },
        { Permission.ContactsRead, "contact-1" },
        { Permission.ContactsArchive, "contact-1" },
        { TeamRolePermissions.RecordsWrite, "invoice-1" },
        { TeamRolePermissions.RecordsRead, "invoice-1" },
        { TeamRolePermissions.LedgerPost, "journal-1" },
        { Permission.FormsAuthor, "form-1" },
        { Permission.SchedulingRead, "definition-1" },
        { Permission.SchedulingAuthor, "definition-1" },
        { Permission.SpatialRead, "anchor-1" },
    };

    /// <summary>The operations a route may resolve with NO record, because the definition side declares
    /// them install-wide: a list, a create whose record does not exist yet, a no-effect validate, and the
    /// install's own authorization configuration.</summary>
    public static TheoryData<string> InstallWideFamilies() => new()
    {
        Permission.ContactsRead,
        Permission.ContactsCreate,
        Permission.SchedulingRead,
        Permission.SchedulingAuthor,
        Permission.SchedulingOperate,
        Permission.OrgManageSettings,
        TeamRolePermissions.RecordsRead,
        TeamRolePermissions.RecordsWrite,
        TeamRolePermissions.LedgerPost,
    };

    /// <summary>The operations whose every route names its record, so a record-less check is a bug.</summary>
    public static TheoryData<string> NeverInstallWide() => new()
    {
        Permission.ContactsWrite,
        Permission.ContactsArchive,
        Permission.FormsAuthor,
        Permission.SpatialRead,
    };

    [Theory]
    [MemberData(nameof(RecordScopedFamilies))]
    public async Task A_grant_outside_the_addressed_records_scope_refuses(string permission, string recordId)
    {
        var http = ContextWith(TestRouteGate.ScopedTo("some-other-record"));

        var refusal = await RequestAuthorization.RefusalAsync(
            http, Tenant, permission, RouteRecord.Of(recordId), CancellationToken.None);

        AssertRefused(refusal);
    }

    [Theory]
    [MemberData(nameof(RecordScopedFamilies))]
    public async Task A_grant_inside_the_addressed_records_scope_passes(string permission, string recordId)
    {
        var http = ContextWith(TestRouteGate.ScopedTo(recordId));

        var refusal = await RequestAuthorization.RefusalAsync(
            http, Tenant, permission, RouteRecord.Of(recordId), CancellationToken.None);

        Assert.Null(refusal);
    }

    [Theory]
    [MemberData(nameof(NeverInstallWide))]
    public async Task An_operation_that_is_not_declared_install_wide_refuses_without_a_record(string permission)
    {
        // The holding is not the question: this gate holds everything. The act is refused because it names
        // no record and the definition side never declared this operation install-wide (L600/L671).
        var http = ContextWith(TestRouteGate.AllowAll());

        var refusal = await RequestAuthorization.RefusalAsync(
            http, Tenant, permission, RouteRecord.TheInstall, CancellationToken.None);

        AssertRefused(refusal);
    }

    [Theory]
    [MemberData(nameof(InstallWideFamilies))]
    public async Task A_declared_install_wide_operation_passes_without_a_record(string permission)
    {
        var http = ContextWith(TestRouteGate.AllowAll());

        var refusal = await RequestAuthorization.RefusalAsync(
            http, Tenant, permission, RouteRecord.TheInstall, CancellationToken.None);

        Assert.Null(refusal);
    }

    [Theory]
    [MemberData(nameof(InstallWideFamilies))]
    public async Task A_declared_install_wide_operation_still_refuses_a_record_scoped_grant(string permission)
    {
        // Declaring an operation install-wide admits the record-LESS shape on the install root; it does not
        // hand the act to a principal whose grant only covers one record.
        var http = ContextWith(TestRouteGate.ScopedTo("some-record"));

        var refusal = await RequestAuthorization.RefusalAsync(
            http, Tenant, permission, RouteRecord.TheInstall, CancellationToken.None);

        AssertRefused(refusal);
    }

    [Fact]
    public async Task A_blank_record_id_is_an_unresolvable_record_not_an_install_wide_act()
    {
        var http = ContextWith(TestRouteGate.AllowAll());

        var refusal = await RequestAuthorization.RefusalAsync(
            http, Tenant, Permission.ContactsWrite, RouteRecord.Of("   "), CancellationToken.None);

        AssertRefused(refusal);
    }

    [Fact]
    public async Task A_caller_supplied_record_id_carrying_a_scope_separator_refuses_rather_than_throwing()
    {
        var http = ContextWith(TestRouteGate.AllowAll());

        var refusal = await RequestAuthorization.RefusalAsync(
            http, Tenant, Permission.ContactsWrite, RouteRecord.Of("../other"), CancellationToken.None);

        AssertRefused(refusal);
    }

    [Fact]
    public async Task An_act_the_container_cannot_decide_does_not_happen()
    {
        // No gate in this request's container: the guard has nothing to resolve against, and an
        // undecidable act refuses rather than falling back to any ambient answer.
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };

        var refusal = await RequestAuthorization.RefusalAsync(
            http, Tenant, Permission.ContactsWrite, RouteRecord.Of("contact-1"), CancellationToken.None);

        AssertRefused(refusal);
    }

    [Fact]
    public void The_record_kind_a_family_targets_comes_from_the_gate_not_from_the_call_site()
    {
        // The junction never spells a record kind: it derives one from the operation through the same
        // reading AuthorizationGate.Validate enforces, so the two cannot drift apart.
        Assert.Equal(
            "contacts",
            AuthorizationGate.RecordKindFor(AuthorizationOperation.Parse(Permission.ContactsWrite)));
        Assert.Equal(
            "record",
            AuthorizationGate.RecordKindFor(AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite)));
        Assert.Equal(
            "journal-entry",
            AuthorizationGate.RecordKindFor(AuthorizationOperation.Parse(TeamRolePermissions.LedgerPost)));
        Assert.Equal(
            "forms",
            AuthorizationGate.RecordKindFor(AuthorizationOperation.Parse(Permission.FormsAuthor)));
        Assert.Equal(
            "scheduling",
            AuthorizationGate.RecordKindFor(AuthorizationOperation.Parse(Permission.SchedulingRead)));
        Assert.Equal(
            "spatial",
            AuthorizationGate.RecordKindFor(AuthorizationOperation.Parse(Permission.SpatialRead)));
    }

    private static DefaultHttpContext ContextWith(AuthorizationGate gate)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gate);
        services.AddTestKernelClock();
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private static void AssertRefused(IResult? refusal)
    {
        Assert.NotNull(refusal);
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(refusal);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }
}
