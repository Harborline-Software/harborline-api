using System.Runtime.CompilerServices;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Definitions;

/// <summary>Authorized visibility changes with the captured audit in the same database commit.</summary>
public sealed class NodeDefinitionArchiveWriter(
    IDbContextFactory<LocalNodeDbContext> factory,
    AuthorizationGate gate,
    NodeAuditOutbox outbox,
    IWritePipelineObserver? observer = null) : IDisposable
{
    private readonly IDbContextFactory<LocalNodeDbContext> _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly AuthorizationGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    private readonly NodeAuditOutbox _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    private readonly SemaphoreSlim _mutation = new(1, 1);

    public ValueTask<DefinitionLifecycleKey?> SetFormArchivedAsync(
        AuthorizedFormDefinitionLifecycle forms, string id, bool archived,
        AuthorizationWriteContext authority, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(forms);
        return RunAsync(DefinitionKind.Forms, id, archived, authority, async token =>
        {
            await foreach (var revision in forms.ListByTenantAsync(authority.Tenant, token).ConfigureAwait(false))
                if (revision.Tenant == authority.Tenant && revision.Id.Value == id) return true;
            return false;
        }, ct);
    }

    public ValueTask<DefinitionLifecycleKey?> SetWorkflowArchivedAsync(
        AuthorizedWorkflowDefinitionLifecycle workflows, string id, bool archived,
        AuthorizationWriteContext authority, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        return RunAsync(DefinitionKind.Workflows, id, archived, authority, async token =>
        {
            await foreach (var revision in workflows.ListByTenantAsync(authority.Tenant, token).ConfigureAwait(false))
                if (revision.Tenant == authority.Tenant.Value && revision.Key == id) return true;
            return false;
        }, ct);
    }

    internal async ValueTask<HashSet<string>> ListArchivedAsync(TenantId tenant, DefinitionKind kind, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var keys = await db.Set<DefinitionArchiveMark>().AsNoTracking()
            .Where(row => row.TenantId == tenant.Value && row.Kind == kind)
            .Select(row => row.DefinitionKey).ToListAsync(ct).ConfigureAwait(false);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    private async ValueTask<DefinitionLifecycleKey?> RunAsync(
        DefinitionKind kind, string id, bool archived, AuthorizationWriteContext authority,
        Func<CancellationToken, ValueTask<bool>> exists, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A definition id is required.", nameof(id));
        var key = new DefinitionLifecycleKey(authority.Tenant.Value, kind, id);
        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await WritePipeline.RunAsync(new ArchiveCommand(this, key, archived, authority, exists), observer, ct)
                .ConfigureAwait(false);
        }
        finally { _mutation.Release(); }
    }

    private static DefinitionWriteTarget Target(DefinitionLifecycleKey key) => key.Kind switch
    {
        DefinitionKind.Forms => new(new TenantId(key.Tenant), "forms", Permission.FormsAuthor, key.DefinitionKey),
        DefinitionKind.Workflows => new(new TenantId(key.Tenant), "scheduling", Permission.SchedulingAuthor, key.DefinitionKey),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    public void Dispose() => _mutation.Dispose();

    private sealed class ArchiveCommand(
        NodeDefinitionArchiveWriter writer, DefinitionLifecycleKey key, bool archived,
        AuthorizationWriteContext authority, Func<CancellationToken, ValueTask<bool>> exists)
        : KernelWrite<DefinitionLifecycleKey, DefinitionLifecycleKey, DefinitionLifecycleKey, DefinitionLifecycleKey>
    {
        private AuthorizationDecision _decision = null!;

        protected override async ValueTask AuthorizeAsync(CancellationToken ct)
        {
            var target = Target(key);
            _decision = await writer._gate.DecideAsync(authority.Request(
                AuthorizationOperation.Parse(target.AuthorOperation), target.RecordKind, Uri.EscapeDataString(key.DefinitionKey)), ct)
                .ConfigureAwait(false);
            _decision.RequireAllowed();
            DefinitionWriteAdmission.Decide(_decision).Authorize(target);
        }

        protected override async ValueTask<DefinitionLifecycleKey?> BindAsync(CancellationToken ct) =>
            await exists(ct).ConfigureAwait(false) ? key : null;

        protected override ValueTask<DefinitionLifecycleKey> MutateAsync(DefinitionLifecycleKey bound, CancellationToken ct) =>
            ValueTask.FromResult(bound);

        protected override ValueTask<DefinitionLifecycleKey> ValidateAsync(
            DefinitionLifecycleKey bound, DefinitionLifecycleKey mutation, CancellationToken ct) => ValueTask.FromResult(mutation);

        protected override async ValueTask CommitAsync(DefinitionLifecycleKey validated, CancellationToken ct)
        {
            IDefinitionLifecycleStore store = new ArchiveStore(writer._factory, _decision, validated);
            if (archived) await store.ArchiveAsync(validated, ct).ConfigureAwait(false);
            else await store.UnarchiveAsync(validated, ct).ConfigureAwait(false);
        }

        protected override async ValueTask<DefinitionLifecycleKey> ReactAsync(DefinitionLifecycleKey committed, CancellationToken ct)
        {
            try { await writer._outbox.DrainAsync(ct).ConfigureAwait(false); }
            catch (Exception deferredAuditDelivery) when (deferredAuditDelivery is not OperationCanceledException)
            {
                _ = deferredAuditDelivery;
            }
            return committed;
        }
    }

    private sealed class ArchiveStore : IDefinitionLifecycleStore
    {
        private readonly IDbContextFactory<LocalNodeDbContext> _factory;
        private readonly AuthorizationDecision _decision;
        private readonly DefinitionLifecycleKey _expected;

        public ArchiveStore(IDbContextFactory<LocalNodeDbContext> factory,
            AuthorizationDecision decision, DefinitionLifecycleKey expected)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _decision = decision ?? throw new ArgumentNullException(nameof(decision));
            _expected = expected ?? throw new ArgumentNullException(nameof(expected));
            RequireKey(expected);
        }

        private void RequireKey(DefinitionLifecycleKey key)
        {
            if (key != _expected) throw new AuthorizationDeniedException(_decision);
            DefinitionWriteAdmission.Decide(_decision).Authorize(Target(key));
        }

        public ValueTask ArchiveAsync(DefinitionLifecycleKey key, CancellationToken cancellationToken = default) =>
            SetAsync(key, true, cancellationToken);
        public ValueTask UnarchiveAsync(DefinitionLifecycleKey key, CancellationToken cancellationToken = default) =>
            SetAsync(key, false, cancellationToken);

        private async ValueTask SetAsync(DefinitionLifecycleKey key, bool archived, CancellationToken ct)
        {
            RequireKey(key);
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var mark = await db.Set<DefinitionArchiveMark>().SingleOrDefaultAsync(row =>
                row.TenantId == key.Tenant && row.Kind == key.Kind && row.DefinitionKey == key.DefinitionKey, ct).ConfigureAwait(false);
            if ((mark is not null) == archived) return;
            if (archived) db.Set<DefinitionArchiveMark>().Add(new()
            {
                TenantId = key.Tenant, Kind = key.Kind, DefinitionKey = key.DefinitionKey,
            });
            else db.Remove(mark!);
            NodeAuditOutbox.StageAuthorized(db,
                new AuditEventType(archived ? "DefinitionArchived" : "DefinitionUnarchived"), _decision,
                new Dictionary<string, string?> { ["kind"] = key.Kind.ToString(), ["definition_key"] = key.DefinitionKey });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public async ValueTask<bool> IsArchivedAsync(DefinitionLifecycleKey key, CancellationToken cancellationToken = default)
        {
            RequireKey(key);
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await db.Set<DefinitionArchiveMark>().AnyAsync(row => row.TenantId == key.Tenant
                && row.Kind == key.Kind && row.DefinitionKey == key.DefinitionKey, cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<string> ListArchivedAsync(string tenant, DefinitionKind kind,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequireKey(new(tenant, kind, _expected.DefinitionKey));
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var keys = await db.Set<DefinitionArchiveMark>().AsNoTracking()
                .Where(row => row.TenantId == tenant && row.Kind == kind)
                .OrderBy(row => row.DefinitionKey).Select(row => row.DefinitionKey)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var key in keys) yield return key;
        }
    }
}
