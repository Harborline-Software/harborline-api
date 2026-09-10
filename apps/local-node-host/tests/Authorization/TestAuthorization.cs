using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Blocks.Workflow.Durable;
using System.Reflection;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

internal static class TestAuthorization
{
    internal static readonly DateTimeOffset At =
        new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    internal static AuthorizationGate AllowGate() => Gate(true);

    internal static RoleGateAdmission RoleGate() =>
        new(new InMemoryRoleVocabulary([AccessGrantAuthorizationSeed.MemberDefinition]));

    internal static AuthorizedFormDefinitionLifecycle FormLifecycle(
        IFormDefinitionStore store,
        AuthorizationGate gate,
        IRoleGateAdmission admission,
        IFormDefinitionLegalHoldValidator? legalHold = null) => store switch
        {
            EntityStoreFormDefinitionStore entity => new(
                entity,
                EntityStore(entity),
                EntityTime(entity),
                gate,
                admission,
                legalHold),
            InMemoryFormDefinitionStore memory => new(
                memory,
                memory.PersistenceHandle,
                gate,
                admission,
                legalHold),
            _ => throw new ArgumentException("Unsupported test form store.", nameof(store)),
        };

    internal static AuthorizedWorkflowDefinitionLifecycle WorkflowLifecycle(
        IWorkflowDefinitionStore store,
        AuthorizationGate gate,
        IRoleGateAdmission admission)
    {
        var entity = Assert.IsType<EntityStoreWorkflowDefinitionStore>(store);
        return new AuthorizedWorkflowDefinitionLifecycle(
            entity,
            EntityStore(entity),
            Field<IWorkflowAdmissionValidator>(entity, "_admission"),
            EntityTime(entity),
            gate,
            admission);
    }

    private static IEntityMutationStore EntityStore(object store) =>
        (IEntityMutationStore)(store.GetType().BaseType!
            .GetProperty("Mutations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)
            ?? throw new InvalidOperationException("Test store has no entity backing."));

    private static TimeProvider EntityTime(object store) =>
        Field<TimeProvider>(store, "_time", store.GetType().BaseType!);

    private static T Field<T>(object instance, string name, Type? declaringType = null) =>
        (T)((declaringType ?? instance.GetType())
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)
            ?? throw new InvalidOperationException($"Test store has no '{name}' field."));

    internal static AuthorizationGate Gate(
        bool allowed,
        Action<AuthorizationGateRequest>? observed = null) =>
        Gate(_ => allowed, observed);

    /// <summary>
    /// A gate that answers per REQUEST rather than uniformly — the shape a test needs when two principals
    /// must get different verdicts for the same act (ticket 272 slice 4: the overriding party is resolved by
    /// putting it to this gate).
    /// </summary>
    internal static AuthorizationGate Gate(
        Func<AuthorizationGateRequest, bool> allow,
        Action<AuthorizationGateRequest>? observed = null)
    {
        var source = new ConfigurableAuthorizationSource(allow, observed);
        return new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
    }

    /// <summary>
    /// A gate that decides from CONFERRED GRANTS rather than echoing the requested act. Ticket 293 slice 4:
    /// the roster supplies no deciding set any more, so a fixture whose subject must hold a REAL per-principal
    /// install-root set — to enumerate candidates through <c>InstallRootPermissionsAsync</c>, to satisfy a
    /// <c>RequiredPermissions</c> clause, or to give two principals different verdicts for one act — has to
    /// hand the gate that set. <paramref name="conferred"/> stands in for the closure the admission's own
    /// conferral writes: the atoms are the principal's permissions at the install root.
    /// </summary>
    internal static AuthorizationGate ConferredGate(Func<ActorId, PermissionSet> conferred)
    {
        ArgumentNullException.ThrowIfNull(conferred);
        var source = new ConferredGrantAuthorizationSource(conferred);
        return new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
    }

    internal static AuthorizationWriteContext FormWrite(
        CapabilityToken token,
        FormDefinitionId form,
        DateTimeOffset at) => new(
        token.Subject,
        token.Tenant,
        at);

    internal static AuthorizationWriteContext Write(
        TenantId tenant,
        string principal = "test-operator",
        DateTimeOffset? at = null) =>
        new(new ActorId(principal), tenant, at ?? At);

    internal static AuthorizationDecision AllowedDecision(
        TenantId tenant,
        string recordId,
        string recordKind = "record",
        string operation = TeamRolePermissions.RecordsWrite,
        string principal = "test-operator",
        DateTimeOffset? at = null)
    {
        var authority = Write(tenant, principal, at);
        return AllowGate().DecideAsync(
                authority.Request(AuthorizationOperation.Parse(operation), recordKind, recordId))
            .AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Derives one Administrator-role atom per conferred permission, at the install root, for the principal
    /// the request names. The definition side answers with the SAME atoms so the gate's atom and named-role
    /// readings cannot diverge.
    /// </summary>
    private sealed class ConferredGrantAuthorizationSource(Func<ActorId, PermissionSet> conferred) :
        IAuthorizationClosureSnapshotReader,
        IAuthorizationDefinitionAtomReader
    {
        private IReadOnlyList<PermissionAtom> lastAtoms = Array.Empty<PermissionAtom>();

        private static IReadOnlyList<PermissionAtom> AtomsOf(PermissionSet set) =>
            set.Permissions
                .Select(operation => PermissionAtom.Parse($"{operation}@/"))
                .ToArray();

        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(
            AuthorizationGateRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(request);
            var atoms = AtomsOf(conferred(request.Principal) ?? PermissionSet.Empty);
            lastAtoms = atoms;
            IReadOnlyList<AuthorizationAtomDerivation> derivations = atoms
                .Select(atom => new AuthorizationAtomDerivation(
                    atom,
                    RoleReference.Administrator,
                    "conferred-grant",
                    1,
                    "conferred-definition",
                    ScopeExpression.Parse("/"),
                    request.At.AddMinutes(-1),
                    null))
                .ToArray();
            return ValueTask.FromResult(new AuthorizationClosureSnapshot(derivations));
        }

        public ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(
            TenantId tenantId,
            RoleReference role,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = tenantId;
            return ValueTask.FromResult(role == RoleReference.Administrator
                ? lastAtoms
                : Array.Empty<PermissionAtom>());
        }
    }

    private sealed class ConfigurableAuthorizationSource(
        Func<AuthorizationGateRequest, bool> allow,
        Action<AuthorizationGateRequest>? observed) :
        IAuthorizationClosureSnapshotReader,
        IAuthorizationDefinitionAtomReader
    {
        private PermissionAtom? requested;
        private bool allowed;

        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(
            AuthorizationGateRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            observed?.Invoke(request);
            requested = request.Act;
            allowed = allow(request);
            IReadOnlyList<AuthorizationAtomDerivation> derivations = allowed
                ?
                [
                    new AuthorizationAtomDerivation(
                        request.Act,
                        RoleReference.Administrator,
                        "test-grant",
                        1,
                        "test-definition",
                        request.Target.Scope,
                        request.At.AddMinutes(-1),
                        null),
                ]
                : Array.Empty<AuthorizationAtomDerivation>();
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
            IReadOnlyList<PermissionAtom> atoms = allowed && requested is { } atom
                ? [atom]
                : Array.Empty<PermissionAtom>();
            return ValueTask.FromResult(atoms);
        }
    }
}
