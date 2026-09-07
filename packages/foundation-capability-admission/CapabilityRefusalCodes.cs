namespace Harborline.Api.Foundation.CapabilityAdmission;

/// <summary>Stable, locale-independent ADR 0154 refusal vocabulary.</summary>
public static class CapabilityRefusalCodes
{
    /// <summary>The signed release does not declare the requested capability.</summary>
    public const string Uncompiled = "capability.uncompiled";
    /// <summary>The release declaration and executable runtime inventory disagree.</summary>
    public const string ManifestMismatch = "capability.manifest_mismatch";
    /// <summary>No reviewed runtime registration was admitted for the compiled capability.</summary>
    public const string NotAdmitted = "capability.not_admitted";
    /// <summary>The admitted runtime is not currently usable.</summary>
    public const string NotReady = "capability.not_ready";
    /// <summary>A required entitlement, DCP, pack, hardware, or other prerequisite is inactive.</summary>
    public const string PrerequisiteInactive = "capability.prerequisite_inactive";
    /// <summary>The tenant has disabled the capability for ordinary actions.</summary>
    public const string TenantDisabled = "capability.tenant_disabled";
    /// <summary>The tenant capability is draining consequential work before becoming disabled.</summary>
    public const string Disabling = "capability.disabling";
    /// <summary>The current principal may not discover or perform the requested action.</summary>
    public const string PermissionDenied = "capability.permission_denied";
}
