namespace Harborline.Api.Foundation.CapabilityAdmission;

/// <summary>Code-owned minimum inventory baselines for security-sensitive capability contracts.</summary>
internal static class CapabilityInventoryProfiles
{
    private static readonly CapabilityKey MultiTenantWeb = new(
        CapabilityId.Of("identity.multi-tenant-web"),
        CapabilityVersion.Of("v1"));

    /// <summary>
    /// Gate for the ADR 0169 D4 'Harborline App host permissions' required-non-empty baseline addition.
    /// FALSE until the Harborline App HostPermissions emitter exists (ADR 0169 checklist item on
    /// capability-directory enumeration): per ADR 0169 edit 5, the Validate requirement must land
    /// WITH the emitter or gated on it, never ahead of it. Flip to true in the same change that
    /// ships the Harborline App emitter — a Harborline App bundle then always carries at least 'core:default'.
    /// </summary>
    internal const bool RequireReferenceAppHostPermissions = false;

    public static void Validate(ReleaseCapabilityEntry entry) =>
        Validate(entry, RequireReferenceAppHostPermissions);

    internal static void Validate(ReleaseCapabilityEntry entry, bool requireReferenceAppHostPermissions)
    {
        if (entry.Key != MultiTenantWeb)
        {
            return;
        }

        var clientBundle = entry.ExecutableInventories.SingleOrDefault(x =>
            string.Equals(x.ArtifactName, "carrier.bundle", StringComparison.Ordinal));
        var node = entry.ExecutableInventories.SingleOrDefault(x =>
            string.Equals(x.ArtifactName, "local-node", StringComparison.Ordinal));
        if (clientBundle is null || node is null)
        {
            throw new CapabilityCatalogAdmissionException(
                CapabilityRefusalCodes.ManifestMismatch,
                $"Capability '{entry.Key}' requires both 'carrier.bundle' client and " +
                "'local-node' authority inventories.");
        }

        var missing = new List<string>();
        Require(missing, "client bundle routes", clientBundle.Executables.Routes.Count != 0);
        Require(missing, "client bundle controls", clientBundle.Executables.Controls.Count != 0);
        Require(missing, "client bundle state stores", clientBundle.Executables.StateStores.Count != 0);
        if (requireReferenceAppHostPermissions)
        {
            Require(
                missing,
                "client bundle host permissions",
                clientBundle.Executables.HostPermissions.Count != 0);
        }
        Require(missing, "node routes", node.Executables.Routes.Count != 0);
        Require(missing, "node commands", node.Executables.Commands.Count != 0);
        Require(missing, "node handlers", node.Executables.Handlers.Count != 0);
        Require(missing, "node migrations", node.Executables.Migrations.Count != 0);
        Require(missing, "node bundled aliases", node.Executables.BundledAliases.Count != 0);
        Require(missing, "node state stores", node.Executables.StateStores.Count != 0);
        Require(missing, "node background workers", node.Executables.BackgroundWorkers.Count != 0);
        Require(missing, "node data families", node.Executables.DataFamilies.Count != 0);
        Require(missing, "node caches", node.Executables.Caches.Count != 0);
        Require(missing, "node search indexes", node.Executables.SearchIndexes.Count != 0);
        Require(missing, "node file stores", node.Executables.FileStores.Count != 0);
        Require(missing, "node report exports", node.Executables.ReportExports.Count != 0);
        Require(missing, "node audit streams", node.Executables.AuditStreams.Count != 0);
        Require(missing, "node jobs", node.Executables.Jobs.Count != 0);
        if (missing.Count != 0)
        {
            throw new CapabilityCatalogAdmissionException(
                CapabilityRefusalCodes.ManifestMismatch,
                $"Capability '{entry.Key}' is missing required inventory families: " +
                $"{string.Join(", ", missing)}.");
        }
    }

    private static void Require(ICollection<string> missing, string family, bool present)
    {
        if (!present)
        {
            missing.Add(family);
        }
    }
}
