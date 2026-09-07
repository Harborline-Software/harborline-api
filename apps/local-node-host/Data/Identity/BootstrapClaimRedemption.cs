using System.Security.Cryptography;
using System.Data;
using System.Data.Common;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

public enum BootstrapClaimIssuerKind
{
    HostedControlPlane,
    SelfHostedFileSystemOwner,
    DesktopOsSession,
}

internal sealed class BootstrapClaim
{
    internal BootstrapClaim(
        BootstrapClaimIssuerKind issuerKind,
        string issuerIdentity,
        string installationId,
        string targetDigest,
        DateTimeOffset issuedAt,
        DateTimeOffset notBefore,
        DateTimeOffset expiresAt,
        long issuedTimestamp,
        string nonce,
        object acceptance)
    {
        IssuerKind = issuerKind;
        IssuerIdentity = issuerIdentity;
        InstallationId = installationId;
        TargetDigest = targetDigest;
        IssuedAt = issuedAt;
        NotBefore = notBefore;
        ExpiresAt = expiresAt;
        IssuedTimestamp = issuedTimestamp;
        Nonce = nonce;
        Acceptance = acceptance;
    }

    internal BootstrapClaimIssuerKind IssuerKind { get; }
    internal string IssuerIdentity { get; }
    internal string InstallationId { get; }
    internal string TargetDigest { get; }
    internal DateTimeOffset IssuedAt { get; }
    internal DateTimeOffset NotBefore { get; }
    internal DateTimeOffset ExpiresAt { get; }
    internal long IssuedTimestamp { get; }
    internal string Nonce { get; }
    internal object Acceptance { get; }
}

internal interface IBootstrapClaimIssuer
{
    Task<BootstrapClaim?> IssueAsync(
        BootstrapGrantTarget target,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);
}

internal static class BootstrapClaimIssuerSeam
{
    private static readonly object Acceptance = new();

    internal static async Task<BootstrapClaim?> IssueAsync(
        BootstrapClaimIssuerKind kind,
        TimeProvider timeProvider,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        BootstrapGrantTarget target,
        string issuerIdentity,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(identityFactory);
        ArgumentNullException.ThrowIfNull(target);
        var installationId = target.InstallationId;
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuerIdentity);
        if (installationId.Length > 64) throw new ArgumentException("Installation id is too long.", nameof(installationId));
        if (issuerIdentity.Length > 128) throw new ArgumentException("Issuer identity is too long.", nameof(issuerIdentity));
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(lifetime));

        var now = timeProvider.GetUtcNow();
        await using var identity = await identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var window = await HomeEpochFenceTransaction.RunAsync(identity, async () =>
        {
            var persisted = await identity.BootstrapClaimWindows.SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (persisted is null)
            {
                persisted = new BootstrapClaimWindowRecord
                {
                    SingletonKey = BootstrapClaimWindowRecord.SingletonKeyValue,
                    InstallationId = installationId,
                    IssuedAtUtc = now,
                    DeadlineUtc = now.Add(lifetime),
                };
                identity.BootstrapClaimWindows.Add(persisted);
                await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return persisted;
        }, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(window.InstallationId, installationId, StringComparison.Ordinal) ||
            now < window.IssuedAtUtc ||
            now >= window.DeadlineUtc)
            return null;

        return new BootstrapClaim(
            kind,
            issuerIdentity,
            installationId,
            TargetDigest(target),
            now,
            now,
            window.DeadlineUtc,
            timeProvider.GetTimestamp(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            Acceptance);
    }

    internal static bool IsAccepted(BootstrapClaim claim) =>
        ReferenceEquals(claim.Acceptance, Acceptance) &&
        claim.IssuerKind is BootstrapClaimIssuerKind.HostedControlPlane or
            BootstrapClaimIssuerKind.SelfHostedFileSystemOwner or
            BootstrapClaimIssuerKind.DesktopOsSession;

    internal static bool IsBoundTo(BootstrapClaim claim, BootstrapGrantTarget target) =>
        string.Equals(claim.TargetDigest, TargetDigest(target), StringComparison.Ordinal);

    private static string TargetDigest(BootstrapGrantTarget target) =>
        InstallationAuditIntegrity.Hash(
            "bootstrap-claim-target/v1",
            target.InstallationId,
            target.TenantId.Value,
            target.Principal.Value,
            target.Party.Value,
            target.CeremonyCorrelationId);
}

