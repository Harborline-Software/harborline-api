namespace Harborline.Api.Foundation.UI;

/// <summary>
/// Canonical sync-state enum per ADR 0036's encoding contract (A1.1).
/// 5-value set; PascalCase form of the canonical lowercase identifiers
/// (<c>healthy</c> / <c>stale</c> / <c>offline</c> / <c>conflict</c> /
/// <c>quarantine</c>). Round-trips via
/// <see cref="Harborline.Api.Foundation.Crypto.CanonicalJson.Serialize"/> as
/// the lowercase string forms (per A1.2) when paired with the
/// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// configured with <see cref="System.Text.Json.JsonNamingPolicy.CamelCase"/>
/// — single-word identifiers are flat-case-identical, producing the
/// lowercase canonical wire form.
/// </summary>
public enum SyncState
{
    /// <summary>The last peer exchange succeeded and the peer is reachable; currentness is not established. Canonical identifier <c>"healthy"</c>.</summary>
    Healthy,

    /// <summary>The last peer exchange is past the configured display threshold; currentness is not established. Canonical identifier <c>"stale"</c>.</summary>
    Stale,

    /// <summary>The peer / replica is currently unreachable. Canonical identifier <c>"offline"</c>.</summary>
    Offline,

    /// <summary>A merge produced a conflict that needs operator review. Canonical identifier <c>"conflict"</c>.</summary>
    Conflict,

    /// <summary>The replica has been quarantined (e.g., after a security or integrity violation). Canonical identifier <c>"quarantine"</c>.</summary>
    Quarantine,
}
