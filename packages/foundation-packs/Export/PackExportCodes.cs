namespace Harborline.Api.Foundation.Packs.Export;

/// <summary>
/// Stable, locale-independent export refusal CODES the exporter itself raises (ticket 394: every pack refusal code
/// is declared by a <c>*Codes</c> catalogue). Same discipline as <c>PackValidationCodes</c> and
/// <c>DcpValidationCodes</c>: findings are carried as <c>PackValidationError</c>, so they reach the export outcome
/// and the route in the one error shape, and the client localizes off the CODE.
/// </summary>
public static class PackExportCodes
{
    /// <summary>
    /// Goal 3 P3: the request's exposed-definition entries are malformed, so nothing is signed. They are present
    /// without an exposure (<c>Exposes</c> and <c>InterfaceVersion</c>), empty, or an entry is null, names a key the
    /// pack does not expose, repeats a key, states a blank version, or states a digest other than <c>sha256:</c> and
    /// 64 lowercase hex digits. PROVISIONAL spelling, pending owner confirmation (Goal 3 open question O-6).
    /// </summary>
    public const string ExposedDefinitionMalformed = "pack.export.exposed_definition_malformed";
}
