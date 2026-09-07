using System.Data;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

internal sealed record AccountSetupInvitationSeed(
    string TenantId,
    string InviterAccountId,
    string InviterPrincipalId,
    string InviterPartyId,
    string InviterSessionCorrelationId,
    string InviterMembershipId,
    long InviterMembershipOwnerVersion,
    string InviterGrantId,
    long InviterGrantOwnerVersion,
    long InviterAuthorizationEpoch,
    string RequestedPermissionsJson,
    string CommandFingerprint,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc);

internal sealed record AccountSetupInvitationIssueResult(
    string InvitationId,
    string RawCode,
    string TenantId,
    DateTimeOffset AbsoluteExpiresAtUtc);

/// <summary>
/// A single still-pending (unconsumed, unrevoked, unexpired) AccountSetup invitation, projected for the
/// admin Team &amp; access surface (MTW-2 #2617). Carries only non-secret listing fields — never the token
/// digest or any inviter credential — so it is safe to return from the admin members:manage-gated read.
/// </summary>
internal sealed record PendingAccountSetupInvitation(
    string InvitationId,
    string InviterPartyId,
    string RequestedPermissionsJson,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc);

/// <summary>
/// The server-resolved inviter pins recovered when an AccountSetup invitation is consumed. Returned by
/// <see cref="AccountSetupInvitationStore.ConsumeAndReadAsync"/> so the acceptance saga (MTW-2 #2614) can
/// build the <c>AdmissionCompleted</c> contract + run the inviter mandate-attenuation gate from the
/// SIGNED invitation record — never from browser-supplied request input.
/// </summary>
internal sealed record AccountSetupInvitationConsumeResult(
    string InvitationId,
    string TenantId,
    string InviterAccountId,
    string InviterPrincipalId,
    string InviterPartyId,
    string RequestedPermissionsJson,
    string CommandFingerprint,
    long OwnerVersion);

