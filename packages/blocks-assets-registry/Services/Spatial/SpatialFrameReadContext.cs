namespace Harborline.Api.Blocks.Assets.Registry.Services.Spatial;

/// <summary>
/// The caller-declared read posture every spatial-frame read carries — the store-level
/// enforcement seam for the <c>pii</c> policy binding's <c>Redact@Read</c> + <c>Audit@Read</c>
/// effects (CIC ruling 2026-08-06: enforcement is STORE-LEVEL and UNIFORM, so every consumer —
/// routes, exports, sync — inherits it; no per-route divergence).
/// </summary>
/// <remarks>
/// <para>
/// <b>The parameter is REQUIRED, never defaulted</b> — a read that does not state its posture does
/// not compile. Fail-closed composition: the cheap mistake is a loud compile error, never a silent
/// cleartext surface.
/// </para>
/// <para>
/// <b><see cref="Redacted"/></b> implements <c>Redact@Read</c>: the two governed PII cells
/// (<c>originDescription</c>, <c>georeference</c>) are WITHHELD — returned as <see langword="null"/>
/// without ever being decrypted, so no plaintext exists in memory and no audit row is owed
/// (audit rows are bounded to actual PII unsealing). Identity triple, axis convention, length
/// unit and the mint attestation (all cleartext columns) are returned as normal.
/// </para>
/// <para>
/// <b><see cref="PrivilegedUnseal"/></b> implements the authorized read: the governed cells are
/// unsealed and ONE <c>Audit@Read</c> record per unsealed row is appended to the registry audit
/// journal (<c>RegistryOp.SpatialFrameDescriptorPiiUnsealed</c> → canonical <c>Op.Read</c>),
/// carrying the identity triple ONLY — never the PII values. The append is FATAL on failure: a
/// read that cannot be audited is not surfaced (the PR 3716 grant-audit asymmetry — surfacing
/// PII IS the grant). Authorization itself (e.g. the route's <c>spatial:read</c> gate) is the
/// CALLER's obligation; this context is the caller's positive assertion that it was performed,
/// and <see cref="ActorRef"/> names the principal the audit row attributes the unsealing to.
/// </para>
/// </remarks>
public sealed record SpatialFrameReadContext
{
    private SpatialFrameReadContext(bool unsealsGoverned, string? actorRef)
    {
        UnsealsGoverned = unsealsGoverned;
        ActorRef = actorRef;
    }

    /// <summary>True when this read unseals the governed PII cells (and therefore audits).</summary>
    public bool UnsealsGoverned { get; }

    /// <summary>
    /// The principal the <c>Audit@Read</c> row attributes the unsealing to (non-null exactly when
    /// <see cref="UnsealsGoverned"/>). Scheme-prefixed: <c>principal:&lt;id&gt;</c> for a
    /// server-derived request principal (the normal case — e.g. the <c>ICurrentUser.UserId</c>
    /// behind the same validated token the permission check evaluated), <c>device:&lt;id&gt;</c>
    /// for the rare device-initiated substrate read (sync/maintenance) where no user principal
    /// exists. It MUST identify WHO initiated this read — never a static node id standing in for
    /// a user, which would attribute every unsealing to the machine (deep-review F4).
    /// </summary>
    public string? ActorRef { get; }

    /// <summary>The default, unprivileged posture — governed cells withheld, nothing decrypted,
    /// nothing audited.</summary>
    public static SpatialFrameReadContext Redacted { get; } = new(false, null);

    /// <summary>
    /// The authorized posture — unseal the governed cells and append one identity-triple-only
    /// audit record per unsealed row, attributed to <paramref name="actorRef"/>.
    /// </summary>
    /// <param name="actorRef">The already-authorized principal (never null/whitespace).</param>
    public static SpatialFrameReadContext PrivilegedUnseal(string actorRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorRef);
        return new(true, actorRef);
    }
}
