using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.Foundation.Time;

namespace Harborline.Api.LocalNodeHost.Data.Configuration;

/// <summary>
/// DES-0029 kernel-core-ck-6: delivers the configuration activation evidence outbox to the kernel audit trail in the
/// running host. Activation commits each row with the authority its decision captured (T-644); this drain appends
/// every owed row once, under an audit id derived from its tenant and evidence intent, so a crash between the
/// append and the mark is found on the trail and only marked. A failed delivery records its error and stays owed.
/// </summary>
/// <remarks>
/// The offline <c>recover-configuration</c> verb delivers a tenant's stranded rows through
/// <see cref="AppendOwedAsync"/> and marks them in its recovery transaction with <see cref="MarkPublishedAsync"/>, as
/// this drain does. Rows committed before the authority column existed carry no snapshot: nothing is appended for
/// them, and the verb's recovery audit names them as the evidence it stands in for.
/// </remarks>
[ClockAuthority("Out-of-act evidence outbox: dates its own publication, not the activation act.")]
public sealed class ConfigurationEvidenceOutbox(
    IDbContextFactory<NodeLocalPacksDbContext> factory,
    IAuditTrail trail,
    ICapturedAuditTrail captured,
    IOperationSigner signer,
    TimeProvider time,
    ILogger<ConfigurationEvidenceOutbox> logger) : IDisposable
{
    // Single-flight: the post-activation drain and the daemon's tick share this singleton, and the
    // hold-check-then-append below is not atomic, so two overlapping passes could append one entry twice.
    // ponytail: in-process gate; a second host process on the same store would need a row claim instead.
    private readonly SemaphoreSlim _drain = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _drain.Dispose();

    /// <summary>The captured authority of the decision that admitted an activation, stored on its outbox row.</summary>
    internal static string Capture(AuthorizationDecision decision)
    {
        var request = decision.Request;
        return JsonSerializer.Serialize(CapturedAuditAuthority.Capture(request.Tenant, new ActorId(request.Principal.Value),
            request.At, request.Target, request.Act, decision));
    }

    /// <summary>The publish step this drain and the offline recovery share: the owed row is marked at <paramref name="at"/>.</summary>
    internal static async ValueTask<ConfigurationEvidenceOutboxRow> MarkPublishedAsync(NodeLocalPacksDbContext context, string tenant,
        string intentId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var row = await context.EvidenceOutbox.FindAsync([tenant, intentId], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("configuration-outbox-entry-moved");
        row.PublishedAt = at;
        row.LastError = null;
        return row;
    }

    /// <summary>The trail audit id of one activation's evidence, stable across drains and restarts.</summary>
    internal static Guid AuditIdFor(string tenant, string intentId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("configuration.activated\n" + tenant + "\n" + intentId)).AsSpan(0, 16));

    /// <summary>
    /// Appends each owed row of <paramref name="tenant"/> that carries a captured authority, under the drain's gate
    /// and audit id, and marks none: the offline recovery marks them in its own transaction.
    /// </summary>
    internal async Task AppendOwedAsync(string tenant, CancellationToken cancellationToken)
    {
        await _drain.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var row in await OwedAsync(tenant, cancellationToken).ConfigureAwait(false))
                await EnsureOnTrailAsync(row, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _drain.Release();
        }
    }

    /// <summary>Delivers every owed row in commit order.</summary>
    /// <returns>The number of rows delivered or found delivered by this pass.</returns>
    public async Task<int> DrainAsync(CancellationToken cancellationToken = default)
    {
        await _drain.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DrainOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _drain.Release();
        }
    }

    /// <summary>
    /// Delivers one activation's row, if it is still owed, under the drain's gate: the post-commit path of that
    /// activation. The owed backlog stays with <see cref="ConfigurationEvidenceDrainDaemon"/>, so an activation
    /// after a trail outage does not wait on every earlier row.
    /// </summary>
    public async Task DeliverAsync(string tenant, string intentId, CancellationToken cancellationToken = default)
    {
        await _drain.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await context.EvidenceOutbox.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Tenant == tenant && r.IntentId == intentId && r.PublishedAt == null && r.AuthoritySnapshotJson != null,
                    cancellationToken).ConfigureAwait(false);
            if (row is not null) await DeliverRowAsync(row, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _drain.Release();
        }
    }

    private async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        var delivered = 0;
        foreach (var row in await OwedAsync(tenant: null, cancellationToken).ConfigureAwait(false))
            if (await DeliverRowAsync(row, cancellationToken).ConfigureAwait(false)) delivered++;
        return delivered;
    }

    /// <summary>Appends and marks one owed row; a failure is logged and recorded on the row, which stays owed.</summary>
    private async Task<bool> DeliverRowAsync(ConfigurationEvidenceOutboxRow row, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureOnTrailAsync(row, cancellationToken).ConfigureAwait(false);
            await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await MarkPublishedAsync(context, row.Tenant, row.IntentId, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Configuration activation evidence {IntentId} (tenant {Tenant}) is still owed to the audit trail.",
                row.IntentId, row.Tenant);
            try
            {
                await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await context.EvidenceOutbox.Where(r => r.Tenant == row.Tenant && r.IntentId == row.IntentId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.LastError, exception.Message), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception markFailure) when (markFailure is not OperationCanceledException)
            {
                // Best effort: the row stays owed either way, and throwing would end the pass before later rows.
                logger.LogError(markFailure, "Configuration activation evidence {IntentId} could not record its delivery failure.", row.IntentId);
            }
            return false;
        }
    }

    /// <summary>The unpublished rows that carry a captured authority, in commit order, for one tenant or all.</summary>
    private async Task<List<ConfigurationEvidenceOutboxRow>> OwedAsync(string? tenant, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var owed = await context.EvidenceOutbox.AsNoTracking()
            .Where(row => row.PublishedAt == null && row.AuthoritySnapshotJson != null && (tenant == null || row.Tenant == tenant))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return owed.OrderBy(row => row.CommittedAt).ToList();
    }

    /// <summary>Appends the row's entry unless the trail already holds its audit id, so a retried delivery appends once.</summary>
    private async Task EnsureOnTrailAsync(ConfigurationEvidenceOutboxRow row, CancellationToken cancellationToken)
    {
        var tenant = new TenantId(row.Tenant);
        var auditId = AuditIdFor(row.Tenant, row.IntentId);
        if (!await HoldsAsync(tenant, auditId, cancellationToken).ConfigureAwait(false))
            await AppendAsync(row, tenant, auditId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HoldsAsync(TenantId tenant, Guid auditId, CancellationToken cancellationToken)
    {
        await foreach (var _ in trail.QueryAsync(new AuditQuery(tenant, AuditId: auditId), cancellationToken).ConfigureAwait(false))
            return true;
        return false;
    }

    private async Task AppendAsync(ConfigurationEvidenceOutboxRow row, TenantId tenant, Guid auditId, CancellationToken cancellationToken)
    {
        var snapshot = JsonSerializer.Deserialize<AuthoritySnapshot>(row.AuthoritySnapshotJson!)
            ?? throw new InvalidOperationException("configuration-evidence-authority-empty");
        // The entry the inline pack-install adapter wrote for an activation, now addressed at the install-wide act
        // the decision admitted, so the captured authority matches its header.
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action"] = PackInstallAuditAction.Activated.ToString(),
            ["packKey"] = "configuration-generation",
            ["version"] = row.NewDigest,
            ["detail"] = $"configuration.activated:{row.IntentId}:{row.PriorDigest}->{row.NewDigest}:{row.DecisionId}",
            ["preDecision"] = false,
        };
        var payload = await signer.SignAsync(new AuditPayload(body), row.CommittedAt, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        await captured.AppendCapturedAsync(new AuditRecord(auditId, tenant, KernelAuditPackInstallAudit.PackInstallEventType,
            row.CommittedAt, payload, [],
            Actor: new ActorId(snapshot.Principal!),
            Target: new AuthorizationTarget(string.Empty, string.Empty, ScopeExpression.Parse("/")),
            Act: new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), ScopeExpression.Parse("/")),
            AuthoritySnapshot: snapshot), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Drains the configuration evidence outbox on an interval, so evidence whose first delivery failed is delivered later.</summary>
public sealed class ConfigurationEvidenceDrainDaemon(ConfigurationEvidenceOutbox outbox, TimeProvider time,
    ILogger<ConfigurationEvidenceDrainDaemon> logger) : BackgroundService
{
    /// <summary>The drain interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await outbox.DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Configuration evidence drain failed; it will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
