using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>Rebuildable tenant terminology supplied only by the active pack composition dispatcher.</summary>
public sealed class TerminologyProjection : IPackProjectionParticipant
{
    private readonly object sync = new();
    private Dictionary<(TenantId Tenant, string Id), Entry> entries = new();

    public void StageProjection(PackProjectionTransaction transaction) => transaction.Stage(this, () =>
    {
        var before = entries;
        var next = new Dictionary<(TenantId Tenant, string Id), Entry>(before);
        entries = next;
        return () => entries = before;
    });
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Removes inactive, superseded and non-owner rows before refreshing the authorized pack.</summary>
    internal void BeginProjection(TenantId tenant, string packKey, string packVersion, IReadOnlySet<(string Pack, string Version, string Id)> active)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read();
        lock (sync)
        {
            foreach (var row in entries.Where(row => row.Key.Tenant == tenant
                         && ((row.Value.PackKey == packKey && row.Value.PackVersion == packVersion)
                             || !active.Contains((row.Value.PackKey, row.Value.PackVersion, row.Key.Id)))).ToArray())
                entries.Remove(row.Key);
        }
    }

    /// <summary>Admits the final seed-plus-override body before making its typed row visible.</summary>
    internal string? AdmitTerminology(TenantId tenant, string packVersion, PackComposedItem item)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read();
        var code = PackTerminologyContent.TryReadTerminology(item, tenant, out var content);
        if (code is not null) return code;
        lock (sync) entries[(tenant, item.Key)] = new Entry(content!, item.PackageKey, packVersion);
        return null;
    }

    /// <summary>Resolves a tenant stable identity without changing its addressing when wording changes.</summary>
    public TerminologyResolution? Resolve(TenantId tenant, string stableId, string userLocale)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read();
        lock (sync)
            return entries.TryGetValue((tenant, stableId), out var entry)
                ? PackTerminologyContent.Resolve(entry.Content, userLocale) : null;
    }

    /// <summary>Reads the admitted active projection with its supplying package coordinates.</summary>
    public IReadOnlyList<CatalogueEntry> List(TenantId tenant)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read();
        lock (sync)
            return entries.Where(row => row.Key.Tenant == tenant).OrderBy(row => row.Key.Id, StringComparer.Ordinal)
                .Select(row => new CatalogueEntry(row.Key.Id, row.Value.Content.Version, PackContentKind.TerminologyOverride,
                    new InternationalizedTextDto(row.Value.Content.DefaultLocale,
                        row.Value.Content.PackageTranslations.Keys.Concat(row.Value.Content.TenantOverrides.Keys)
                            .Distinct(StringComparer.Ordinal).ToDictionary(locale => locale,
                                locale => PackTerminologyContent.Resolve(row.Value.Content, locale).Text, StringComparer.Ordinal)),
                    new CatalogueProvenance(row.Value.PackKey, row.Value.PackVersion, "pack"), false, "Published",
                    DateTimeOffset.MinValue, JsonSerializer.SerializeToElement(row.Value.Content, Json))).ToArray();
    }

    private sealed record Entry(TerminologyContent Content, string PackKey, string PackVersion);
}
