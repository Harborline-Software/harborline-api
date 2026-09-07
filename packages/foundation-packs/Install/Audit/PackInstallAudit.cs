using System.Collections.Concurrent;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Packs.Install.Audit;

/// <summary>The audited install-engine mutation an entry records.</summary>
public enum PackInstallAuditAction
{
    /// <summary>A first install of a pack key created a new immutable seed layer (Draft).</summary>
    Installed = 0,

    /// <summary>An upgrade installed a newer version's seed layer beside the prior one (Draft).</summary>
    Upgraded = 1,

    /// <summary>An installed version was activated (Draft/Inactive → Active pointer flip, ADR 0011).</summary>
    Activated = 2,

    /// <summary>A break-glass ceremony overrode an S-8 refusal (downgrade / floor-weakening). The
    /// distinct, loud audit signal — a break-glass is NEVER a silent flag (S-8).</summary>
    BreakGlassOverride = 3,

    /// <summary>An install was REFUSED (verify/revocation/scope/watermark/admission) — the refusal is
    /// auditable too, so an attempted bad install is observable.</summary>
    Refused = 4,

    /// <summary>An Active pack version was reversibly deactivated; no seed layer or tenant data was deleted.</summary>
    Deactivated = 5,

    /// <summary>An administrator NARROWED one content key through the ordinary tenant-override overlay
    /// (ticket 208 L624). The seed layer is untouched; the overlay is what changes.</summary>
    Narrowed = 6,
}

/// <summary>
/// One durable audit-envelope entry for an install-engine mutation (design §6 / the B-1b obligation that
/// closes B-1a's structured-log-only floor). Every install / upgrade / activate / deactivate / break-glass /
/// refusal records one of these; the host binds a durable, signed adapter (kernel-audit <c>IAuditTrail</c>).
/// </summary>
/// <param name="Tenant">The tenant the mutation is scoped to.</param>
/// <param name="Action">The mutation kind.</param>
/// <param name="PackKey">The pack key.</param>
/// <param name="Version">The pack version.</param>
/// <param name="OccurredAtUtc">When the mutation occurred.</param>
/// <param name="SignerKeyId">The signer the pack verified against (provenance), when known.</param>
/// <param name="Epoch">The signing epoch, when known.</param>
/// <param name="Detail">A stable code / short description of the outcome (e.g. a refusal code).</param>
/// <param name="BreakGlassJustification">The operator's justification — REQUIRED on
/// <see cref="PackInstallAuditAction.BreakGlassOverride"/> (the ceremony), null otherwise.</param>
/// <param name="BreakGlassAuthorizingPrincipal">The principal who authorized the break-glass, null
/// otherwise.</param>
/// <param name="ActingPrincipal">WHO performed the mutation — the server-derived acting principal the
/// install/activate/deactivate context carried (ticket 151 requires it for every mutation; refusal rows
/// record whatever the refused request carried, which is null exactly when the refusal IS the missing
/// principal).</param>
/// <param name="PreDecision">True only when the attempt was refused before an allowed authorization
/// decision existed (blank coordinates/principal or a denied gate decision). Such rows use the ordinary
/// append lane and therefore carry no authority snapshot.</param>
public sealed record PackInstallAuditEntry(
    TenantId Tenant,
    PackInstallAuditAction Action,
    string PackKey,
    string Version,
    DateTimeOffset OccurredAtUtc,
    PrincipalId? SignerKeyId,
    long? Epoch,
    string? Detail,
    string? BreakGlassJustification = null,
    string? BreakGlassAuthorizingPrincipal = null,
    string? ActingPrincipal = null,
    bool PreDecision = false)
{
    /// <summary>The actor described by this attempt, independently of any authorization decision.</summary>
    public ActorId Actor => string.IsNullOrWhiteSpace(ActingPrincipal)
        ? ActorId.System
        : new ActorId(ActingPrincipal);

    /// <summary>The package record described by this entry, independently of any decision.</summary>
    public AuthorizationTarget Target
    {
        get
        {
            var id = string.IsNullOrWhiteSpace(PackKey) ? "(blank)" : PackKey;
            var scope = ScopeExpression.Parse($"/records/{id}");
            return new AuthorizationTarget("pack", id, scope);
        }
    }

    /// <summary>The operation this package entry records, independently of any decision.</summary>
    public PermissionAtom Act => new(
        AuthorizationOperation.Parse(Permission.PackagesOperate),
        Target.Scope);
}

/// <summary>
/// The durable audit sink for install-engine mutations. The install engine appends one
/// <see cref="PackInstallAuditEntry"/> per mutation (and per refusal); the host binds a durable, signed
/// adapter over the unified kernel audit trail. Append MUST be durable — this is the envelope that
/// replaces B-1a's structured-log floor.
/// </summary>
public interface IPackInstallAudit
{
    /// <summary>Durably records a refusal that occurred before an allowed decision existed.</summary>
    void Append(PackInstallAuditEntry entry);

    /// <summary>Durably records one install-engine audit entry with the exact decision that admitted it.</summary>
    void AppendAuthorized(PackInstallAuditEntry entry, AuthorizationDecision decision);

    /// <summary>The recorded entries for a tenant, in append order (for list surfaces + tests).</summary>
    IReadOnlyList<PackInstallAuditEntry> Query(TenantId tenant);
}

/// <summary>
/// An in-memory append-only <see cref="IPackInstallAudit"/> — the v1 default + test substrate. A durable
/// host adapter (kernel-audit) replaces it in production; the seam + entry shape are identical.
/// </summary>
public sealed class InMemoryPackInstallAudit : IPackInstallAudit
{
    private readonly ConcurrentQueue<PackInstallAuditEntry> _entries = new();

    /// <inheritdoc />
    public void Append(PackInstallAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.PreDecision)
            throw new ArgumentException("Ordinary pack audit entries must be flagged preDecision.", nameof(entry));
        _entries.Enqueue(entry);
    }

    /// <inheritdoc />
    public void AppendAuthorized(PackInstallAuditEntry entry, AuthorizationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(decision);
        if (entry.PreDecision)
            throw new ArgumentException("An authorized pack audit entry cannot be flagged preDecision.", nameof(entry));
        decision.RequireAllowed();
        _entries.Enqueue(entry);
    }

    /// <inheritdoc />
    public IReadOnlyList<PackInstallAuditEntry> Query(TenantId tenant)
        => _entries.Where(e => e.Tenant.Equals(tenant)).ToList();
}
