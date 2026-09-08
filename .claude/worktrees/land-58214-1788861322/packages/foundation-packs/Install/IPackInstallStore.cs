using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>
/// The durable install-state store for the Pack Composer install engine (B-1b). Persists, per tenant:
/// the immutable pack seed layers (ADR 0101 F2), the S-8 monotonic version + floor watermark per pack
/// key, the tenant override rows, and the append-only nothing-here (audit lives on the
/// <see cref="Audit.IPackInstallAudit"/> port).
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomic apply (S-7).</b> <see cref="IPackInstallMutationStore.Commit"/> is the transactional boundary: it either applies the
/// ENTIRE <see cref="PackInstallTransaction"/> (new seed layer + watermark + re-attached overrides +
/// lifecycle) or leaves the store completely unchanged. A truncated file / yanked USB / mid-apply kill
/// must never leave a partial seed layer — so an implementation MUST NOT mutate any observable state
/// until it can complete the whole transaction (the in-memory reference-swap default; a durable adapter
/// uses a DB transaction).
/// </para>
/// <para>
/// <b>Immutable seeds (F2).</b> A committed <see cref="InstalledPack"/> seed layer is never mutated in
/// place; an upgrade installs a NEW version's seed layer beside the prior one, and activation is a
/// pointer flip (S-2 — deactivation/rollback never deletes a seed layer or a tenant override).
/// </para>
/// </remarks>
public interface IPackInstallStore
{
    /// <summary>The currently Active version for a pack key (the version the cascade resolves), or null
    /// if the pack is not installed / has no Active version.</summary>
    InstalledPack? GetActive(TenantId tenant, string packKey);

    /// <summary>A specific installed version (any lifecycle), or null.</summary>
    InstalledPack? GetVersion(TenantId tenant, string packKey, string version);

    /// <summary>Every installed pack version for the tenant (all lifecycles), stable order.</summary>
    IReadOnlyList<InstalledPack> ListInstalled(TenantId tenant);

    /// <summary>The S-8 monotonic watermark for a pack key, or null if never installed.</summary>
    PackInstallWatermark? GetWatermark(TenantId tenant, string packKey);

    /// <summary>The tenant override rows for a pack key (empty if none).</summary>
    IReadOnlyList<PackTenantOverride> GetOverrides(TenantId tenant, string packKey);

    /// <summary>
    /// The recorded per-key owning-pack choices for the tenant (ADR 0129 D4/D5 / D8): a map of
    /// <c>contentKey → owning pack key</c> for cross-pack same-key collisions the client resolved
    /// explicitly. Empty when nothing was resolved. This is the persisted authority the seed projector
    /// honors so a re-activation / restart projects the SAME chosen owner (never re-litigates silently).
    /// </summary>
    IReadOnlyDictionary<string, string> GetKeyOwnership(TenantId tenant);

}

/// <summary>Unregistered raw install-state persistence held only by <see cref="PackInstaller"/>.</summary>
public interface IPackInstallMutationStore : IPackInstallStore
{
    void SaveOverride(TenantId tenant, string packKey, PackTenantOverride tenantOverride);
    void Commit(PackInstallTransaction transaction);
    void Activate(TenantId tenant, string packKey, string version);
    void Deactivate(TenantId tenant, string packKey, string version);
    void RecordKeyOwnership(TenantId tenant, string contentKey, string owningPackKey);
}

/// <summary>Durable admission evidence for one pack lifecycle transition.</summary>
public sealed record PackProjectionAdmission
{
    public PackProjectionAdmission(
        Guid AdmissionId,
        string PackId,
        string PackVersion,
        TenantId Tenant,
        ActorId Principal,
        DateTimeOffset Instant,
        IReadOnlyList<string> DerivationIds,
        bool Projected)
    {
        this.AdmissionId = AdmissionId;
        this.PackId = PackId;
        this.PackVersion = PackVersion;
        this.Tenant = Tenant;
        this.Principal = Principal;
        this.Instant = Instant;
        this.DerivationIds = DerivationIds;
        this.Projected = Projected;
    }

    public Guid AdmissionId { get; init; }
    public string PackId { get; init; }
    public string PackVersion { get; init; }
    public TenantId Tenant { get; init; }
    public ActorId Principal { get; init; }
    public DateTimeOffset Instant { get; init; }
    public IReadOnlyList<string> DerivationIds { get; init; }
    public bool Projected { get; init; }
}

/// <summary>Installer-only persistence seam for atomic lifecycle admission and reconciliation.</summary>
public interface IPackProjectionAdmissionStore
{
    void ActivateAndRecordProjectionAdmission(
        TenantId tenant, string packKey, string version, PackProjectionAdmission admission);

    void DeactivateAndRecordProjectionAdmission(
        TenantId tenant, string packKey, string version, PackProjectionAdmission admission);

    IReadOnlyList<PackProjectionAdmission> ListIncompleteProjectionAdmissions();

    void MarkProjectionCompleted(Guid admissionId);
}

/// <summary>A lifecycle transition was rejected because its required persisted state was absent.</summary>
public sealed class PackTransitionStateException(string message) : InvalidOperationException(message);

/// <summary>
/// The atomic unit an install/upgrade commits (S-7). Carries the new immutable seed layer, the advanced
/// S-8 watermark, and the tenant overrides re-attached onto the new version (S-10). All applied together
/// or not at all.
/// </summary>
/// <param name="Tenant">The tenant the install is scoped to.</param>
/// <param name="InstalledPack">The new seed layer (committed in <see cref="PackLifecycleState.Draft"/>).</param>
/// <param name="Watermark">The advanced monotonic version + floor watermark for the pack key.</param>
/// <param name="ReattachedOverrides">The tenant overrides re-attached onto the new version's content keys
/// (S-10 — every prior override either re-attaches here or is surfaced as a conflict, never dropped).</param>
public sealed record PackInstallTransaction(
    TenantId Tenant,
    InstalledPack InstalledPack,
    PackInstallWatermark Watermark,
    IReadOnlyList<PackTenantOverride> ReattachedOverrides);