/// <summary>
/// Persists only a digest of each 256-bit setup code. Raw material is returned from issue exactly
/// once and never enters the EF model.
/// </summary>
internal sealed class AccountSetupInvitationStore(
    IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory)
{
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    public async Task<AccountSetupInvitationIssueResult?> IssueAsync(
        AccountSetupInvitationSeed seed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        Validate(seed);

        var rawCode = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var row = new AccountSetupInvitationRecord
        {
            InvitationId = RandomHex(32),
            TenantId = seed.TenantId,
            InviterAccountId = seed.InviterAccountId,
            InviterPrincipalId = seed.InviterPrincipalId,
            InviterPartyId = seed.InviterPartyId,
            InviterSessionCorrelationId = seed.InviterSessionCorrelationId,
            InviterMembershipId = seed.InviterMembershipId,
            InviterMembershipOwnerVersion = seed.InviterMembershipOwnerVersion,
            InviterGrantId = seed.InviterGrantId,
            InviterGrantOwnerVersion = seed.InviterGrantOwnerVersion,
            InviterAuthorizationEpoch = seed.InviterAuthorizationEpoch,
            RequestedPermissionsJson = seed.RequestedPermissionsJson,
            TokenDigest = Digest(rawCode),
            Purpose = WebSetupInvitationPurpose.AccountSetup,
            CommandFingerprint = seed.CommandFingerprint,
            IssuedAtUtc = seed.IssuedAtUtc,
            AbsoluteExpiresAtUtc = seed.AbsoluteExpiresAtUtc,
            ConsumedAtUtc = null,
            RevokedAtUtc = null,
            OwnerVersion = 1,
        };

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        context.AccountSetupInvitations.Add(row);

        // Durable, tamper-evident audit appended to the existing installation chain in the SAME
        // transaction as the invitation row, so the paper trail cannot diverge from the mutation.
        await InstallationIdentityAuditChain.AppendEventAsync(
            context,
            InstallationIdentityAuditEventTypes.InvitationIssued,
            actorKind: "installation-account",
            actorId: seed.InviterAccountId,
            correlationId: InstallationAuditIntegrity.Hash(
                "installation-invitation-issued-audit/v1", row.InvitationId),
            commandFingerprint: seed.CommandFingerprint,
            payloadDigest: InstallationAuditIntegrity.Hash(
                row.InvitationId,
                seed.TenantId,
                seed.InviterAccountId,
                seed.InviterPartyId,
                seed.RequestedPermissionsJson),
            occurredAtUtc: seed.IssuedAtUtc,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AccountSetupInvitationIssueResult(
                row.InvitationId,
                rawCode,
                row.TenantId,
                row.AbsoluteExpiresAtUtc);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Lists every still-pending AccountSetup invitation for <paramref name="tenantId"/> — unconsumed,
    /// unrevoked, and not yet at absolute expiry as of <paramref name="now"/>. Read-only projection for the
    /// admin Team &amp; access surface (#2617); the caller's members:manage authority is enforced upstream.
    /// Never returns the token digest or any secret. Ordered by issue time (newest first) for stable UI.
    /// </summary>
    public async Task<IReadOnlyList<PendingAccountSetupInvitation>> ListPendingAsync(
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await context.AccountSetupInvitations.AsNoTracking()
            .Where(item =>
                item.TenantId == tenantId &&
                item.Purpose == WebSetupInvitationPurpose.AccountSetup &&
                item.ConsumedAtUtc == null &&
                item.RevokedAtUtc == null &&
                item.IssuedAtUtc <= now &&
                item.AbsoluteExpiresAtUtc > now)
            .OrderByDescending(item => item.IssuedAtUtc)
            .Select(item => new PendingAccountSetupInvitation(
                item.InvitationId,
                item.InviterPartyId,
                item.RequestedPermissionsJson,
                item.IssuedAtUtc,
                item.AbsoluteExpiresAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows;
    }

    public async Task<bool> RevokeAsync(
        string invitationId,
        string tenantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var row = await context.AccountSetupInvitations.SingleOrDefaultAsync(item =>
                item.InvitationId == invitationId &&
                item.TenantId == tenantId &&
                item.Purpose == WebSetupInvitationPurpose.AccountSetup,
            cancellationToken).ConfigureAwait(false);
        if (row is null || row.ConsumedAtUtc is not null || row.RevokedAtUtc is not null ||
            now < row.IssuedAtUtc || now >= row.AbsoluteExpiresAtUtc)
        {
            return false;
        }

        row.RevokedAtUtc = now;
        row.OwnerVersion = checked(row.OwnerVersion + 1);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    public async Task<bool> ConsumeAsync(
        string rawCode,
        string tenantId,
        WebSetupInvitationPurpose expectedPurpose,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await ConsumeAndReadAsync(rawCode, tenantId, expectedPurpose, now, cancellationToken)
            .ConfigureAwait(false) is not null;

    /// <summary>
    /// Reads the server-pinned facts from a valid, still-pending invitation without consuming it.
    /// The acceptance saga uses this narrow preflight to verify the inviter mandate and try the
    /// username/account mint before making the token's irreversible state transition.
    /// </summary>
    public async Task<AccountSetupInvitationConsumeResult?> ReadPendingAsync(
        string rawCode,
        string tenantId,
        WebSetupInvitationPurpose expectedPurpose,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (expectedPurpose != WebSetupInvitationPurpose.AccountSetup)
        {
            return null;
        }

        var digest = Digest(rawCode);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await context.AccountSetupInvitations.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                    item.TokenDigest == digest &&
                    item.TenantId == tenantId &&
                    item.Purpose == expectedPurpose,
                cancellationToken)
            .ConfigureAwait(false);
        return IsUsable(row, now) ? Project(row!) : null;
    }

    /// <summary>
    /// Records one username-conflict disclosure against the invitation's existing durable owner
    /// version. While a row is pending, owner version 1 is the issued state and each subsequent
    /// version represents one disclosed conflict. No second counter or migration is needed because
    /// consumption/revocation end the only state in which the count is interpreted.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the conflict was recorded. The final permitted disclosure also
    /// consumes the invitation. <see langword="false"/> means the invitation was no longer the valid
    /// unconsumed version the caller preflighted, so no conflict answer may be exposed.
    /// </returns>
    public async Task<bool> RecordUsernameConflictAsync(
        string rawCode,
        string tenantId,
        WebSetupInvitationPurpose expectedPurpose,
        long expectedOwnerVersion,
        int maximumDisclosures,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (expectedPurpose != WebSetupInvitationPurpose.AccountSetup ||
            expectedOwnerVersion <= 0 ||
            maximumDisclosures <= 0)
        {
            return false;
        }

        var digest = Digest(rawCode);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var row = await context.AccountSetupInvitations.SingleOrDefaultAsync(item =>
                item.TokenDigest == digest &&
                item.TenantId == tenantId &&
                item.Purpose == expectedPurpose,
            cancellationToken).ConfigureAwait(false);
        if (!IsUsable(row, now) || row!.OwnerVersion != expectedOwnerVersion)
        {
            return false;
        }

        row.OwnerVersion = checked(row.OwnerVersion + 1);
        var disclosedConflicts = row.OwnerVersion - 1;
        if (disclosedConflicts >= maximumDisclosures)
        {
            row.ConsumedAtUtc = now;
            await InstallationIdentityAuditChain.AppendEventAsync(
                    context,
                    InstallationIdentityAuditEventTypes.InvitationConflictLimitReached,
                    actorKind: "installation-invitation",
                    actorId: row.InvitationId,
                    correlationId: InstallationAuditIntegrity.Hash(
                        "installation-invitation-conflict-limit-audit/v1", row.InvitationId),
                    commandFingerprint: row.CommandFingerprint,
                    payloadDigest: InstallationAuditIntegrity.Hash(
                        row.InvitationId,
                        row.TenantId,
                        maximumDisclosures.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    occurredAtUtc: now,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Atomically consumes a single-use AccountSetup invitation (the same serializable, single-use, TTL,
    /// tamper-evident-audit path as <see cref="ConsumeAsync"/>) AND returns the server-resolved inviter
    /// pins recovered from the signed record. The acceptance saga (MTW-2 #2614) needs the pins — inviter
    /// principal, tenant, requested permissions — to build the <c>AdmissionCompleted</c> contract and run
    /// the inviter mandate-attenuation gate from the record, not from browser input. Returns
    /// <c>null</c> fail-closed when the code is unknown, already consumed/revoked, expired, or not yet
    /// valid — the exact non-enumerating refusal envelope <see cref="ConsumeAsync"/> uses.
    /// </summary>
    public async Task<AccountSetupInvitationConsumeResult?> ConsumeAndReadAsync(
        string rawCode,
        string tenantId,
        WebSetupInvitationPurpose expectedPurpose,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (expectedPurpose != WebSetupInvitationPurpose.AccountSetup)
        {
            return null;
        }

        var digest = Digest(rawCode);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        var row = await context.AccountSetupInvitations.SingleOrDefaultAsync(item =>
                item.TokenDigest == digest &&
                item.TenantId == tenantId &&
                item.Purpose == expectedPurpose,
            cancellationToken).ConfigureAwait(false);
        if (!IsUsable(row, now))
        {
            return null;
        }

        row!.ConsumedAtUtc = now;
        row.OwnerVersion = checked(row.OwnerVersion + 1);

        // Durable, tamper-evident audit appended to the existing installation chain in the SAME
        // transaction as the consumption, so acceptance always leaves a permanent record.
        await InstallationIdentityAuditChain.AppendEventAsync(
            context,
            InstallationIdentityAuditEventTypes.InvitationAccepted,
            actorKind: "installation-invitation",
            actorId: row.InvitationId,
            correlationId: InstallationAuditIntegrity.Hash(
                "installation-invitation-accepted-audit/v1", row.InvitationId),
            commandFingerprint: row.CommandFingerprint,
            payloadDigest: InstallationAuditIntegrity.Hash(
                row.InvitationId,
                row.TenantId,
                row.InviterAccountId,
                row.TokenDigest),
            occurredAtUtc: now,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Project(row);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    internal static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsUsable(AccountSetupInvitationRecord? row, DateTimeOffset now) =>
        row is not null &&
        row.ConsumedAtUtc is null &&
        row.RevokedAtUtc is null &&
        now >= row.IssuedAtUtc &&
        now < row.AbsoluteExpiresAtUtc;

    private static AccountSetupInvitationConsumeResult Project(AccountSetupInvitationRecord row) =>
        new(
            InvitationId: row.InvitationId,
            TenantId: row.TenantId,
            InviterAccountId: row.InviterAccountId,
            InviterPrincipalId: row.InviterPrincipalId,
            InviterPartyId: row.InviterPartyId,
            RequestedPermissionsJson: row.RequestedPermissionsJson,
            CommandFingerprint: row.CommandFingerprint,
            OwnerVersion: row.OwnerVersion);

    private static string RandomHex(int byteCount) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    private static void Validate(AccountSetupInvitationSeed seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.InviterAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.InviterPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.InviterPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.InviterSessionCorrelationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.InviterMembershipId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.InviterGrantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.RequestedPermissionsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed.CommandFingerprint);
        if (seed.InviterMembershipOwnerVersion <= 0 || seed.InviterGrantOwnerVersion <= 0 ||
            seed.InviterAuthorizationEpoch <= 0 || seed.AbsoluteExpiresAtUtc <= seed.IssuedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seed),
                "Invitation authority versions and expiry must be positive.");
        }
    }
}
