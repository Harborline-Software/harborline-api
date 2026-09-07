namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// Digest-only authority to begin one account-recovery ceremony (MTW-2 #3013, ADR 0160 R3-E purpose
/// 3, D3). Unlike an <see cref="AccountSetupInvitationRecord"/> — which reserves NO account and
/// creates a NEW joiner on redemption — a recovery invitation PINS an EXISTING target installation
/// account and, on redemption, rotates that account's credential and revokes every account session
/// across all tenants. It creates no account, principal, Party, trust, roster, membership, or grant.
///
/// It is a deliberately separate record/table from <see cref="AccountSetupInvitationRecord"/> (whose
/// DB check-constraint pins <c>purpose = 'AccountSetup'</c> and whose columns are inviter-centric with
/// no target account). Structural separation is what makes the recovery code purpose-bound: a recovery
/// digest lives only in this table, so it can never be consumed by the account-setup / login paths and
/// vice-versa (the red-fixture purpose-binding invariant). The raw 256-bit code is returned from
/// issuance exactly once and never enters the EF model — only its SHA-256 digest is persisted.
///
/// <para><b>FENCE — read before adding a second writer (ADR 0160 A1-2, earlier repository ticket #3256).</b> Table
/// separation discriminates purpose only while this table has exactly ONE writer. ADR 0160 D3
/// specifies a second recovery issuance path (the offline, OS-bound ceremony) that is not built. The
/// change that builds it MUST, in that same change, either mint into its own table or add a purpose
/// column here plus a fence predicate at every consume site with a red fixture proving a
/// cross-purpose digest is refused. Adding the writer first and fencing later silently un-enforces a
/// security property everyone believes is checked.</para>
/// </summary>
public sealed class RecoveryInvitationRecord
{
    public required string RecoveryInvitationId { get; set; }

    /// <summary>The tenant whose <c>members:manage</c> administrator issued this recovery invitation
    /// (the authority scope + audit anchor). Recovery's EFFECT is account-wide across all tenants;
    /// this field records only who was authorized to start it.</summary>
    public required string TenantId { get; set; }

    public required string IssuerAccountId { get; set; }

    public required string IssuerPrincipalId { get; set; }

    /// <summary>The EXISTING installation account this recovery targets. Redemption rotates THIS
    /// account's credential; it is never derived from redeemer-supplied input.</summary>
    public required string TargetAccountId { get; set; }

    /// <summary>The target account's normalized username, pinned at issuance so redemption can confirm
    /// the intended account without revealing any other account (non-enumerating).</summary>
    public required string TargetNormalizedUsername { get; set; }

    public required string TokenDigest { get; set; }

    public required string CommandFingerprint { get; set; }

    public DateTimeOffset IssuedAtUtc { get; set; }

    public DateTimeOffset AbsoluteExpiresAtUtc { get; set; }

    /// <summary>Set at first consume. Marks the recovery code single-use across restart.</summary>
    public DateTimeOffset? ConsumedAtUtc { get; set; }

    /// <summary>
    /// The digest of the new credential hash the redeemer committed to at first consume. A resume of a
    /// consumed-but-incomplete recovery (F2 — a transient failure after consume must not re-consume)
    /// must present the SAME new credential; a different one is a changed-replay and is refused. Null
    /// until first consume.
    /// </summary>
    public string? CredentialCommitmentDigest { get; set; }

    /// <summary>
    /// Set only when the recovery has fully succeeded — credential rotated + every account session
    /// across all tenants revoked. Until it is set, a consumed recovery invitation is RESUMABLE
    /// (F2); once set, any replay of the code is refused.
    /// </summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public long OwnerVersion { get; set; }
}
