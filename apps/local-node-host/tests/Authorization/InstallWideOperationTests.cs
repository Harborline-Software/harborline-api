using System;
using System.Linq;
using System.Threading.Tasks;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Contract tests for the install-wide declaration on <see cref="AuthorizationGate"/> (ticket 205 slice 2;
/// ledger L600 "only install-wide capabilities may be checked without record scope" and L671).
/// </summary>
/// <remarks>
/// The shape mature authorization systems use: an object-less permission is never a per-call bypass flag.
/// NIST RBAC pairs every operation with an object, and Zanzibar models an instance-wide permission as a
/// relation on one fixed root object. So here membership is DECLARED on the definition side
/// (<see cref="PermissionVocabulary.InstallWideOperations"/>) and the omitted record target resolves against
/// one fixed install root scope — <c>/</c>, the same scope the founding definitions already carry. A call
/// site cannot assert install-wideness; it can only omit the record and be refused by name.
/// </remarks>
public sealed class InstallWideOperationTests
{
    private static readonly TenantId Tenant = new("tenant-install-wide");

    // contacts:write is deliberately NOT install-wide (ticket 205 slice 4): every route that resolves it
    // names the contact it addresses, so a record-less check of it is a bug and must refuse.
    private static AuthorizationOperation RecordScoped =>
        AuthorizationOperation.Parse(Permission.ContactsWrite);

    private static AuthorizationOperation InstallWide =>
        AuthorizationOperation.Parse(Permission.WorkshopUnlock);

    [Fact]
    public async Task A_record_scoped_atom_without_a_record_refuses_and_names_the_operation()
    {
        var authority = TestAuthorization.Write(Tenant);

        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await TestAuthorization.AllowGate().DecideAsync(authority.InstallWide(RecordScoped)));

        Assert.Contains(RecordScoped.Value, error.Message, StringComparison.Ordinal);
        Assert.Contains("install-wide", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declared_install_wide_atom_without_a_record_passes()
    {
        var authority = TestAuthorization.Write(Tenant);

        var decision = await TestAuthorization.AllowGate().DecideAsync(authority.InstallWide(InstallWide));

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        Assert.Equal(string.Empty, decision.Request.Target.RecordKind);
        Assert.Equal("/", decision.Request.Target.Scope.ToString());
    }

    [Fact]
    public async Task A_declared_install_wide_atom_without_a_record_still_resolves_the_principal_holdings()
    {
        // Install-wide is an admission rule for the REQUEST shape, never a standing allow: a principal whose
        // holdings do not cover the act is still denied.
        var decision = await TestAuthorization.Gate(allowed: false)
            .DecideAsync(TestAuthorization.Write(Tenant).InstallWide(InstallWide));

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
    }

    [Fact]
    public async Task A_declared_install_wide_atom_with_a_record_still_validates_the_record()
    {
        var authority = TestAuthorization.Write(Tenant);

        // Carrying a record target puts the install-wide operation back on the ordinary record path: the
        // record kind must match the operation resource...
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await TestAuthorization.AllowGate().DecideAsync(
                authority.Request(InstallWide, "record", "bench-7")));

        // ...and a matching record kind is admitted and scoped to that record, not to the install root.
        var decision = await TestAuthorization.AllowGate()
            .DecideAsync(authority.Request(InstallWide, "workshop", "bench-7"));

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        Assert.Equal("/records/bench-7", decision.Request.Target.Scope.ToString());
    }

    [Fact]
    public void The_declared_install_wide_set_is_exact_against_the_operation_definitions()
    {
        // Both directions. A set entry with no definition never appears in the catalogue, so the discovered
        // set loses it; a definition marked install-wide that the published set omits shows up on the other
        // side of the same equality.
        var discovered = PermissionVocabulary.Operations
            .Where(PermissionVocabulary.IsInstallWide)
            .Select(operation => operation.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var declared = PermissionVocabulary.InstallWideOperations
            .Select(operation => operation.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(discovered, declared);
    }

    [Fact]
    public void Every_install_wide_operation_has_a_founding_definition_at_the_install_root()
    {
        foreach (var operation in PermissionVocabulary.InstallWideOperations)
        {
            var definition = Assert.Single(
                AccessGrantAuthorizationSeed.FoundingDefinitions,
                d => d.Operation == operation);
            Assert.Equal("/", definition.Atom.Scope.ToString());
        }
    }
}
