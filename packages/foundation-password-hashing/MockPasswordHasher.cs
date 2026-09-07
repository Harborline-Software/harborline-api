using Microsoft.AspNetCore.Identity;

namespace Harborline.Api.Foundation.PasswordHashing;

/// <summary>
/// Fast-test-loop mock <see cref="IPasswordHasher{TUser}"/> (ADR 0097 D4 / S2). Returns
/// the constant string <see cref="MockHash"/> from <see cref="HashPassword"/> — carrying
/// ZERO password-derived material (no length, no first byte, no character-class proxy) so
/// that even a load-test deployment that legitimately ships the mock leaks no
/// cohort-attack credential-strength-histogram signal (S2 sec-eng substrate amendment;
/// satisfies Floor 7 by construction). Verify succeeds iff the stored hash is the mock
/// constant and a non-empty candidate is supplied.
/// </summary>
/// <remarks>
/// Carries the <see cref="IMockPasswordHasher"/> marker so the
/// <see cref="DependencyInjection.MockPasswordHasherProductionGuardAssertion"/> can
/// detect it in the registration tree and fail-closed in production without an explicit
/// opt-out. Register ONLY via
/// <see cref="DependencyInjection.PasswordHashingServiceCollectionExtensions.AddHarborlineMockPasswordHashing{TUser}"/>
/// (type-based registration; the constrained helper guarantees the production-guard scan
/// sees the mock — a raw factory registration would be invisible to the descriptor scan).
/// </remarks>
/// <typeparam name="TUser">The user entity type (TUser-agnostic — the mock does not read it).</typeparam>
public sealed class MockPasswordHasher<TUser> : IPasswordHasher<TUser>, IMockPasswordHasher
    where TUser : class
{
    /// <summary>The constant stored-hash value. Embeds no password-derived material (S2).</summary>
    public const string MockHash = "mock-hash";

    /// <summary>
    /// Returns the constant <see cref="MockHash"/> regardless of the supplied password.
    /// Does NOT log, store, or derive any signal from <paramref name="password"/> (Floor 7).
    /// </summary>
    public string HashPassword(TUser user, string password) => MockHash;

    /// <summary>
    /// Returns <see cref="PasswordVerificationResult.Success"/> iff
    /// <paramref name="hashedPassword"/> is the mock constant AND
    /// <paramref name="providedPassword"/> is non-empty; otherwise
    /// <see cref="PasswordVerificationResult.Failed"/>. NEVER returns
    /// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> — mock hashes are not
    /// subject to the parameter-floor upgrade discipline.
    /// </summary>
    public PasswordVerificationResult VerifyHashedPassword(
        TUser user,
        string hashedPassword,
        string providedPassword)
    {
        return string.Equals(hashedPassword, MockHash, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(providedPassword)
                ? PasswordVerificationResult.Success
                : PasswordVerificationResult.Failed;
    }
}
