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
    internal static AuthorizationGate AllowAll() => Granting(ScopeExpression.Parse("/"));

    /// <summary>A gate granting nothing — every act refuses fail-closed.</summary>
    internal static AuthorizationGate Denying() => Granting();

    /// <summary>A gate granting every pack operation ONLY within the named packs' record scopes.</summary>
    internal static AuthorizationGate ScopedTo(params string[] packKeys) => Granting(
        (packKeys ?? Array.Empty<string>())
            .Select(key => ScopeExpression.Parse($"/records/{key}"))
            .ToArray());

    private static AuthorizationGate Granting(params ScopeExpression[] scopes)
    {
        var source = new ScopedGrantSource(scopes);
        return new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
    }

    private sealed class ScopedGrantSource(IReadOnlyList<ScopeExpression> grantScopes) :
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
            var derivations = new List<AuthorizationAtomDerivation>(grantScopes.Count);
            foreach (var grantScope in grantScopes)
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
