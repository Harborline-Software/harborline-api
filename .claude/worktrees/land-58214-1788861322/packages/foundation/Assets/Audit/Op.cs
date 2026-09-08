namespace Harborline.Api.Foundation.Assets.Audit;

/// <summary>
/// Canonical audit operation codes.
/// </summary>
/// <remarks>
/// Spec §3.3. Phase A surfaces the operations already used by the Entity + Hierarchy flows;
/// Phase B fills in the Transfer/Delegate/Revoke/Attest ownership events.
/// </remarks>
public enum Op
{
    /// <summary>Create (first version).</summary>
    Mint = 0,
    /// <summary>Read. Recording is opt-in per schema.</summary>
    Read = 1,
    /// <summary>Non-destructive update.</summary>
    Write = 2,
    /// <summary>Tombstone insertion (logical delete).</summary>
    Delete = 3,
    /// <summary>Ownership transfer — Phase B.</summary>
    Transfer = 4,
    /// <summary>Capability delegation — Phase B.</summary>
    Delegate = 5,
    /// <summary>Capability revocation — Phase B.</summary>
    Revoke = 6,
    /// <summary>Third-party attestation — Phase B.</summary>
    Attest = 7,
    /// <summary>Retroactive correction (§8.5).</summary>
    Correct = 8,
    /// <summary>Split composite (§8.2).</summary>
    Split = 9,
    /// <summary>Merge composite (§8.3).</summary>
    Merge = 10,
    /// <summary>Re-parent composite (§8.4).</summary>
    Reparent = 11,
    /// <summary>
    /// A guarded mutation was REJECTED and the attempt recorded (additive member, ADR 0101 Rev 3.2
    /// [A11]): a rejected authority-gated mint (e.g. a spatial-frame epoch conflict) is not a
    /// non-destructive update, so it must not be classified <see cref="Write"/>.
    /// </summary>
    Reject = 12,
}
