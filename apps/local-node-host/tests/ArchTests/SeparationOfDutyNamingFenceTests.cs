using System.Reflection;
using System.Runtime.Loader;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 272 slice 4 — only the decider is NAMED for separation of duty.
/// <para>
/// ADR 0067 clause 2 has two halves: the check that decides a duty conflict, and the log "where an auditor
/// reads it". Before this slice the only component named for separation of duty was the enrollment audit
/// recorder — the second half wearing the first half's name — and 222 slice 1 read that name as an engine
/// that did not exist. A name that claims a decision the type does not make is the defect this fence
/// prevents from coming back.
/// </para>
/// <para>
/// The inventory is discovered by REFLECTION over every shipped production <c>Harborline*.dll</c>: every
/// type (nested included) whose name carries the separation-of-duty spelling. Each one must be an exact
/// allow-list row below — the engine, the decision it returns, or the audit trail's faithful mirror of
/// that decision. A recorder, a sink, a writer or a route that takes the name again is red at build time.
/// </para>
/// </summary>
public sealed class SeparationOfDutyNamingFenceTests
{
    /// <summary>
    /// The exact set of types allowed to carry the name, with the reason each is the decision and not a
    /// recording of it. A row the scan no longer finds fails <see cref="AllowListVacuityArchTests"/>.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyEngine"] =
            "the decider itself — the one place ADR 0067 clause 2 is evaluated",
        ["Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyDecision"] =
            "the one decision object the engine returns",
        ["Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyRequest"] =
            "everything the engine decides from; no other input exists",
        ["Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyFact"] =
            "the decided verdict carried on the decision",
        ["Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyOutcome"] =
            "the decided outcome's three members",
        ["Harborline.Api.Kernel.Audit.SeparationOfDutySnapshot"] =
            "the audit trail's stored spelling of the decided verdict — a field-for-field mirror of the "
            + "engine's fact, mapped at the one seam, deciding nothing",
        ["Harborline.Api.Kernel.Audit.SeparationOfDutyResult"] =
            "the stored mirror of SeparationOfDutyOutcome, member for member and number for number",
    };

    /// <summary>Allow-list rows, for the vacuity registry.</summary>
    internal static string[] AllowedRows() => [.. Allowed.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The scan with the allow-list bypassed, for the vacuity registry.</summary>
    internal static string[] DiscoveredSeparationOfDutyNamedTypes() => Named.Value;

    /// <summary>True for a type name that claims the separation-of-duty decision.</summary>
    internal static bool ClaimsTheName(string typeName) =>
        typeName.Contains("SeparationOfDuty", StringComparison.Ordinal)
        || typeName.Contains("SoD", StringComparison.Ordinal)
        || typeName.Contains("SeparationOfDuties", StringComparison.Ordinal);

    // Cached: the vacuity registry re-runs every registered discovery many times over, and the build output
    // does not change mid-run.
    private static readonly Lazy<string[]> Named = new(Scan, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string[] Scan()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .GroupBy(assembly => assembly.GetName().Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var found = new List<string>();
        foreach (var path in Directory
                     .EnumerateFiles(AppContext.BaseDirectory, "Harborline*.dll")
                     .Where(path => !Path.GetFileName(path).Contains("Tests", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(path).Contains("Analyzers", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(path).Contains("Tooling", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            var simpleName = Path.GetFileNameWithoutExtension(path);
            Assembly assembly;
            try
            {
                assembly = loaded.TryGetValue(simpleName, out var existing)
                    ? existing
                    : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            catch (Exception)
            {
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.Where(type => type is not null)!];
            }

            found.AddRange(types
                .Where(type => ClaimsTheName(type.Name))
                .Select(type => type.FullName ?? type.Name));
        }

        return [.. found.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    [Fact(DisplayName = "272: only the separation-of-duty decision is NAMED for separation of duty")]
    public void No_type_outside_the_engine_carries_the_name()
    {
        var offenders = Named.Value.Where(name => !Allowed.ContainsKey(name)).ToArray();

        Assert.True(
            offenders.Length == 0,
            "A type carries the separation-of-duty name without deciding one. A recorder records; the "
            + "engine decides. Rename it, or add an exact row to Allowed with the reason it IS the "
            + "decision:\n  " + string.Join("\n  ", offenders));
    }

    [Fact(DisplayName = "272: the scan actually finds the engine, so an empty offender set means something")]
    public void The_scan_finds_the_engine()
    {
        Assert.Contains(
            "Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyEngine",
            Named.Value,
            StringComparer.Ordinal);
        Assert.Equal(Allowed.Count, Named.Value.Length);
    }

    [Fact(DisplayName = "272: the retired enrollment-recorder spellings would be caught by this fence")]
    public void The_retired_recorder_spellings_are_offending_names()
    {
        // The names this slice retired. If the classifier stopped recognising them the fence would be
        // green while the very type that motivated it came back.
        Assert.True(ClaimsTheName("ISoDAuditSink"));
        Assert.True(ClaimsTheName("KernelAuditSoDSink"));
        Assert.True(ClaimsTheName("SoDAuditControlComposition"));
        Assert.True(ClaimsTheName("SoDCompensatingControlPayloads"));
        Assert.True(ClaimsTheName("SeparationOfDutiesRecorder"));

        // And the names it landed are not offenders, so the rename is a real escape and not a spelling game.
        Assert.False(ClaimsTheName("IEnrollmentCompensatingControlRecorder"));
        Assert.False(ClaimsTheName("KernelAuditEnrollmentCompensatingControlRecorder"));
    }
}
