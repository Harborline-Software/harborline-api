namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// Centralized, wire-stable event-type names for the durable identity audit taxonomy. Each value is
/// the exact string persisted in the <c>EventType</c> column of the chain that owns it; the hash
/// chain and every replay check bind to it, so changing a value is a wire break and an audit-history
/// break — it must never happen. New durable identity events add a constant here instead of an
/// inline literal, so the taxonomy has one home.
/// </summary>
/// <remarks>
/// This class single-sources the NAMES; it does NOT imply a single chain. Most values are persisted
/// in <see cref="InstallationAuditEnvelopeRecord.EventType"/> (the installation-identity chain), but
/// <see cref="SessionEstablished"/> and <see cref="SessionRevoked"/> are persisted on the SEPARATE
/// per-tenant chain (<c>TenantMembershipAuthorityStore.TenantIdentityAuditEnvelopeDocument</c>).
/// Check a constant's own remarks before appending it: passing a tenant-chain value to an
/// installation-chain append would write the event onto the wrong chain.
/// </remarks>
internal static class InstallationIdentityAuditEventTypes
{
    /// <summary>
    /// Genesis founder-bootstrap event. Reused as the durable "founder bound" record emitted by the
    /// dormant installation-founder bootstrap ceremony. Value is wire-stable.
    /// </summary>
    internal const string FounderBootstrapped = "InstallationFounderBootstrapped";

    /// <summary>The single-use installer claim became the first ordinary Administrator grant.</summary>
    internal const string BootstrapClaimRedeemed = "InstallationBootstrapClaimRedeemed";

    /// <summary>
    /// Web selected-session establishment (tenant selection finalized). Reused wire-stable emit
    /// owned by <c>TenantMembershipAuthorityStore</c>; named here so the taxonomy is single-sourced.
    /// </summary>
    /// <remarks>
    /// TENANT CHAIN, not the installation chain: this value is persisted in the <c>EventType</c> of
    /// <c>TenantMembershipAuthorityStore.TenantIdentityAuditEnvelopeDocument</c>, never in
    /// <see cref="InstallationAuditEnvelopeRecord.EventType"/>. Do not append it to the
    /// installation-identity audit chain.
    /// </remarks>
    internal const string SessionEstablished = "WebTenantSelected";

    /// <summary>
    /// Web user-session revocation. Reused wire-stable emit owned by
    /// <c>TenantMembershipAuthorityStore</c>; named here so the taxonomy is single-sourced.
    /// </summary>
    /// <remarks>
    /// TENANT CHAIN, not the installation chain: this value is persisted in the <c>EventType</c> of
    /// <c>TenantMembershipAuthorityStore.TenantIdentityAuditEnvelopeDocument</c>, never in
    /// <see cref="InstallationAuditEnvelopeRecord.EventType"/>. Do not append it to the
    /// installation-identity audit chain.
    /// </remarks>
    internal const string SessionRevoked = "WebUserSessionRevoked";

    /// <summary>
    /// Newly-wired durable seam: an installation-root (founder) binding was designated by
    /// <see cref="InstallationFounderBindingService"/> (the now-live founder-bind route).
    /// </summary>
    internal const string FounderBindingDesignated = "InstallationFounderBindingDesignated";

    /// <summary>Newly-wired durable seam: an account-setup invitation was issued.</summary>
    internal const string InvitationIssued = "InstallationInvitationIssued";

    /// <summary>Newly-wired durable seam: an account-setup invitation was accepted (consumed).</summary>
    internal const string InvitationAccepted = "InstallationInvitationAccepted";

    /// <summary>
    /// An account-setup invitation reached its fixed username-conflict disclosure ceiling and was
    /// consumed without accepting an account.
    /// </summary>
    internal const string InvitationConflictLimitReached =
        "InstallationInvitationConflictLimitReached";

    /// <summary>
    /// Newly-wired durable seam (MTW-2 #2614): a web-plane joiner installation account was minted
    /// from an accepted account-setup invitation. Value is wire-stable.
    /// </summary>
    internal const string AccountAdmitted = "InstallationAccountAdmitted";

    /// <summary>Durable contact creation evidence emitted by the node-local People write path.</summary>
    internal const string ContactCreated = "People.PartyCreated";

    /// <summary>
    /// Newly-wired durable seam (MTW-2 #3013): a recovery invitation was issued by a tenant
    /// <c>members:manage</c> administrator, targeting an EXISTING installation account. Value is
    /// wire-stable.
    /// </summary>
    internal const string RecoveryInvitationIssued = "InstallationRecoveryInvitationIssued";

    /// <summary>
    /// Newly-wired durable seam (MTW-2 #3013): an installation account credential was recovered
    /// (rotated) via an accepted recovery invitation, advancing the account security version and
    /// revoking every account session across all tenants (ADR 0160 R3-E/R3-F, D3). The recovery path
    /// emits exactly ONE aggregate <c>CredentialRecovered</c> envelope for the credential change and
    /// stages the swept sessions as durable revocation tombstones; it does NOT emit a per-session
    /// <see cref="SessionRevoked"/> event. Value is wire-stable.
    /// </summary>
    internal const string CredentialRecovered = "InstallationCredentialRecovered";
}
