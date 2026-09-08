using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Data.Packs;

/// <summary>
/// The DURABLE <see cref="IPackInstallStore"/> — the SQLCipher-backed replacement for the v1
/// <c>InMemoryPackInstallStore</c> (F5; migration-update-architecture D5.2 / D5.3). An installed + activated pack's
/// version + lifecycle, the S-8 monotonic watermark, the tenant overrides, and the per-key owning-pack choices are
/// persisted to the recoverable <see cref="NodeLocalPacksDbContext"/> so they SURVIVE a node restart /
/// <c>deploy-dogfood</c> redeploy: boot re-projects the still-installed + still-active packs through the EXISTING
/// <c>PackSeedProjector</c> path — no from-empty re-seed, no lost watermark (the DOGFOOD.md "re-run
/// seed-general-pack.ps1 after every restart" caveat this closes).
/// </summary>
/// <remarks>
/// <para>
/// <b>The engine enforces the invariants; this store only persists.</b> <see cref="PackInstaller"/> runs the fixed
/// fail-closed pipeline (S-7 verify-before-effect → S-11 revocation → S-13 scope → S-8 watermark → S-9 admission →
/// S-10 total re-attach) and only then calls <see cref="Commit"/>. This store's obligations are narrow and durable:
/// <list type="bullet">
///   <item><b>S-7 atomic apply.</b> <see cref="Commit"/> wraps the new seed layer + advanced watermark + re-attached
///     overrides in ONE explicit DB transaction — a truncated write / yanked power leaves the prior state fully
///     intact, never a partial seed layer (the durable analogue of the in-memory reference-swap).</item>
///   <item><b>S-2 immutable seeds / pointer-flip.</b> <see cref="Activate"/> flips only the authoritative
///     <c>lifecycle</c> column; it never rewrites or deletes a seed-layer payload, so a supersede is a pure pointer
///     flip and a rollback within the ADR 0011 window is loadable from the retained row.</item>
///   <item><b>S-8 durable watermark.</b> <see cref="GetWatermark"/> reads the persisted monotonic version + floor
///     set, so the downgrade / floor-weakening refusal keeps its teeth across the very updates it polices.</item>
///   <item><b>S-10 durable overrides.</b> <see cref="GetOverrides"/> reads the persisted tenant overlay patches, so
///     the next upgrade's three-way re-attach runs against real prior state, not an empty set.</item>
/// </list>
/// </para>
/// <para>
/// <b>Synchronous interface over async EF — by design.</b> <see cref="IPackInstallStore"/> is deliberately
/// synchronous (the installer calls it inline, and install/activate/deactivate is a LOW-frequency, human-paced
/// operation — a handful of packs, never a hot path). Each call opens a short-lived context via the factory and runs the EF
/// round-trip synchronously. A single process-wide lock serialises every operation so a read never observes a
/// half-applied commit and — because it prevents concurrent SQLite connections — it also side-steps SQLite
/// writer-lock contention. This mirrors the <see cref="Admission.DurableAdmissionTokenStore"/> single-gate model.
/// </para>
/// </remarks>
public sealed class DurablePackInstallStore : IPackInstallStore, IPackInstallMutationStore, IPackProjectionAdmissionStore
{
    private const int LifecycleActive = (int)PackLifecycleState.Active;
    private const int LifecycleSuperseded = (int)PackLifecycleState.Superseded;
    private const int LifecycleInactive = (int)PackLifecycleState.Inactive;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    private readonly object _gate = new();
    private readonly IDbContextFactory<NodeLocalPacksDbContext> _factory;

    /// <summary>Construct over the SQLCipher-keyed packs DbContext factory (registered by
    /// <c>AddSqlCipherLocalNodeDbContext</c>).</summary>
    public DurablePackInstallStore(IDbContextFactory<NodeLocalPacksDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public InstalledPack? GetActive(TenantId tenant, string packKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        var t = tenant.Value;
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var row = ctx.InstalledVersions.AsNoTracking()
                .FirstOrDefault(r => r.Tenant == t && r.PackKey == packKey && r.Lifecycle == LifecycleActive);
            return row is null ? null : Materialize(row);
        }
    }

