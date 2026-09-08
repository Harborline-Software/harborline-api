using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Governance.Consent;

/// <summary>
/// The fail-closed default <see cref="ITenantConsentStore"/> — no tenant has any consent record on file, so
/// every subject-consent act refuses as <see cref="ConsentRefusal.NoRecord"/>. This is the SAFE default when
/// a composition wires no durable consent store: a <c>Consent</c>-effect field is blocked rather than
/// silently permitted.
/// </summary>
/// <remarks>
/// It replaces the old <c>DenyAllConsentGate</c> null object (ticket 213). The fail-closed default belongs at
/// the STORE layer now, not the gate layer: there is exactly ONE consent gate — the one that reads records —
/// so no composition can end up with a gate that decides by some other reading. Writing here is refused
/// loudly rather than dropped: a transition that cannot be persisted must not appear to have happened.
/// </remarks>
public sealed class NoConsentRecordsStore : ITenantConsentStore
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TenantConsentRecord>> ReadAsync(
        TenantId tenant, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<TenantConsentRecord>>([]);

    /// <inheritdoc />
    public ValueTask SaveAsync(TenantConsentRecord record, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "No durable consent store is wired: a consent record cannot be written. Register an "
            + "ITenantConsentStore (the node registers FileTenantConsentStore) before recording consent.");

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TenantId>> TenantsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<TenantId>>([]);
}
