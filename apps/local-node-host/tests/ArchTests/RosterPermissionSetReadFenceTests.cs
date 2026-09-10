using System.Reflection;

using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Tests.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 293 slice 5 — NO production code reads a permission SET off the roster.
/// <para>
/// The M1 milestone exit test is that a search finds no production read of a roster member's permission
/// set. The reason is not tidiness: since slice 3b2 the roster wire record carries no permission set, so a
/// REPLICATED member has none. A production reader of <c>MemberRoster.PermissionsOf</c> therefore answers
/// the empty set for exactly the members it is asked about — silently, and in the fail-OPEN direction for
/// an audit row (it records "granted nothing") and the fail-CLOSED direction for a display (it blanks the
/// app). Slice 4 moved the answer to <c>AuthorizationGate</c> over the local grant store; the roster's
/// remaining contribution is membership and ejection, which are booleans and are welcome.
/// </para>
/// <para>
/// The fence discovers its inventory BY SYMBOL — the same IL walk over every shipped
/// <c>Harborline*.dll</c> that the audit-append and separation-of-duty fences use — so an alias, a local,
/// an extension-looking call or a reformat does not make a reader disappear. The allow-list has ZERO rows
/// and is registered in <see cref="AllowListVacuityArchTests"/>: there is no justified production reader,
/// and a row added to excuse one must name a call site the scan really finds.
/// </para>
/// </summary>
public sealed class RosterPermissionSetReadFenceTests
{
    private const string PermissionsOfMethod =
        "Harborline.Api.Foundation.IdentityAtlas.MemberRoster.PermissionsOf";
    private const string HasPermissionMethod =
        "Harborline.Api.Foundation.IdentityAtlas.MemberRoster.HasPermission";

    /// <summary>
    /// Production readers of a roster member's permission set, as <c>file|symbol|callee</c>. EMPTY, and
    /// intended to stay empty: what a party may do is the gate's answer over the grant store, and the
    /// roster answers only whether the party is a live member. A row here would be a decision taken
    /// outside the gate on data a replicated member does not carry.
    /// </summary>
    private static readonly Dictionary<string, string> ForbiddenRosterSetReaders = new(StringComparer.Ordinal);

    /// <summary>Allow-list rows, for the vacuity registry.</summary>
    internal static string[] ForbiddenRosterSetReaderRows() =>
        [.. ForbiddenRosterSetReaders.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The scan with the allow-list bypassed, for the vacuity registry.</summary>
    internal static string[] DiscoveredRosterSetReads() => Sites.Value;

    // Cached: the vacuity registry re-runs each registered discovery many times across several threads.
    private static readonly Lazy<string[]> Sites = new(
        () => Discover(Classify),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string[]> MembershipSites = new(
        () => Discover(method =>
            method.DeclaringType == typeof(MemberRoster) && method.Name == nameof(MemberRoster.Contains)
                ? "Harborline.Api.Foundation.IdentityAtlas.MemberRoster.Contains"
                : null),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static string[] Discover(Func<MethodBase, string?> classify) =>
        [.. AuditAppendSymbolInventory.Discover(classify)
            .Select(site => $"{site.File}|{site.Symbol}|{site.Kind}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>A read of the SIGNED set on a roster member. A membership or ejection predicate is not one.</summary>
    internal static string? Classify(MethodBase method) =>
        method.DeclaringType != typeof(MemberRoster)
            ? null
            : method.Name switch
            {
                "PermissionsOf" => PermissionsOfMethod,
                "HasPermission" => HasPermissionMethod,
                _ => null,
            };

    [Fact(DisplayName = "293 s5: no production code reads a permission set off the roster")]
    public void No_production_call_site_reads_a_roster_permission_set()
    {
        var offenders = Sites.Value.Where(site => !ForbiddenRosterSetReaders.ContainsKey(site)).ToArray();

        Assert.True(
            offenders.Length == 0,
            "Production code reads a roster member's permission SET. A replicated member carries none "
            + "(ticket 293 slice 3b2), so this answers the empty set for the members it is asked about. Ask "
            + "AuthorizationGate over the grant store instead, and take membership/ejection from "
            + "EffectiveMemberPermissions.Read. Offending sites:\n  " + string.Join("\n  ", offenders));
        Assert.Empty(ForbiddenRosterSetReaders);
    }

    [Fact(DisplayName = "293 s5: the roster set readers are not reachable from production at all")]
    public void The_roster_set_readers_are_internal_to_the_atlas()
    {
        // Belt to the fence's braces: the compiler keeps a production assembly from calling these at all.
        // Only the host test assembly sees them (InternalsVisibleTo), which is why this test can name them.
        foreach (var name in new[] { "PermissionsOf", "HasPermission" })
        {
            var method = typeof(MemberRoster).GetMethod(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            Assert.False(method!.IsPublic, $"MemberRoster.{name} is public again; production can read the set.");
            Assert.True(method.IsAssembly, $"MemberRoster.{name} must be internal to the atlas assembly.");
        }
    }

    [Fact(DisplayName = "293 s5: the set-read scan is not vacuous — the same walk finds the membership predicate")]
    public void The_scan_resolves_roster_call_sites()
    {
        // Anti-vacuity. An empty offender list is only evidence if the walk can find a roster call at all;
        // if call-token resolution broke, the fence above would pass over nothing forever. MemberRoster
        // .Contains is the predicate production is SUPPOSED to use, so it must be found, and in numbers.
        Assert.NotEmpty(MembershipSites.Value);
        Assert.Contains(
            MembershipSites.Value,
            site => site.Contains("EffectiveMemberPermissions", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "293 s5: a planted production set read is rejected")]
    public void A_planted_set_read_is_rejected()
    {
        // The mutation the fence exists for, run in-test rather than committed, so it runs every build.
        const string planted =
            "apps/local-node-host/Health/WebSession/SelectedSessionIdentityRoutes.cs"
            + "|Harborline.Api.LocalNodeHost.Health.WebSession.SelectedSessionIdentityRoutes.PermissionsAsync()"
            + ": System.Threading.Tasks.Task`1[Microsoft.AspNetCore.Http.IResult]"
            + "|" + PermissionsOfMethod;

        var offenders = Sites.Value.Append(planted)
            .Where(site => !ForbiddenRosterSetReaders.ContainsKey(site)).ToArray();

        Assert.Equal([planted], offenders);
    }
}