internal sealed record HostedControlPlaneBootstrapAuthority(
    string InstallationId,
    string TargetDigest,
    string IssuerIdentity,
    DateTimeOffset NotBefore,
    DateTimeOffset ExpiresAt);

internal interface IHostedControlPlaneBootstrapClaimSource
{
    Task<SignedOperation<HostedControlPlaneBootstrapAuthority>?> ObtainAsync(
        BootstrapGrantTarget target,
        CancellationToken cancellationToken);
}

internal sealed class HostedControlPlaneBootstrapClaimIssuer : IBootstrapClaimIssuer
{
    private readonly TimeProvider _timeProvider;
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly byte[]? _configuredPublicKey;
    private readonly IHostedControlPlaneBootstrapClaimSource _claimSource;
    private readonly IOperationVerifier _verifier;

    internal HostedControlPlaneBootstrapClaimIssuer(
        TimeProvider timeProvider,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        byte[]? configuredPublicKey,
        IHostedControlPlaneBootstrapClaimSource claimSource,
        IOperationVerifier verifier)
    {
        _timeProvider = timeProvider;
        _identityFactory = identityFactory;
        _configuredPublicKey = configuredPublicKey;
        _claimSource = claimSource;
        _verifier = verifier;
    }

    public async Task<BootstrapClaim?> IssueAsync(
        BootstrapGrantTarget target,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        if (_configuredPublicKey is not { Length: PrincipalId.LengthInBytes })
            return null;
        var signed = await _claimSource.ObtainAsync(target, cancellationToken).ConfigureAwait(false);
        if (signed is null ||
            !CryptographicOperations.FixedTimeEquals(_configuredPublicKey, signed.IssuerId.AsSpan()) ||
            !_verifier.Verify(signed))
            return null;
        var now = _timeProvider.GetUtcNow();
        var payload = signed.Payload;
        var expectedTarget = InstallationAuditIntegrity.Hash(
            "bootstrap-claim-target/v1", target.InstallationId, target.TenantId.Value,
            target.Principal.Value, target.Party.Value, target.CeremonyCorrelationId);
        if (!string.Equals(payload.InstallationId, target.InstallationId, StringComparison.Ordinal) ||
            !string.Equals(payload.TargetDigest, expectedTarget, StringComparison.Ordinal) ||
            payload.NotBefore > now || payload.ExpiresAt <= now || signed.IssuedAt > now)
            return null;

        return await BootstrapClaimIssuerSeam.IssueAsync(
            BootstrapClaimIssuerKind.HostedControlPlane,
            _timeProvider,
            _identityFactory,
            target,
            payload.IssuerIdentity,
            Min(lifetime, payload.ExpiresAt - now),
            cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}

internal interface IFileSystemOwnerEvidence
{
    bool IsOwnedByCurrentProcessUser(string dataDirectory);
}

internal sealed class ProcessFileSystemOwnerEvidence : IFileSystemOwnerEvidence
{
    public bool IsOwnedByCurrentProcessUser(string dataDirectory)
    {
        try
        {
            if (!Directory.Exists(dataDirectory)) return false;
            if (OperatingSystem.IsWindows())
            {
                using var current = WindowsIdentity.GetCurrent();
                var owner = new DirectoryInfo(dataDirectory)
                    .GetAccessControl(AccessControlSections.Owner)
                    .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                return owner is not null && current.User is not null && owner.Equals(current.User);
            }

            if (OperatingSystem.IsLinux() &&
                statx(-100, dataDirectory, 0, 0x00000001, out var status) == 0)
                return status.UserId == geteuid();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        return false;
    }

    // Linux writes the complete 256-byte statx payload even though this evidence reader only
    // consumes stx_uid. Reserving the native size prevents the kernel from overrunning the
    // managed interop buffer.
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct LinuxStatx
    {
        internal uint Mask;
        internal uint BlockSize;
        internal ulong Attributes;
        internal uint LinkCount;
        internal uint UserId;
        internal uint GroupId;
        internal ushort Mode;
        internal ushort Padding;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int directoryFileDescriptor, string path, int flags, uint mask, out LinuxStatx status);

    [DllImport("libc")]
    private static extern uint geteuid();
}

internal sealed class SelfHostedFileSystemOwnerBootstrapClaimIssuer : IBootstrapClaimIssuer
{
    private readonly TimeProvider _timeProvider;
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly string _dataDirectory;
    private readonly IFileSystemOwnerEvidence _ownerEvidence;

    internal SelfHostedFileSystemOwnerBootstrapClaimIssuer(
        TimeProvider timeProvider,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        string dataDirectory,
        IFileSystemOwnerEvidence? ownerEvidence = null)
    {
        _timeProvider = timeProvider;
        _identityFactory = identityFactory;
        _dataDirectory = dataDirectory;
        _ownerEvidence = ownerEvidence ?? new ProcessFileSystemOwnerEvidence();
    }

    public Task<BootstrapClaim?> IssueAsync(
        BootstrapGrantTarget target,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        if (!_ownerEvidence.IsOwnedByCurrentProcessUser(_dataDirectory) ||
            string.IsNullOrWhiteSpace(Environment.UserName))
            return Task.FromResult<BootstrapClaim?>(null);
        return BootstrapClaimIssuerSeam.IssueAsync(
            BootstrapClaimIssuerKind.SelfHostedFileSystemOwner,
            _timeProvider,
            _identityFactory,
            target,
            ActorId.Mint($"filesystem-owner:{Environment.UserName}").Value,
            lifetime,
            cancellationToken);
    }
}

internal interface IDesktopOsSessionEvidence
{
    bool IsInteractive { get; }
    string UserName { get; }
}

internal sealed class ProcessDesktopOsSessionEvidence : IDesktopOsSessionEvidence
{
    public bool IsInteractive => Environment.UserInteractive;
    public string UserName => Environment.UserName;
}

internal sealed class DesktopOsSessionBootstrapClaimIssuer : IBootstrapClaimIssuer
{
    private readonly TimeProvider _timeProvider;
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly string _configuredFounderIdentity;
    private readonly IDesktopOsSessionEvidence _sessionEvidence;

    internal DesktopOsSessionBootstrapClaimIssuer(
        TimeProvider timeProvider,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        string configuredFounderIdentity,
        IDesktopOsSessionEvidence? sessionEvidence = null)
    {
        _timeProvider = timeProvider;
        _identityFactory = identityFactory;
        _configuredFounderIdentity = configuredFounderIdentity;
        _sessionEvidence = sessionEvidence ?? new ProcessDesktopOsSessionEvidence();
    }

    public Task<BootstrapClaim?> IssueAsync(
        BootstrapGrantTarget target,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var processUser = _sessionEvidence.UserName;
        var normalizedProcessUser = WebUsernameNormalizer.TryNormalize(processUser.Split('\\')[^1]);
        var normalizedFounder = WebUsernameNormalizer.TryNormalize(_configuredFounderIdentity);
        if (!_sessionEvidence.IsInteractive || normalizedProcessUser is null || normalizedFounder is null ||
            !string.Equals(normalizedProcessUser, normalizedFounder, StringComparison.Ordinal))
            return Task.FromResult<BootstrapClaim?>(null);

        return BootstrapClaimIssuerSeam.IssueAsync(
            BootstrapClaimIssuerKind.DesktopOsSession,
            _timeProvider,
            _identityFactory,
            target,
            ActorId.Mint($"desktop-os-session:{processUser}").Value,
            lifetime,
            cancellationToken);
    }
}

internal sealed record BootstrapGrantTarget(
    TenantId TenantId,
    PrincipalUserId Principal,
    CanonicalPartyReference Party,
    string CeremonyCorrelationId,
    string InstallationId);

internal enum BootstrapClaimRedemptionStatus
{
    Redeemed,
    ClaimRejected,
    SurfaceUnavailable,
}

internal sealed record BootstrapClaimRedemptionResult(
    BootstrapClaimRedemptionStatus Status,
    AccessGrant? Grant = null,
    long? AuthorizationEpoch = null);

internal sealed class BootstrapClaimRedemptionService
{
    private const int BusyRetryCount = 100;
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly IGrantStore _grantStore;
    private readonly InitialGrantIssuanceService _grantIssuance;
    private readonly AuthorizationSeedProfile _seedProfile;
    private readonly TimeProvider _timeProvider;

    internal Func<IDisposable>? FenceAttemptScopeForTests { get; set; }

    public BootstrapClaimRedemptionService(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        IGrantStore grantStore,
        InitialGrantIssuanceService grantIssuance,
        AuthorizationSeedProfile seedProfile,
        TimeProvider timeProvider)
    {
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _grantStore = grantStore ?? throw new ArgumentNullException(nameof(grantStore));
        _grantIssuance = grantIssuance ?? throw new ArgumentNullException(nameof(grantIssuance));
        _seedProfile = seedProfile ?? throw new ArgumentNullException(nameof(seedProfile));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<bool> IsSurfaceAvailableAsync(
        string installationId,
        TenantId tenant,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await HasRetirementEvidenceAsync(identity, cancellationToken).ConfigureAwait(false) ||
            !await identity.InstallationIdentities.AsNoTracking().AnyAsync(
                row => row.InstallationIdentityId == installationId,
                cancellationToken).ConfigureAwait(false))
            return false;

        return !await HasDisqualifyingGrantAsync(identity, tenant, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BootstrapClaimRedemptionResult> RedeemAsync(
        BootstrapClaim? claim,
        BootstrapGrantTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var now = _timeProvider.GetUtcNow();
        if (claim is null ||
            !BootstrapClaimIssuerSeam.IsAccepted(claim) ||
            !IsWithinClaimWindow(claim, now) ||
            !string.Equals(claim.InstallationId, target.InstallationId, StringComparison.Ordinal) ||
            !BootstrapClaimIssuerSeam.IsBoundTo(claim, target))
            return new(BootstrapClaimRedemptionStatus.ClaimRejected);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryRedeemAsync(claim, target, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsBusy(exception) && attempt < BusyRetryCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5 * (attempt + 1), 100)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    internal async Task<BootstrapClaimRedemptionResult?> ResolveRedeemedAsync(
        BootstrapGrantTarget target,
        CancellationToken cancellationToken)
    {
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var marker = await identity.BootstrapClaimMarkers.AsNoTracking().SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (marker is null || !string.Equals(marker.InstallationId, target.InstallationId, StringComparison.Ordinal))
            return null;

        var grant = await _grantStore.FindAsync(target.TenantId, new GrantId(Guid.Parse(marker.GrantId)), cancellationToken)
            .ConfigureAwait(false);
        if (grant is null || _grantStore is not IGrantAuthorizationEpochReader epochs)
            return null;
        var epoch = await epochs.ReadAuthorizationEpochAsync(target.TenantId, grant.Subject, cancellationToken)
            .ConfigureAwait(false);
        return epoch is null ? null : new(BootstrapClaimRedemptionStatus.SurfaceUnavailable, grant, epoch);
    }

    private async Task<BootstrapClaimRedemptionResult> TryRedeemAsync(
        BootstrapClaim claim,
        BootstrapGrantTarget target,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        using var fenceAttempt = FenceAttemptScopeForTests?.Invoke();
        return await HomeEpochFenceTransaction.RunAsync(identity, async () =>
        {
            if (await HasRetirementEvidenceAsync(identity, cancellationToken).ConfigureAwait(false) ||
                await HasDisqualifyingGrantAsync(identity, target.TenantId, cancellationToken).ConfigureAwait(false))
                return new BootstrapClaimRedemptionResult(BootstrapClaimRedemptionStatus.SurfaceUnavailable);

            var installationExists = await identity.InstallationIdentities.AsNoTracking().AnyAsync(
                    row => row.InstallationIdentityId == target.InstallationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!installationExists || !IsWithinClaimWindow(claim, at))
                return new BootstrapClaimRedemptionResult(BootstrapClaimRedemptionStatus.ClaimRejected);

            var admission = new AdmissionCompleted(
                target.TenantId,
                target.Principal,
                target.Party,
                new PrincipalUserId(claim.IssuerIdentity),
                target.CeremonyCorrelationId,
                RoleReference.Administrator,
                new GrantProvenance(
                    GrantSourceKind.Bootstrap,
                    new GrantReason(GrantReasonCodes.Bootstrap, target.CeremonyCorrelationId),
                    new ActorId(claim.IssuerIdentity)));
            var prepared = _grantIssuance.PrepareBootstrapGrant(admission, at);
            var authorizationEpoch = await AppendPreparedGrantAsync(
                identity,
                prepared,
                cancellationToken).ConfigureAwait(false);

            var nonceDigest = InstallationAuditIntegrity.Hash("bootstrap-claim-nonce/v1", claim.Nonce);
            identity.BootstrapClaimMarkers.Add(new BootstrapClaimMarkerRecord
            {
                SingletonKey = BootstrapClaimMarkerRecord.SingletonKeyValue,
                InstallationId = target.InstallationId,
                GrantId = prepared.Grant.GrantId.ToString(),
                IssuerKind = claim.IssuerKind,
                IssuerIdentity = claim.IssuerIdentity,
                NonceDigest = nonceDigest,
                ClaimedAtUtc = at,
            });
            await InstallationIdentityAuditChain.AppendEventAsync(
                identity,
                InstallationIdentityAuditEventTypes.BootstrapClaimRedeemed,
                claim.IssuerKind.ToString(),
                claim.IssuerIdentity,
                $"bootstrap-claim:{nonceDigest[..32]}",
                InstallationAuditIntegrity.Hash(
                    "bootstrap-claim-command/v1",
                    claim.InstallationId,
                    claim.IssuerIdentity,
                    claim.IssuedAt.ToUnixTimeMilliseconds().ToString(),
                    claim.ExpiresAt.ToUnixTimeMilliseconds().ToString(),
                    nonceDigest),
                InstallationAuditIntegrity.Hash(
                    "bootstrap-claim-redemption/v1",
                    prepared.Grant.GrantId.ToString(),
                    target.TenantId.Value,
                    target.Principal.Value,
                    RoleReference.Administrator.ToString(),
                    nonceDigest),
                at,
                cancellationToken).ConfigureAwait(false);
            await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new BootstrapClaimRedemptionResult(
                BootstrapClaimRedemptionStatus.Redeemed,
                prepared.Grant,
                authorizationEpoch);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HasRetirementEvidenceAsync(
        NodeLocalInstallationIdentityDbContext context,
        CancellationToken cancellationToken) =>
        await context.BootstrapClaimMarkers.AsNoTracking().AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await context.AuditEnvelopes.AsNoTracking().AnyAsync(
            row => row.EventType == InstallationIdentityAuditEventTypes.BootstrapClaimRedeemed,
            cancellationToken).ConfigureAwait(false);

    private bool IsWithinClaimWindow(BootstrapClaim claim, DateTimeOffset now)
    {
        if (now < claim.IssuedAt || now < claim.NotBefore || now >= claim.ExpiresAt)
            return false;
        var elapsed = _timeProvider.GetElapsedTime(claim.IssuedTimestamp, _timeProvider.GetTimestamp());
        return elapsed >= TimeSpan.Zero && elapsed < claim.ExpiresAt - claim.IssuedAt;
    }

    private async Task<bool> HasDisqualifyingGrantAsync(
        NodeLocalInstallationIdentityDbContext context,
        TenantId tenant,
        CancellationToken cancellationToken)
    {
        if (context.Database.GetDbConnection().State != ConnectionState.Open)
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(context,
            """
            SELECT grant_id, tenant_id, subject_id, role_vocabulary, role_name, scope_type, scope_value,
                residency, validity_from_unix_ms, validity_until_unix_ms, status, granter_kind,
                granted_by, granted_at_unix_ms, source, reason_code, reason_reference, approver,
                last_reviewed_at_unix_ms, last_reviewed_by, validity_changed_by,
                validity_change_reason_code, validity_change_reason_reference, revoked_by,
                revoked_at_unix_ms, revocation_reason_code, revocation_reason_reference, source_reference
            FROM search_grants
            WHERE tenant_id = @tenant_id;
            """);
        Add(command, "@tenant_id", tenant.Value);
        var grants = new List<InstallerSeedGrantEvidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var revocation = reader.IsDBNull(24) ? null : new GrantRevocation(
                new ActorId(reader.GetString(23)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(24)),
                new GrantReason(reader.GetString(25), reader.IsDBNull(26) ? null : reader.GetString(26)));
            var validityChange = reader.IsDBNull(20) ? null : new GrantValidityChangeEvidence(
                new ActorId(reader.GetString(20)),
                new GrantReason(reader.GetString(21), reader.IsDBNull(22) ? null : reader.GetString(22)));
            var grant = new AccessGrant(
                new GrantId(Guid.Parse(reader.GetString(0))),
                TenantId.FromString(reader.GetString(1)),
                new ActorId(reader.GetString(2)),
                new RoleReference(reader.GetString(3), reader.GetString(4)),
                ScopeExpression.Parse(reader.GetString(6)),
                (GrantResidency)reader.GetInt32(7),
                new GrantValidity(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
                    reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9))),
                (GranterKind)reader.GetInt32(11),
                new ActorId(reader.GetString(12)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(13)),
                new GrantProvenance(
                    (GrantSourceKind)reader.GetInt32(14),
                    new GrantReason(reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetString(16)),
                    new ActorId(reader.GetString(17))),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(18)),
                (GrantStatus)reader.GetInt32(10),
                revocation,
                validityChange,
                reader.IsDBNull(19) ? (ActorId?)null : new ActorId(reader.GetString(19)));
            grants.Add(new InstallerSeedGrantEvidence(
                grant,
                (ScopeExpressionType)reader.GetInt32(5),
                reader.IsDBNull(27) ? null : reader.GetString(27)));
        }
        return !AccessGrantAuthorizationSeed.IsExactInstallerSeedSet(tenant, grants, _seedProfile);
    }

    private static async Task<long> AppendPreparedGrantAsync(
        NodeLocalInstallationIdentityDbContext context,
        PreparedInitialGrant prepared,
        CancellationToken cancellationToken)
    {
        var grant = prepared.Grant;
        await using (var command = CreateCommand(context,
            """
            INSERT INTO search_grants (
                grant_id, tenant_id, subject_id, role_vocabulary, role_name, scope_type, scope_value,
                residency, validity_from_unix_ms, validity_until_unix_ms, status, granter_kind,
                granted_by, granted_at_unix_ms, source, reason_code, reason_reference, approver,
                last_reviewed_at_unix_ms, last_reviewed_by, validity_changed_by,
                validity_change_reason_code, validity_change_reason_reference, revoked_by,
                revoked_at_unix_ms, revocation_reason_code, revocation_reason_reference,
                source_reference, owner_version)
            VALUES (
                @grant_id, @tenant_id, @subject_id, @role_vocabulary, @role_name, @scope_type,
                @scope_value, @residency, @validity_from, @validity_until, @status, @granter_kind,
                @granted_by, @granted_at, @source, @reason_code, @reason_reference, @approver,
                @last_reviewed_at, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
                @source_reference, 1);
            """))
        {
            Add(command, "@grant_id", grant.GrantId.ToString());
            Add(command, "@tenant_id", grant.TenantId.Value);
            Add(command, "@subject_id", grant.Subject.Value);
            Add(command, "@role_vocabulary", grant.Role.Vocabulary);
            Add(command, "@role_name", grant.Role.Name);
            Add(command, "@scope_type", (int)grant.Scope.Type);
            Add(command, "@scope_value", grant.Scope.Value);
            Add(command, "@residency", (int)grant.Residency);
            Add(command, "@validity_from", grant.Validity.ValidFrom.ToUnixTimeMilliseconds());
            Add(command, "@validity_until", grant.Validity.ValidTo?.ToUnixTimeMilliseconds());
            Add(command, "@status", (int)grant.Status);
            Add(command, "@granter_kind", (int)grant.GranterKind);
            Add(command, "@granted_by", grant.GrantedBy.Value);
            Add(command, "@granted_at", grant.GrantedAt.ToUnixTimeMilliseconds());
            Add(command, "@source", (int)grant.Grant.Source);
            Add(command, "@reason_code", grant.Grant.Reason.Code);
            Add(command, "@reason_reference", grant.Grant.Reason.Reference);
            Add(command, "@approver", grant.Grant.Approver.Value);
            Add(command, "@last_reviewed_at", grant.LastReviewedAt.ToUnixTimeMilliseconds());
            Add(command, "@source_reference", prepared.SourceReference);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var epoch = CreateCommand(context,
            """
            INSERT INTO search_grant_authorization_epochs (tenant_id, principal_id, authorization_epoch)
            VALUES (@tenant_id, @principal_id, 1)
            ON CONFLICT(tenant_id, principal_id)
            DO UPDATE SET authorization_epoch = authorization_epoch + 1;
            """))
        {
            Add(epoch, "@tenant_id", grant.TenantId.Value);
            Add(epoch, "@principal_id", grant.Subject.Value);
            await epoch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var version = CreateCommand(context,
            """
            INSERT INTO authorization_tenant_versions (tenant_id, version) VALUES (@tenant_id, 1)
            ON CONFLICT(tenant_id) DO UPDATE SET version = version + 1;
            """))
        {
            Add(version, "@tenant_id", grant.TenantId.Value);
            await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var read = CreateCommand(context,
            """
            SELECT authorization_epoch FROM search_grant_authorization_epochs
            WHERE tenant_id = @tenant_id AND principal_id = @principal_id;
            """);
        Add(read, "@tenant_id", grant.TenantId.Value);
        Add(read, "@principal_id", grant.Subject.Value);
        return Convert.ToInt64(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static DbCommand CreateCommand(
        NodeLocalInstallationIdentityDbContext context,
        string commandText)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = commandText;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static bool IsBusy(Exception exception) =>
        exception.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("database table is locked", StringComparison.OrdinalIgnoreCase) ||
        exception.InnerException is { } inner && IsBusy(inner);
}
