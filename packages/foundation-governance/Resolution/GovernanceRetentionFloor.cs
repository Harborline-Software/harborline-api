using Harborline.Api.Foundation.SecurityPolicy.Models;
using Harborline.Api.Foundation.SecurityPolicy.Retention;

namespace Harborline.Api.Foundation.Governance.Resolution;

/// <summary>Combines declared floors with the tenant verdict without shortening either hold.</summary>
public static class GovernanceRetentionFloor
{
    public static bool SupportsRegime(string regime) => regime is "HIPAA" or "PCI_DSS_v4" or "SOC2" or "GDPR" or "EU_AI_Act";

    public static RetentionVerdict Apply(RetentionVerdict verdict, DateTimeOffset created, string? regime, int? minimumDays)
    {
        var preset = regime switch
        {
            "HIPAA" => RetentionJurisdictionPreset.HipaaInformedDefault,
            "PCI_DSS_v4" => RetentionJurisdictionPreset.PciDssInformedDefault,
            "SOC2" => RetentionJurisdictionPreset.Soc2InformedDefault,
            "GDPR" => RetentionJurisdictionPreset.GdprInformedDefault,
            "EU_AI_Act" => RetentionJurisdictionPreset.EuAiActInformedDefault,
            _ => RetentionJurisdictionPreset.Custom,
        };
        var declared = Until(created, minimumDays ?? 0);
        var minimum = declared > verdict.MinimumHoldUntil ? declared : verdict.MinimumHoldUntil;
        var jurisdiction = Until(created, JurisdictionFloorHelper.GetFloor(preset, verdict.EventClass)?.TotalDays ?? 0);
        var jurisdictionRaised = jurisdiction > minimum;
        if (jurisdictionRaised) minimum = jurisdiction;
        return verdict with
        {
            MinimumHoldUntil = minimum,
            MaximumHoldUntil = verdict.MaximumHoldUntil < minimum ? minimum : verdict.MaximumHoldUntil,
            IsJurisdictionFloor = verdict.IsJurisdictionFloor || jurisdictionRaised,
        };
    }

    private static DateTimeOffset Until(DateTimeOffset created, double days) =>
        days >= (DateTimeOffset.MaxValue - created).TotalDays ? DateTimeOffset.MaxValue : created.AddDays(Math.Max(0, days));
}
