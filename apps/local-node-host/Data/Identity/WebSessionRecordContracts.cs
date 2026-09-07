namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The three mutually exclusive hosted-web cookie audiences.</summary>
public enum WebCookieAudience
{
    AccountChallenge,
    SelectedSession,
    InstallationSession,
}

/// <summary>The canonical R3-H progress vocabulary carried by coordinated transitions.</summary>
public enum WebSessionCoordinationState
{
    Preparing,
    Committing,
    Finalizing,
    Completed,
}

/// <summary>A consume-only pin to one tenant grant authority version.</summary>
public sealed record PinnedGrantOwnerVersion(
    string GrantId,
    long OwnerVersion);

/// <summary>
/// Short-lived account-authenticated audience used before one tenant membership is selected.
/// </summary>
public sealed record WebAccountAccessChallengeRecord(
    string ChallengeId,
    string AccountId,
    long AccountSecurityVersion,
    string HandleDigest,
    string CoordinationCorrelationId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc,
    DateTimeOffset? ConsumedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    long OwnerVersion)
{
    /// <summary>True at and after the absolute TTL boundary.</summary>
    public bool IsExpired(DateTimeOffset atUtc) => atUtc >= AbsoluteExpiresAtUtc;
}

/// <summary>One selected-tenant user-session audience carrying only pinned authority versions.</summary>
public sealed record WebUserSessionRecord(
    string SessionCorrelationId,
    string AccountId,
    long AccountSecurityVersion,
    string TenantId,
    string MembershipId,
    long MembershipOwnerVersion,
    string TenantPrincipalId,
    string CanonicalPartyReference,
    IReadOnlyList<PinnedGrantOwnerVersion> PinnedGrantOwnerVersions,
    long AuthorizationEpoch,
    string HandleDigest,
    string AntiforgeryStateId,
    string CoordinationCorrelationId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc,
    long OwnerVersion)
{
    private IReadOnlyList<PinnedGrantOwnerVersion> _pinnedGrantOwnerVersions =
        RequireExactlyOneGrantPin(PinnedGrantOwnerVersions);

    /// <summary>The session's single immutable membership-grant authority pin.</summary>
    public IReadOnlyList<PinnedGrantOwnerVersion> PinnedGrantOwnerVersions
    {
        get => _pinnedGrantOwnerVersions;
        private init => _pinnedGrantOwnerVersions = RequireExactlyOneGrantPin(value);
    }

    private static IReadOnlyList<PinnedGrantOwnerVersion> RequireExactlyOneGrantPin(
        IReadOnlyList<PinnedGrantOwnerVersion> pinnedGrantOwnerVersions)
    {
        ArgumentNullException.ThrowIfNull(
            pinnedGrantOwnerVersions,
            nameof(PinnedGrantOwnerVersions));
        if (pinnedGrantOwnerVersions.Count != 1)
        {
            throw new ArgumentException(
                "Exactly one pinned grant owner version is required.",
                nameof(PinnedGrantOwnerVersions));
        }

        return Array.AsReadOnly(pinnedGrantOwnerVersions.ToArray());
    }
}

/// <summary>Purpose-bound installation-administration audience with no tenant authority.</summary>
public sealed record WebInstallationSessionRecord(
    string SessionCorrelationId,
    string AccountId,
    long AccountSecurityVersion,
    string InstallationGrantId,
    long InstallationGrantOwnerVersion,
    long AuthorizationEpoch,
    string HandleDigest,
    string AntiforgeryStateId,
    string CoordinationCorrelationId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc,
    long OwnerVersion);

/// <summary>Durable revocation or supersession evidence for exactly one audience record.</summary>
public sealed record WebSessionRevocationRecord(
    string RevocationId,
    WebCookieAudience Audience,
    string AccountId,
    string SubjectCorrelationId,
    string? SupersededByCorrelationId,
    string ReasonCode,
    string CoordinationCorrelationId,
    DateTimeOffset RevokedAtUtc,
    long OwnerVersion);

/// <summary>Digest-only antiforgery state bound to one audience correlation.</summary>
public sealed record WebAntiforgeryStateRecord(
    string AntiforgeryStateId,
    WebCookieAudience Audience,
    string AccountId,
    string SubjectCorrelationId,
    string TokenDigest,
    string CoordinationCorrelationId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc,
    DateTimeOffset? ConsumedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    long OwnerVersion);
