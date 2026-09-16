using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

public sealed record ProjectedCascadeDefaults(CascadeSource Source, CascadeDefaults Content);

/// <summary>Rebuildable tenant projection, populated only by the authorized pack projector.</summary>
public sealed class ActiveCascadeDefaultsProjection : ICascadeDefaultsProjection, IPackProjectionParticipant
{
    private readonly object sync = new();
    private Dictionary<TenantId, IReadOnlyList<ProjectedCascadeDefaults>> tenants = new();

    public void StageProjection(PackProjectionTransaction transaction) => transaction.Stage(this, () =>
    {
        var before = tenants;
        var next = new Dictionary<TenantId, IReadOnlyList<ProjectedCascadeDefaults>>(before);
        tenants = next;
        return () => tenants = before;
    });

    public IReadOnlyList<ProjectedCascadeDefaults> List(TenantId tenant)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read();
        lock (sync) return tenants.GetValueOrDefault(tenant) ?? [];
    }

    internal void Reconcile(TenantId tenant, IReadOnlySet<(string Pack, string Version, string Key)> active)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read();
        lock (sync) tenants[tenant] = Array.AsReadOnly(List(tenant).Where(row => active.Contains(
            (row.Source.PackId, row.Source.PackVersion, row.Source.ContentKey))).ToArray());
    }

    internal void Replace(TenantId tenant, string pack, IReadOnlyList<ProjectedCascadeDefaults> rows)
    {
        using var projectionLease = PackProjectionActivationBarrier.Read();
        if (rows.Any(row => row.Source.Tenant != tenant || row.Source.PackId != pack))
            throw new ArgumentException("Projection provenance must match its tenant and package.", nameof(rows));
        lock (sync) tenants[tenant] = Array.AsReadOnly(List(tenant).Where(row => row.Source.PackId != pack).Concat(rows).ToArray());
    }

    public ResolvedCascadeDefaults Resolve(TenantId tenant, string package, string recordType, string field)
    {
        var values = new CascadeValues();
        var sources = new Dictionary<string, CascadeSource>(StringComparer.Ordinal);
        var declarations = List(tenant).Where(row => row.Source.PackId == package)
            .SelectMany(row => row.Content.Defaults.Select(declaration => (row.Source, Declaration: declaration)))
            .Where(row => (row.Declaration.RecordType is null || row.Declaration.RecordType == recordType)
                && (row.Declaration.Field is null || row.Declaration.Field == field))
            .OrderBy(row => row.Declaration.RecordType is null ? 0 : row.Declaration.Field is null ? 1 : 2);
        foreach (var (source, declaration) in declarations)
        {
            var next = declaration.Values;
            void Declared(string axis, bool present) { if (present) sources[axis] = source; }
            Declared("classification", next.Classification is not null);
            Declared("personalData", next.PersonalData is not null);
            Declared("masking", next.Masking is not null);
            Declared("retention", next.Retention is not null);
            Declared("conflictPolicy", next.ConflictPolicy is not null);
            Declared("trackChanges", next.TrackChanges is not null);
            values = new(next.Classification ?? values.Classification, next.PersonalData ?? values.PersonalData,
                next.Masking ?? values.Masking, next.Retention ?? values.Retention,
                next.ConflictPolicy ?? values.ConflictPolicy, next.TrackChanges ?? values.TrackChanges);
        }
        return new(values, sources);
    }
}
