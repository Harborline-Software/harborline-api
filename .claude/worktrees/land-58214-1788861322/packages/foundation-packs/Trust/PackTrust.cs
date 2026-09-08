using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.Packs.Trust;

/// <summary>
/// The trust SCOPE a root carries — WHAT a root may vouch for (design fold S-13: the trust store is
/// scope-carrying from day one). v1 has exactly two first-party roots.
/// </summary>
/// <remarks>
/// The scope is load-bearing for B-1b (install): a channel root MAY seed CP floors; a future
/// non-first-party root would be capability-scoped + sandbox-gated and could NEVER seed a CP floor
/// or an effecting workflow. B-1a carries the scope through to the verdict so B-1b can enforce it;
/// enabling a third-party root later requires a new ADR-gated scoped root, NOT a flag flip — because
/// there is structurally no "install anyway" / bypass path (S-13).
/// </remarks>
public enum TrustScope
{
    /// <summary>The installing org's OWN roster key — self-authored packs moving between the org's
    /// own instances (trust root (a)).</summary>
    OwnRoster = 0,

    /// <summary>The Harborline-Software channel key — channel-shipped packs (trust root (b)).
    /// Channel trust is IN v1 scope, which is what makes signing (not a checksum) non-optional.</summary>
    HarborlineChannel = 1,
}

/// <summary>
/// The lifecycle status of a trust root's <c>{key, epoch}</c> binding (ADR 0126 D4 sealed-epoch
/// model). A <see cref="Current"/> binding is the authoritative anchor; a <see cref="Retired"/>
/// binding is a sealed prior epoch — its signatures still VERIFY cryptographically, but the epoch is
/// no longer current, so a pack signed under it is <c>EpochUnverifiable</c>, never a false
/// <c>VerificationFailed</c> (S-11).
/// </summary>
public enum TrustRootStatus
{
    /// <summary>The current, authoritative epoch key for this scope.</summary>
    Current = 0,

    /// <summary>A sealed / retired prior epoch key — recognized (lineage), but not current.</summary>
    Retired = 1,
}

/// <summary>
/// One trust root: a scoped, epoch-tagged signing key the installing instance recognizes. A roster
/// that has rotated once carries TWO roots for the same <see cref="Scope"/> — the retired old
/// <c>{key, epoch}</c> and the current new one — so a pack signed under the old sealed epoch is still
/// recognized (as <c>EpochUnverifiable</c>) rather than mis-reported as tampered (S-11).
/// </summary>
/// <param name="Scope">What this root may vouch for (S-13).</param>
/// <param name="KeyId">The signing public key (the envelope <c>IssuerId</c> a pack must carry).</param>
/// <param name="Epoch">The epoch this key belongs to (ADR 0126 D4).</param>
/// <param name="Status">Whether this <c>{key, epoch}</c> is the current anchor or a sealed prior.</param>
public sealed record PackTrustRoot(
    TrustScope Scope,
    PrincipalId KeyId,
    long Epoch,
    TrustRootStatus Status);

/// <summary>How a <c>{key-id, epoch}</c> resolved against the trust store.</summary>
public enum PackTrustMatch
{
    /// <summary>No root recognizes this key at all — fail-closed refuse (S-1).</summary>
    None = 0,

    /// <summary>A root recognizes this key AND the claimed epoch is its CURRENT anchor.</summary>
    Current = 1,

    /// <summary>A root recognizes this key, but the claimed epoch is a sealed/retired prior (or an
    /// epoch this key is not current for) — epoch-unverifiable, NOT a tamper (S-11).</summary>
    RetiredOrUnverifiableEpoch = 2,
}

/// <summary>The outcome of a trust-store resolution: the match kind + the vouching scope (when a
/// key was recognized).</summary>
/// <param name="Match">Whether/how the key+epoch was recognized.</param>
/// <param name="Scope">The scope of the recognizing root, or <c>null</c> when
/// <see cref="Match"/> is <see cref="PackTrustMatch.None"/>.</param>
public sealed record PackTrustResolution(PackTrustMatch Match, TrustScope? Scope)
{
    /// <summary>The canonical "no root recognizes this signer" resolution.</summary>
    public static readonly PackTrustResolution NotTrusted = new(PackTrustMatch.None, null);
}
