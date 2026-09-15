using Harborline.Api.Foundation.Governance.Resolution;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>Checks the publisher ceiling after inheritance, including undeclared residual scopes.</summary>
internal static class CascadeDefaultsRestrictionCheck
{
    internal const string Refused = "pack.defaults.relax_forbidden";

    internal static bool Preserves(CascadeDefaults seed, CascadeDefaults composed)
    {
        var coordinates = seed.Defaults.Concat(composed.Defaults)
            .SelectMany(declaration => new[] { (declaration.RecordType, declaration.Field), (declaration.RecordType, (string?)null) })
            .Append((null, null)).Distinct();
        foreach (var (recordType, field) in coordinates)
        {
            var before = Resolve(seed, recordType, field);
            var after = Resolve(composed, recordType, field);
            if (before.Classification?.Any(tag => after.Classification?.Any(candidate => candidate.System == tag.System && candidate.Code == tag.Code) != true) == true
                || (before.PersonalData == true && after.PersonalData != true)
                || (before.TrackChanges == true && after.TrackChanges != true)
                || (before.Masking is { } mask && (after.Masking is null || after.Masking.RevealLast > mask.RevealLast))
                || (before.ConflictPolicy is not null && after.ConflictPolicy != before.ConflictPolicy)
                || (before.Retention is { } retention && (after.Retention is null
                    || after.Retention.Regime != retention.Regime || after.Retention.FloorClass != retention.FloorClass
                    || after.Retention.MinimumRetentionDays < retention.MinimumRetentionDays))) return false;
        }
        return true;
    }

    private static CascadeValues Resolve(CascadeDefaults content, string? recordType, string? field)
    {
        var values = new CascadeValues();
        foreach (var declaration in content.Defaults.Where(declaration =>
                     (declaration.RecordType is null || declaration.RecordType == recordType)
                     && (declaration.Field is null || declaration.Field == field))
                 .OrderBy(declaration => declaration.RecordType is null ? 0 : declaration.Field is null ? 1 : 2))
        {
            var next = declaration.Values;
            values = new(next.Classification ?? values.Classification, next.PersonalData ?? values.PersonalData,
                next.Masking ?? values.Masking, next.Retention ?? values.Retention,
                next.ConflictPolicy ?? values.ConflictPolicy, next.TrackChanges ?? values.TrackChanges);
        }
        return values;
    }
}