    /// <inheritdoc />
    public InstalledPack? GetVersion(TenantId tenant, string packKey, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var t = tenant.Value;
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var row = ctx.InstalledVersions.AsNoTracking()
                .FirstOrDefault(r => r.Tenant == t && r.PackKey == packKey && r.Version == version);
            return row is null ? null : Materialize(row);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<InstalledPack> ListInstalled(TenantId tenant)
    {
        var t = tenant.Value;
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var rows = ctx.InstalledVersions.AsNoTracking().Where(r => r.Tenant == t).ToList();
            // Match the in-memory store's stable ORDINAL order (SQLite's default collation is byte-wise but the
            // ordering is asserted here in-memory so it is provider-independent).
            return rows
                .Select(Materialize)
                .OrderBy(p => p.PackKey, StringComparer.Ordinal)
                .ThenBy(p => p.Version, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <inheritdoc />
    public PackInstallWatermark? GetWatermark(TenantId tenant, string packKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        var t = tenant.Value;
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var row = ctx.Watermarks.AsNoTracking()
                .FirstOrDefault(r => r.Tenant == t && r.PackKey == packKey);
            if (row is null)
            {
                return null;
            }

            var floors = JsonSerializer.Deserialize<Dictionary<string, int>>(row.FloorsJson, Json)
                ?? new Dictionary<string, int>(StringComparer.Ordinal);
            return new PackInstallWatermark(row.PackKey, row.Version, floors);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PackTenantOverride> GetOverrides(TenantId tenant, string packKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        var t = tenant.Value;
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var rows = ctx.Overrides.AsNoTracking()
                .Where(r => r.Tenant == t && r.PackKey == packKey)
                .ToList();
            return rows
                .OrderBy(r => r.ContentKey, StringComparer.Ordinal)
                .Select(r => new PackTenantOverride(r.ContentKey, ParseOverlay(r.ContentKey, r.OverlayJson)))
                .ToList();
        }
    }

    /// <inheritdoc />
    public void SaveOverride(TenantId tenant, string packKey, PackTenantOverride tenantOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        ArgumentNullException.ThrowIfNull(tenantOverride);
        var t = tenant.Value;
        var json = tenantOverride.OverlayPatch.ToJsonString();
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var row = ctx.Overrides.Find(t, packKey, tenantOverride.ContentKey);
            if (row is null)
            {
                ctx.Overrides.Add(new PackTenantOverrideRow
                {
                    Tenant = t,
                    PackKey = packKey,
                    ContentKey = tenantOverride.ContentKey,
                    OverlayJson = json,
                });
            }
            else
            {
                // An override on the same content key REPLACES the prior one (matches the in-memory contract).
                row.OverlayJson = json;
            }

            ctx.SaveChanges();
        }
    }

    /// <inheritdoc />
    public void Commit(PackInstallTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var pack = transaction.InstalledPack;
        var t = transaction.Tenant.Value;
        var payload = JsonSerializer.Serialize(pack, Json);
        var floorsJson = JsonSerializer.Serialize(transaction.Watermark.Floors, Json);

        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            // S-7 ATOMIC apply: the new seed layer + advanced watermark + re-attached overrides commit
            // all-or-nothing inside ONE explicit transaction. A fault before Commit rolls the whole thing back
            // (the durable analogue of the in-memory single reference-swap).
            using var tx = ctx.Database.BeginTransaction();

            // (1) Upsert the new immutable seed-layer version row (committed in the pack's own lifecycle — Draft
            //     at install; activation is a separate pointer flip). The payload is the FULL InstalledPack JSON.
            var versionRow = ctx.InstalledVersions.Find(t, pack.PackKey, pack.Version);
            if (versionRow is null)
            {
                ctx.InstalledVersions.Add(new PackInstalledVersionRow
                {
                    Tenant = t,
                    PackKey = pack.PackKey,
                    Version = pack.Version,
                    Lifecycle = (int)pack.Lifecycle,
                    PayloadJson = payload,
                });
            }
            else
            {
                versionRow.Lifecycle = (int)pack.Lifecycle;
                versionRow.PayloadJson = payload;
            }

            // (2) Upsert the S-8 monotonic watermark for the pack key.
            var wmRow = ctx.Watermarks.Find(t, pack.PackKey);
            if (wmRow is null)
            {
                ctx.Watermarks.Add(new PackWatermarkRow
                {
                    Tenant = t,
                    PackKey = pack.PackKey,
                    Version = transaction.Watermark.Version,
                    FloorsJson = floorsJson,
                });
            }
            else
            {
                wmRow.Version = transaction.Watermark.Version;
                wmRow.FloorsJson = floorsJson;
            }

            // (3) REPLACE the pack key's overrides with the S-10 re-attached set. Delete-then-insert: the delete is
            //     flushed FIRST (a distinct SaveChanges) so a re-attached override reusing a prior content key never
            //     collides with the row being removed on insert. Both flushes are inside the same transaction.
            var existing = ctx.Overrides.Where(o => o.Tenant == t && o.PackKey == pack.PackKey).ToList();
            ctx.Overrides.RemoveRange(existing);
            ctx.SaveChanges();

            foreach (var ov in transaction.ReattachedOverrides)
            {
                ctx.Overrides.Add(new PackTenantOverrideRow
                {
                    Tenant = t,
                    PackKey = pack.PackKey,
                    ContentKey = ov.ContentKey,
                    OverlayJson = ov.OverlayPatch.ToJsonString(),
                });
            }

            ctx.SaveChanges();
            tx.Commit();
        }
    }

    /// <inheritdoc />
    public void Activate(TenantId tenant, string packKey, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var t = tenant.Value;

        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            StageActivation(ctx, t, packKey, version);
            ctx.SaveChanges();
        }
    }

