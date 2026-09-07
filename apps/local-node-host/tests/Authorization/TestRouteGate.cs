using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// A real <see cref="AuthorizationGate"/> for the record-scoped route-family tests (ticket 205 slice 4),
/// backed by grants at NAMED SCOPES.
/// </summary>
/// <remarks>
/// <para>
/// The refusal is decided by the gate's own scope containment — a grant at <c>/records/inv-a</c> does not
/// cover an act at <c>/records/inv-b</c> — so a scope test here exercises the production resolution rather
/// than a test double's opinion. This is the same construction <c>TestPackGate</c> uses for the pack family
/// in slice 3, generalised over the operation predicate so a host that already flips a mutable permission
/// set keeps ONE source of truth for what the caller holds.
/// </para>
/// <para>
/// <see cref="Following"/> takes the predicate the host's existing authorization double answers with;
/// <see cref="ScopedTo"/> narrows the grant to named records, which is what makes an out-of-scope refusal
/// observable.
/// </para>
/// </remarks>
internal static class TestRouteGate
{
    /// <summary>The install root — a grant here covers every record scope.</summary>
    internal static ScopeExpression InstallRoot { get; } = ScopeExpression.Parse("/");

    /// <summary>A gate granting, at the install root, exactly the operations <paramref name="holds"/>
    /// admits.</summary>
    internal static AuthorizationGate Following(Func<string, bool> holds) =>
        Granting(request => holds(request.Act.Operation.Value));

    /// <summary>
    /// A gate whose holdings depend on WHO is acting — <c>holds(principal, operation)</c>. A fixture with
    /// two planes (the desktop operator and a signed-in web member) needs this: the two callers do not hold
    /// the same operations, and a gate that ignored the principal could not tell them apart.
    /// </summary>
    internal static AuthorizationGate Following(Func<string, string, bool> holds)
    {
        ArgumentNullException.ThrowIfNull(holds);
        return Granting(request => holds(request.Principal.Value, request.Act.Operation.Value));
    }

    /// <summary>A gate granting every operation at the install root.</summary>
    internal static AuthorizationGate AllowAll() => Granting(_ => true);

    /// <summary>A gate granting nothing — every act refuses fail-closed.</summary>
    internal static AuthorizationGate Denying() => Granting(_ => false);

    /// <summary>A gate granting every operation ONLY within the named records' scopes.</summary>
    internal static AuthorizationGate ScopedTo(params string[] recordIds) => Granting(
        _ => true,
        (recordIds ?? []).Select(id => ScopeExpression.Parse($"/records/{id}")).ToArray());

    /// <summary>
    /// A gate granting every operation ONLY within ONE named record's canonical scope, for a record kind
    /// other than <c>records</c> (ticket 205 slice 5 needs a <c>financial</c> period target). The scope is
    /// computed by the production scope math — <see cref="AuthorizationWriteContext.Request"/> — so an
    /// out-of-scope refusal here is the gate's own containment refusing, not a spelled-out string.
    /// </summary>
    /// <param name="tenant">The tenant the act is scoped to.</param>
    /// <param name="recordKind">The record kind the operation targets.</param>
    /// <param name="recordId">The one record the grant covers.</param>
    internal static AuthorizationGate ScopedToRecord(TenantId tenant, string recordKind, string recordId)
    {
        var scope = new AuthorizationWriteContext(new ActorId("test-grant-subject"), tenant, DateTimeOffset.UnixEpoch)
            .Request(AuthorizationOperation.Parse("records:read"), recordKind, recordId)
            .Target.Scope;
        return Granting(_ => true, scope);
    }

    /// <summary>
    /// Builds the gate. With no <paramref name="scopes"/> the grant is issued at the REQUEST's own target
    /// scope — the shape <c>TestAuthorization.Gate</c> uses, and the one that leaves scope out of the
    /// question when the test is about the holding. Naming scopes is what makes an out-of-scope refusal
    /// observable.
    /// </summary>
    private static AuthorizationGate Granting(
        Func<AuthorizationGateRequest, bool> holds,
        params ScopeExpression[] scopes)
    {
        ArgumentNullException.ThrowIfNull(holds);
        var source = new ScopedGrantSource(holds, scopes);
        return new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
    }

    private sealed class ScopedGrantSource(
        Func<AuthorizationGateRequest, bool> holds,
        IReadOnlyList<ScopeExpression> grantScopes) :
        IAuthorizationClosureSnapshotReader,
        IAuthorizationDefinitionAtomReader
    {
        private readonly List<PermissionAtom> _issued = [];

        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(
            AuthorizationGateRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _issued.Clear();
            var scopes = grantScopes.Count == 0 ? [request.Target.Scope] : grantScopes;
            var derivations = new List<AuthorizationAtomDerivation>(scopes.Count);
            if (holds(request))
            {
                foreach (var grantScope in scopes)
                {
                    var atom = new PermissionAtom(request.Act.Operation, grantScope);
                    _issued.Add(atom);
                    derivations.Add(new AuthorizationAtomDerivation(
                        atom,
                        RoleReference.Administrator,
                        "test-grant",
                        1,
                        "test-definition",
                        grantScope,
                        request.At.AddMinutes(-1),
                        null));
                }
            }

            return ValueTask.FromResult(new AuthorizationClosureSnapshot(derivations));
        }

        public ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(
            TenantId tenantId,
            RoleReference role,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = tenantId;
            _ = role;
            return ValueTask.FromResult<IReadOnlyList<PermissionAtom>>(_issued.ToArray());
        }
    }
}
