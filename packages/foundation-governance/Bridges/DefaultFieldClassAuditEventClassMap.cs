using Harborline.Api.Foundation.Governance.Enforcement;
using Harborline.Api.Foundation.SecurityPolicy.Models;

namespace Harborline.Api.Foundation.Governance.Bridges;

/// <summary>
/// Default field-class → <see cref="AuditEventClass"/> map. Parses the floor-class token
/// against the <see cref="AuditEventClass"/> enum (case-insensitive), with an optional
/// override map for non-identical names. An unmappable token throws fail-closed.
/// </summary>
public sealed class DefaultFieldClassAuditEventClassMap : IFieldClassAuditEventClassMap
{
    private readonly IReadOnlyDictionary<string, AuditEventClass> _overrides;

    /// <summary>Construct with optional explicit overrides (e.g. <c>"pci" → Financial</c>).</summary>
    public DefaultFieldClassAuditEventClassMap(IReadOnlyDictionary<string, AuditEventClass>? overrides = null)
        => _overrides = overrides ?? new Dictionary<string, AuditEventClass>(StringComparer.OrdinalIgnoreCase)
        {
            ["pii"] = AuditEventClass.Identity,
            ["phi"] = AuditEventClass.Identity,
            ["pci"] = AuditEventClass.Financial,
            ["cui"] = AuditEventClass.Security,
        };

    /// <inheritdoc />
    public AuditEventClass Resolve(string floorClass)
    {
        if (string.IsNullOrWhiteSpace(floorClass))
        {
            throw new GovernanceConfigurationException(
                "Retain effect has no floor-class; cannot resolve an AuditEventClass (no silent default window).");
        }
        if (_overrides.TryGetValue(floorClass, out var mapped)) return mapped;
        if (Enum.TryParse<AuditEventClass>(floorClass, ignoreCase: true, out var parsed)) return parsed;

        throw new GovernanceConfigurationException(
            $"Retain floor-class '{floorClass}' maps to no AuditEventClass " +
            "(Security|Financial|Identity|Configuration|System). Add an explicit mapping — " +
            "no silent default retention window is permitted.");
    }
}
