using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

#pragma warning disable HARBORLINE_API_PROVNEUT_001 // Ticket 026: explicit waiver; both types are the ratified ADR 0097 foundation hasher contract, not HTTP-layer state.
using IPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<Harborline.Api.LocalNodeHost.Data.Identity.InstallationAccountRecord>;
using PasswordVerificationResult = Microsoft.AspNetCore.Identity.PasswordVerificationResult;
#pragma warning restore HARBORLINE_API_PROVNEUT_001
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.PasswordHashing;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The one-time handle returned after an installation account credential is verified.</summary>
public sealed record WebAccountAccessChallengeIssueResult(
    string Handle,
    string AntiforgeryToken,
    DateTimeOffset ExpiresAtUtc,
    string AccountId);

/// <summary>The outcome of one account-challenge login attempt.</summary>
/// <param name="VerifyRan">Whether the password verification path ran before this outcome.</param>
public sealed record WebAccountAccessChallengeAttemptResult(
    WebAccountAccessChallengeIssueResult? Challenge,
    WebLoginFailureReason? FailureReason,
    bool VerifyRan);

/// <summary>Verifies one installation account and issues its pre-tenant challenge audience.</summary>
public interface IWebAccountAccessChallengeIssuer
{
    /// <summary>Returns a fresh challenge or a typed refusal; unexpected faults still throw.</summary>
    Task<WebAccountAccessChallengeAttemptResult> IssueAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable ADR 0160 account-challenge issuer. The returned handle is never persisted; only its
/// SHA-256 digest enters the dedicated web-session store.
/// </summary>
public sealed class WebAccountAccessChallengeIssuer : IWebAccountAccessChallengeIssuer
{
    /// <summary>ADR 0160's maximum account-challenge lifetime.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private const int MaxPasswordLength = 4096;
    private const int HandleByteLength = 32;

    private static readonly InstallationAccountRecord UnknownAccount = new()
    {
        AccountId = "unknown-account",
        NormalizedUsername = "UNKNOWN-ACCOUNT",
        CredentialHash = "not-persisted",
        CredentialAlgorithm = Argon2idCredentialArtifact.AlgorithmId,
        CredentialCeremonyId = "00000000000000000000000000000000",
        CredentialVersion = 1,
        Status = InstallationAccountStatus.Disabled,
        SecurityVersion = 1,
        OwnerVersion = 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly IDbContextFactory<NodeLocalWebSessionDbContext> _sessionFactory;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IInstallationIdentityV1AuthorityGate _v1AuthorityGate;
    private readonly TimeProvider _timeProvider;
    private readonly string _unknownAccountCredentialHash;

    /// <summary>Constructs the issuer from the two durable authority stores and ADR 0097 hasher.</summary>
    public WebAccountAccessChallengeIssuer(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        IDbContextFactory<NodeLocalWebSessionDbContext> sessionFactory,
        IPasswordHasher passwordHasher,
        IInstallationIdentityV1AuthorityGate v1AuthorityGate,
        TimeProvider timeProvider)
    {
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _v1AuthorityGate = v1AuthorityGate ?? throw new ArgumentNullException(nameof(v1AuthorityGate));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        // Unknown users still traverse the same Argon2id verification path. Generate this process-local
        // sentinel once so no dummy credential is committed, persisted, configured, or returned.
        _unknownAccountCredentialHash = _passwordHasher.HashPassword(
            UnknownAccount,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(HandleByteLength)));
    }

    /// <inheritdoc />
    public async Task<WebAccountAccessChallengeAttemptResult> IssueAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (password is null || password.Length > MaxPasswordLength)
        {
            return new(null, WebLoginFailureReason.MalformedInput, VerifyRan: false);
        }

        // Minting a challenge is the ISSUE half of the AccountChallenge legacy bearer audience, so it
        // stops the moment the barrier rises (ADR 0160 R3-H step 5 refuses all new v1 session
        // mutations) and stays stopped after the marker CAS retires the audience. Gating here rather
        // than in AccountChallengeRoutes puts the check on the AUTHORITY, so every caller of this
        // seam is covered rather than only the one transport that exists today.
        //
        // Placed before the Argon2id verify, and that does NOT weaken the non-enumeration posture the
        // rest of this method maintains: the refusal depends only on installation-wide cutover state,
        // never on the supplied username, so every caller is refused identically and no account's
        // existence becomes observable.
        var admission = await _v1AuthorityGate
            .CheckLegacyBearerAdmissionAsync(
                InstallationIdentityLegacyBearerAudience.AccountChallenge,
                cancellationToken)
            .ConfigureAwait(false);
        if (!admission.IsAllowed)
        {
            return new(null, WebLoginFailureReason.AuthorityRefused, VerifyRan: false);
        }

