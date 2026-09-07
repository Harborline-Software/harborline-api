namespace Harborline.Api.Kernel.Security.Verification;

/// <summary>
/// Identifies a single verification and binds a derived code to it (ADR 0152 §Item-(a)).
/// </summary>
/// <param name="EngagementId">
/// Binds the code to THIS engagement — used as the HKDF salt in <see cref="Keys.SafetyCodeDerivation"/>,
/// so a past engagement's code never validates against a new one (replay resistance).
/// </param>
/// <param name="Label">The consumer requesting the code; domain-separates one consumer's code from another's.</param>
/// <param name="Policy">Expiry / rotation policy the fail-closed gate consults.</param>
public readonly record struct VerificationContext(
    string EngagementId,
    VerificationLabel Label,
    VerificationLifecycle Policy);