    void IPackProjectionAdmissionStore.ActivateAndRecordProjectionAdmission(
        TenantId tenant, string packKey, string version, PackProjectionAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ValidateAdmissionCoordinates(admission, tenant, packKey, version);
        var t = tenant.Value;
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            HomeEpochFenceTransaction.RunAsync(ctx, async () =>
            {
                StageActivation(ctx, t, packKey, version);
                AddProjectionAdmission(ctx, admission);
                await ctx.SaveChangesAsync().ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public void Deactivate(TenantId tenant, string packKey, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var t = tenant.Value;

        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            StageDeactivation(ctx, t, packKey, version);
            ctx.SaveChanges();
        }
    }

    void IPackProjectionAdmissionStore.DeactivateAndRecordProjectionAdmission(
        TenantId tenant, string packKey, string version, PackProjectionAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ValidateAdmissionCoordinates(admission, tenant, packKey, version);
        var t = tenant.Value;
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            HomeEpochFenceTransaction.RunAsync(ctx, async () =>
            {
                StageDeactivation(ctx, t, packKey, version);
                AddProjectionAdmission(ctx, admission);
                await ctx.SaveChangesAsync().ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }
    }

    IReadOnlyList<PackProjectionAdmission> IPackProjectionAdmissionStore.ListIncompleteProjectionAdmissions()
    {
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            return ctx.ProjectionAdmissions.AsNoTracking()
                .Where(row => !row.Projected)
                .AsEnumerable()
                .OrderBy(row => row.Tenant, StringComparer.Ordinal)
                .ThenBy(row => row.Instant)
                .ThenBy(row => row.AdmissionId)
                .Select(row => new PackProjectionAdmission(
                    row.AdmissionId,
                    row.PackId,
                    row.PackVersion,
                    new TenantId(row.Tenant),
                    new ActorId(row.Principal),
                    row.Instant,
                    JsonSerializer.Deserialize<string[]>(row.DerivationIdsJson, Json) ?? [],
                    row.Projected))
                .ToArray();
        }
    }

    void IPackProjectionAdmissionStore.MarkProjectionCompleted(Guid admissionId)
    {
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var row = ctx.ProjectionAdmissions.Find(admissionId)
                ?? throw new InvalidOperationException("Pack projection admission evidence was not found.");
            row.Projected = true;
            ctx.SaveChanges();
        }
    }

    private static void StageActivation(
        NodeLocalPacksDbContext ctx, string tenant, string packKey, string version)
    {
        var target = ctx.InstalledVersions.Find(tenant, packKey, version)
            ?? throw new PackTransitionStateException(
                $"Pack version '{packKey}@{version}' is not installed; cannot activate.");
        var priorActive = ctx.InstalledVersions
            .Where(row => row.Tenant == tenant && row.PackKey == packKey
                && row.Lifecycle == LifecycleActive && row.Version != version)
            .ToList();
        foreach (var prior in priorActive)
            prior.Lifecycle = LifecycleSuperseded;
        target.Lifecycle = LifecycleActive;
    }

    private static void StageDeactivation(
        NodeLocalPacksDbContext ctx, string tenant, string packKey, string version)
    {
        var target = ctx.InstalledVersions.Find(tenant, packKey, version);
        if (target is null || target.Lifecycle != LifecycleActive)
        {
            throw new PackTransitionStateException(
                $"Pack version '{packKey}@{version}' is not Active; cannot deactivate.");
        }
        target.Lifecycle = LifecycleInactive;
    }

    private static void AddProjectionAdmission(
        NodeLocalPacksDbContext ctx, PackProjectionAdmission admission)
    {
        if (ctx.ProjectionAdmissions.Find(admission.AdmissionId) is not null)
            throw new InvalidOperationException("A pack projection admission id cannot be reused.");
        ctx.ProjectionAdmissions.Add(new PackProjectionAdmissionRow
        {
            AdmissionId = admission.AdmissionId,
            Tenant = admission.Tenant.Value,
            PackId = admission.PackId,
            PackVersion = admission.PackVersion,
            Principal = admission.Principal.Value,
            Instant = admission.Instant,
            DerivationIdsJson = JsonSerializer.Serialize(admission.DerivationIds, Json),
            Projected = admission.Projected,
        });
    }

    private static void ValidateAdmissionCoordinates(
        PackProjectionAdmission admission, TenantId tenant, string packKey, string version)
    {
        if (admission.Tenant != tenant
            || !string.Equals(admission.PackId, packKey, StringComparison.Ordinal)
            || !string.Equals(admission.PackVersion, version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The pack projection admission does not match the transition.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetKeyOwnership(TenantId tenant)
    {
        var t = tenant.Value;
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var rows = ctx.KeyOwnership.AsNoTracking().Where(r => r.Tenant == t).ToList();
            return rows.ToDictionary(r => r.ContentKey, r => r.OwningPackKey, StringComparer.Ordinal);
        }
    }

    /// <inheritdoc />
    public void RecordKeyOwnership(TenantId tenant, string contentKey, string owningPackKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(owningPackKey);
        var t = tenant.Value;
        lock (_gate)
        {
            using var ctx = _factory.CreateDbContext();
            var row = ctx.KeyOwnership.Find(t, contentKey);
            if (row is null)
            {
                ctx.KeyOwnership.Add(new PackKeyOwnershipRow
                {
                    Tenant = t,
                    ContentKey = contentKey,
                    OwningPackKey = owningPackKey,
                });
            }
            else
            {
                // A later choice on the same key REPLACES the prior one (matches the in-memory contract).
                row.OwningPackKey = owningPackKey;
            }

            ctx.SaveChanges();
        }
    }

    /// <summary>
    /// Materialize a version row into an <see cref="InstalledPack"/>. The immutable seed-layer JSON is
    /// deserialized as-is; the pack's lifecycle is then OVERWRITTEN from the authoritative <c>lifecycle</c> column
    /// (activation flips that column without rewriting the payload, so the column — not the embedded JSON — is the
    /// source of truth for lifecycle).
    /// </summary>
    private static InstalledPack Materialize(PackInstalledVersionRow row)
    {
        var pack = JsonSerializer.Deserialize<InstalledPack>(row.PayloadJson, Json)
            ?? throw new InvalidOperationException(
                $"Installed pack '{row.PackKey}@{row.Version}' payload deserialized to null.");
        return pack with { Lifecycle = (PackLifecycleState)row.Lifecycle };
    }

    private static JsonNode ParseOverlay(string contentKey, string overlayJson)
        => JsonNode.Parse(overlayJson)
           ?? throw new InvalidOperationException($"Override '{contentKey}' overlay JSON parsed to null.");
}
