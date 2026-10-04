using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// A real <see cref="AuthorizationGate"/> for the <c>/packs/*</c> and <c>/channels/*</c> route tests
/// (ticket 205 slice 3), backed by grants at NAMED SCOPES rather than a permission-string set.
/// </summary>
/// <remarks>
/// The refusal is decided by the gate's own scope containment — a grant at <c>/records/pack-a</c> simply
/// does not cover an act at <c>/records/pack-b</c> — so a scope test here exercises the production
/// resolution and not a test double's opinion. <see cref="ScopedTo"/> grants only the named packs;
/// <see cref="AllowAll"/> grants at the install root (which covers every record scope, and is the shape an
/// install-wide act needs); <see cref="Denying"/> grants nothing.
/// </remarks>
internal static class TestPackGate
{
    /// <summary>A gate granting every pack operation at the install root.</summary>
    internal static AuthorizationGate AllowAll(Action<string>? observe = null) =>
        GrantingObserved(observe, ScopeExpression.Parse("/"));

    /// <summary>A gate granting nothing — every act refuses fail-closed.</summary>
    internal static AuthorizationGate Denying() => Granting();

    /// <summary>
    /// A gate granting ONE operation at the install root and refusing every other (T-668). The
    /// configuration entries do not share a permission, so proving each is scoped to its own needs a
    /// holder of one who is not a holder of the other.
    /// </summary>
    internal static AuthorizationGate Only(AuthorizationOperation operation)
    {
        var source = new ScopedGrantSource([ScopeExpression.Parse("/")], operation);
        return new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
    }

    /// <summary>A gate granting every pack operation ONLY within the named packs' record scopes.</summary>
    internal static AuthorizationGate ScopedTo(params string[] packKeys) => Granting(
        (packKeys ?? Array.Empty<string>())
            .Select(key => ScopeExpression.Parse($"/records/{key}"))
            .ToArray());

    private static AuthorizationGate Granting(params ScopeExpression[] scopes) => GrantingObserved(null, scopes);

    private static AuthorizationGate GrantingObserved(Action<string>? observe, params ScopeExpression[] scopes)
    {
        var source = new ScopedGrantSource(scopes, observe: observe);
        return new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
    }

    private sealed class ScopedGrantSource(
        IReadOnlyList<ScopeExpression> grantScopes,
        AuthorizationOperation? onlyOperation = null,
        Action<string>? observe = null) :
        IAuthorizationClosureSnapshotReader,
        IAuthorizationDefinitionAtomReader
    {
        private readonly List<PermissionAtom> _issued = [];

        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(
            AuthorizationGateRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            observe?.Invoke("authorization-snapshot-start");
            _issued.Clear();
            var derivations = new List<AuthorizationAtomDerivation>(grantScopes.Count);
            IReadOnlyList<ScopeExpression> scopes =
                onlyOperation is { } only && !string.Equals(only.Value, request.Act.Operation.Value, StringComparison.Ordinal)
                    ? []
                    : grantScopes;
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

            observe?.Invoke("authorization-snapshot-complete");
            return ValueTask.FromResult(new AuthorizationClosureSnapshot(derivations));
        }

        public ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(
            TenantId tenantId,
            RoleReference role,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            observe?.Invoke("authorization-atoms-start");
            _ = tenantId;
            _ = role;
            observe?.Invoke("authorization-atoms-complete");
            return ValueTask.FromResult<IReadOnlyList<PermissionAtom>>(_issued.ToArray());
        }
    }
}