        var normalizedUsername = WebUsernameNormalizer.TryNormalize(username);
        var malformedUsername = normalizedUsername is null;
        InstallationAccountRecord? candidate = null;
        if (normalizedUsername is not null)
        {
            await using var identity = await _identityFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            candidate = await identity.Accounts
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    account => account.NormalizedUsername == normalizedUsername,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var verificationAccount = candidate ?? UnknownAccount;
        var credentialHash = candidate?.CredentialHash ?? _unknownAccountCredentialHash;
        var verification = _passwordHasher.VerifyHashedPassword(
            verificationAccount,
            credentialHash,
            password);

        if (malformedUsername)
        {
            return new(null, WebLoginFailureReason.MalformedInput, VerifyRan: true);
        }

        if (candidate is null)
        {
            return new(null, WebLoginFailureReason.CredentialMismatch, VerifyRan: true);
        }

        if (candidate.Status != InstallationAccountStatus.Active ||
            candidate.SecurityVersion <= 0 ||
            !string.Equals(
                candidate.CredentialAlgorithm,
                Argon2idCredentialArtifact.AlgorithmId,
                StringComparison.Ordinal))
        {
            return new(null, WebLoginFailureReason.ProvisioningRefused, VerifyRan: true);
        }

        if (verification == PasswordVerificationResult.Failed)
        {
            return new(null, WebLoginFailureReason.CredentialMismatch, VerifyRan: true);
        }

        // Password verification is deliberately expensive. Re-read afterward so a concurrent disable,
        // credential rotation, or security-version increment cannot issue a challenge from stale state.
        await using (var identity = await _identityFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var current = await identity.Accounts
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    account => account.AccountId == candidate.AccountId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current is null ||
                current.Status != InstallationAccountStatus.Active ||
                current.SecurityVersion != candidate.SecurityVersion ||
                current.OwnerVersion != candidate.OwnerVersion ||
                current.CredentialVersion != candidate.CredentialVersion ||
                !string.Equals(current.CredentialHash, candidate.CredentialHash, StringComparison.Ordinal) ||
                !string.Equals(
                    current.CredentialAlgorithm,
                    Argon2idCredentialArtifact.AlgorithmId,
                    StringComparison.Ordinal))
            {
                return new(null, WebLoginFailureReason.ProvisioningRefused, VerifyRan: true);
            }
        }

        var issuedAt = _timeProvider.GetUtcNow();
        var expiresAt = issuedAt + ChallengeLifetime;
        var handle = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(HandleByteLength));
        var handleDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(handle)));
        var challengeId = RandomHex(32);
        var coordinationCorrelationId = RandomHex(32);
        var challenge = new WebAccountAccessChallengeRecord(
            ChallengeId: challengeId,
            AccountId: candidate.AccountId,
            AccountSecurityVersion: candidate.SecurityVersion,
            HandleDigest: handleDigest,
            CoordinationCorrelationId: coordinationCorrelationId,
            IssuedAtUtc: issuedAt,
            AbsoluteExpiresAtUtc: expiresAt,
            ConsumedAtUtc: null,
            RevokedAtUtc: null,
            OwnerVersion: 1);
        var antiforgery = WebAntiforgeryStateStore.CreateState(
            WebCookieAudience.AccountChallenge,
            candidate.AccountId,
            challengeId,
            coordinationCorrelationId,
            issuedAt,
            expiresAt);

        await using var sessions = await _sessionFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        sessions.AccountAccessChallenges.Add(challenge);
        sessions.AntiforgeryStates.Add(antiforgery.State);
        await sessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new(
            new WebAccountAccessChallengeIssueResult(
                handle, antiforgery.Token, expiresAt, candidate.AccountId),
            null,
            VerifyRan: true);
    }

    private static string RandomHex(int byteLength) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteLength)).ToLowerInvariant();
}
