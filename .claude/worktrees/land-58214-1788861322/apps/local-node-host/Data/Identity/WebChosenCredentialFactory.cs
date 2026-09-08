#pragma warning disable HARBORLINE_API_PROVNEUT_001 // Ticket 026: explicit waiver; ADR 0097's foundation hasher contract is Microsoft.Extensions.Identity.Core's IPasswordHasher.
using IPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<Harborline.Api.LocalNodeHost.Data.Identity.InstallationAccountRecord>;
#pragma warning restore HARBORLINE_API_PROVNEUT_001

using Harborline.Api.Foundation.PasswordHashing;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// Server-minted credential material for one human-chosen password: the canonical ADR 0097 Argon2id
/// artifact plus the ceremony id that records which establishment produced it.
/// </summary>
public sealed record WebChosenCredential(string CredentialHash, string CredentialCeremonyId);

/// <summary>
/// Turns a password the human chose into the credential artifact the identity substrate persists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this seam exists (ADR 0160 D3, earlier repository ticket #3338).</b> D3 says redemption "chooses a password".
/// The artifact that password becomes is NOT something a browser can produce. Two reasons hold
/// today, on the shipped configuration:
/// </para>
/// <list type="number">
///   <item><b>Installation-local cost parameters.</b> The artifact carries the <c>m</c>/<c>t</c>/<c>p</c>
///     this installation is configured with, and
///     <see cref="Argon2idCredentialArtifact.IsCanonicalAndWithinVerificationPolicy"/> range-checks
///     them. A client would have to be told the installation's parameters and track every change to
///     them.</item>
///   <item><b>A canonical encoding the standard does not emit.</b> The accepted form uses PADDED
///     Base64 (<see cref="Convert.ToBase64String"/>); the standard PHC encoding is unpadded, so an
///     artifact from any off-the-shelf Argon2 library fails the canonical check outright.</item>
/// </list>
/// <para>
/// <b>And one that is a bound, not a present fact.</b> <c>Argon2idHashOptions.Pepper</c> is a
/// server-only secret the hasher mixes into both hash and verify. It is <c>null</c> at MVP and the
/// host binds no value to it — so it is NOT why a browser cannot produce the artifact today. It is
/// why the request contract must never come to DEPEND on a browser producing one: the day a pepper
/// is configured, a client-computed artifact would start minting accounts whose password can never
/// verify at <see cref="WebAccountAccessChallengeIssuer"/>, silently — acceptance returns 200 and
/// every subsequent sign-in fails. Hashing belongs on the side that would hold the secret.
/// </para>
/// <para>
/// <b>Fail-closed, and BEFORE any invitation is consumed.</b> <see cref="Create"/> returns
/// <see langword="null"/> for anything it will not hash rather than throwing. Its caller must run it
/// ahead of the acceptance authority: invitation consumption is single-use and irreversible, so a
/// credential refused after the gate would burn the human's one code on a recoverable input error.
/// </para>
/// </remarks>
public interface IWebChosenCredentialFactory
{
    /// <summary>Returns the minted credential, or <see langword="null"/> when the password is unusable.</summary>
    WebChosenCredential? Create(string? password);
}

/// <inheritdoc />
internal sealed class WebChosenCredentialFactory : IWebChosenCredentialFactory
{
    /// <summary>
    /// The substrate's own provided-password ceiling (<c>Argon2idPasswordHasher</c> throws above it).
    /// Mirrored here so an over-long password is a clean refusal rather than an exception, and so this
    /// type introduces NO password policy the platform has not already decided — a minimum length is
    /// deliberately absent because no accepted ADR states one.
    /// </summary>
    internal const int MaxPasswordLength = 4096;

    /// <summary>
    /// The hasher ignores its user argument (the artifact is derived from password + salt + options
    /// only), so this sentinel exists solely to satisfy the interface. It is never persisted.
    /// </summary>
    private static readonly InstallationAccountRecord HashSubject = new()
    {
        AccountId = "credential-mint",
        NormalizedUsername = "CREDENTIAL-MINT",
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

    private readonly IPasswordHasher _hasher;

    /// <summary>Constructs the factory over the installation account hasher.</summary>
    public WebChosenCredentialFactory(IPasswordHasher hasher) =>
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));

    /// <inheritdoc />
    public WebChosenCredential? Create(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
        {
            return null;
        }

        var credentialHash = _hasher.HashPassword(HashSubject, password);

        // Defensive: the host asserts the Argon2id parameter floors at startup, so a below-floor
        // artifact should be unreachable here. Verifying anyway keeps a misconfiguration a refusal
        // the caller can answer BEFORE the invitation is consumed, instead of an exception thrown
        // deeper in the mint — after the human's single-use code has already been spent.
        return Argon2idCredentialArtifact.IsCanonicalAndWithinVerificationPolicy(credentialHash)
            ? new WebChosenCredential(credentialHash, Guid.NewGuid().ToString("N"))
            : null;
    }
}
