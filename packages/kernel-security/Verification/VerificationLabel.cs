namespace Harborline.Api.Kernel.Security.Verification;

/// <summary>
/// The five ratified safety-code consumers (ADR 0152 §Item-(d)). Each yields a distinct code from the
/// same authenticated shared secret because the label is appended to the versioned HKDF info prefix
/// (<see cref="Keys.SafetyCodeDerivation.InfoPrefix"/>), so a co-approval code never equals an identity
/// code for the same engagement.
/// </summary>
public enum VerificationLabel
{
    /// <summary>Channel-1 call-verification safety-number (#2267).</summary>
    Identity,

    /// <summary>Channel-2 entitlement code over a funded-contract artifact (#2271).</summary>
    Entitlement,

    /// <summary>Gates a CP action on a matched code (maps to CP-confirm + SoD).</summary>
    CoApproval,

    /// <summary>Allows a local recovery reset only on a matched code (support-trust D-8).</summary>
    Recovery,

    /// <summary>Duress / absence-signal — label RESERVED; response semantics designed in #2281.</summary>
    ProofOfSafety,
}

/// <summary>Maps a <see cref="VerificationLabel"/> to its frozen HKDF info suffix.</summary>
public static class VerificationLabelExtensions
{
    /// <summary>
    /// The canonical, frozen label string appended to <c>sunfish-safety-code-v1:</c>. These strings are
    /// part of the frozen <c>sunfish-*-v1:</c> crypto-identifier namespace (ADR 0152 OQ-1) — DO NOT rename.
    /// </summary>
    public static string ToLabelString(this VerificationLabel label) => label switch
    {
        VerificationLabel.Identity => "identity",
        VerificationLabel.Entitlement => "entitlement",
        VerificationLabel.CoApproval => "co-approval",
        VerificationLabel.Recovery => "recovery",
        VerificationLabel.ProofOfSafety => "proof-of-safety",
        _ => throw new ArgumentOutOfRangeException(
            nameof(label), label, "Unknown verification label; only the five ratified ADR 0152 labels exist."),
    };
}
