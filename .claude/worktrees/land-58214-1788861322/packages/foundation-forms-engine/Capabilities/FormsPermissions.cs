namespace Harborline.Api.Foundation.Forms.Engine.Capabilities;

/// <summary>
/// The permission strings the forms engine itself gates on. They follow the shipped
/// <c>family:verb</c> vocabulary (<c>Permission.cs</c> in foundation-identity-atlas) and are
/// checked against <see cref="CapabilityToken.Roles"/>; the Harborline App choice (roles list versus a
/// resolved per-request permission set) is pending ADR 0163 D3 ratification.
/// </summary>
public static class FormsPermissions
{
    /// <summary>
    /// Grants decrypt-on-render of a Sensitive / encrypted-at-rest field value
    /// (ADR 0055 Rev 10 OQ-A; ADR 0168 D4 spatial read-back). Follows the shipped
    /// <c>family:verb</c> permission vocabulary. FAIL-CLOSED:
    /// a token without this permission renders exactly the pre-capability withhold
    /// (<c>IsReadable = false</c>, <c>Value = null</c>), and every successful decrypt is
    /// audited (<c>Op.Read</c> with a <c>decrypt-on-render</c> payload).
    /// </summary>
    public const string DecryptSensitive = "forms:decrypt-sensitive";

    /// <summary>
    /// The <see cref="Harborline.Api.Foundation.Crypto.IDecryptCapabilityProvider"/> purpose key under
    /// which the engine acquires its short-lived decrypt capability for a render.
    /// </summary>
    public const string DecryptOnRenderPurpose = "forms-decrypt-on-render";
}
