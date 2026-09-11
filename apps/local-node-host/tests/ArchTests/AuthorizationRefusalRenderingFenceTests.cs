using System.Reflection;
using System.Runtime.Loader;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Ship.Common;
using Harborline.Api.LocalNodeHost.Tests.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 214 slice 1 — a refusal becomes words in ONE place.
/// <para>
/// Ledger L656: a refusal must not disclose a record field the acting principal cannot read. That holds
/// only while every refusal is rendered by <see cref="AuthorizationRefusalRenderer"/>, which asks ticket
/// 205's evaluator per field. Two ways round it are fenced here:
/// </para>
/// <list type="number">
///   <item>reading <c>PermissionDecision.Denied.ReasonDisplay</c> or <c>.Remediation</c> — arbitrary
///     resolver-authored prose — anywhere outside the renderer;</item>
///   <item>catching <see cref="AuthorizationDeniedException"/>, whose message and decision are the
///     unfiltered reading, in a route or writer that could serialize it.</item>
/// </list>
/// <para>Both inventories are discovered by SYMBOL over the shipped assemblies and must equal an exact
/// allow-list, each row carrying its reason.</para>
/// </summary>
public sealed class AuthorizationRefusalRenderingFenceTests
{
    private const string ReasonDisplayGetter =
        "Harborline.Api.Foundation.Ship.Common.PermissionDecision+Denied.get_ReasonDisplay";
    private const string RemediationGetter =
        "Harborline.Api.Foundation.Ship.Common.PermissionDecision+Denied.get_Remediation";

    /// <summary>
    /// Every production read of the denial prose today, as <c>file|symbol|callee</c>, with the reason it is
    /// not a disclosure. A row the scan stops finding fails <see cref="AllowListVacuityArchTests"/>.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["packages/foundation-ship-common/DefaultPermissionResolver.cs"
            + "|Harborline.Api.Foundation.Ship.Common.DefaultPermissionResolver.EmitDenialAsync("
            + "Harborline.Foundation.Assets.Common.TenantId,"
            + "Harborline.Api.Foundation.Assets.Common.ActorId,"
            + "Harborline.Api.Foundation.Ship.Common.ShipLocation,"
            + "Harborline.Api.Foundation.Ship.Common.PermissionDecision+Denied,"
            + "Harborline.Api.Foundation.Ship.Common.ShipAction,System.DateTimeOffset,"
            + "System.Threading.CancellationToken): System.Threading.Tasks.ValueTask"
            + "|" + RemediationGetter] =
            "the AUDIT row, not a response: it records the remediation KIND (an enum) on the internal "
            + "PermissionDenied entry, which ticket 214 deliberately keeps classified rather than filtered",
    };

    /// <summary>
    /// Every production type that catches the gate's denial exception, with the reason it cannot leak.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedCatchers = new(StringComparer.Ordinal)
    {
        ["Harborline.Api.LocalNodeHost.Health.AuthorizationAdminRoutes"] =
            "Binding writes render and audit the writer decision through RequestAuthorization.RefusedAsync",
        ["Harborline.Api.LocalNodeHost.FormsDevSeeder"] =
            "a development seeder: it logs and skips, and serves no request",
        ["Harborline.Api.LocalNodeHost.FormsShowcaseDevSeeder"] =
            "a development seeder: it logs and skips, and serves no request",
        ["Harborline.Api.LocalNodeHost.LivingStandardCatalogDevSeeder"] =
            "a development seeder: it logs and skips, and serves no request",
        ["Harborline.Api.LocalNodeHost.Health.WorkflowDefinitionRoutes"] =
            "ticket 214 slice 2: the workflow-definition PUT authorizes inside its store, so it meets the "
            + "refusal as an exception; both catches hand the carried DECISION to "
            + "RequestAuthorization.RefusedAsync, which renders it through AuthorizationRefusalRenderer and "
            + "audits it. The exception's message is never read",
        ["Harborline.Api.LocalNodeHost.Health.WebSession.AdminTeamAccessRoutes"] =
            "ticket 293 slice 1: apps/local-node-host/Health/WebSession/AdminTeamAccessRoutes.cs: "
            + "IssueInvitationAsync: the denial is rendered through the read-filtered refusal renderer "
            + "and its message never reaches the wire; "
            + "RevokeMemberAsync: the denial is rendered through the read-filtered refusal renderer "
            + "and its message never reaches the wire; "
            + "UpdateMemberPermissionsAsync: the denial is rendered through the read-filtered refusal renderer "
            + "and its message never reaches the wire. All three catches hand the exception to "
            + "RequestAuthorization.RefusedAsync, which renders it through AuthorizationRefusalRenderer and "
            + "audits it. The exception's message is never read",
        ["Harborline.Api.LocalNodeHost.Health.AuthorizationDenialTranslation"] =
            "ticket 380 slice 1: the node's one translation of a denial a handler met as an exception. It "
            + "hands the CARRIED decision to RequestAuthorization.RefusedAsync, which renders it through "
            + "AuthorizationRefusalRenderer and audits it; the exception's message is never read, and "
            + "nothing is re-decided",
        ["Harborline.Api.Foundation.Packs.Install.PackInstaller"] =
            "swallows the denial to fail the install step closed; it writes no response text",
    };

    /// <summary>Allow-list rows, for the vacuity registry.</summary>
    internal static string[] AllowedRows() => [.. Allowed.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Catcher allow-list rows, for the vacuity registry.</summary>
    internal static string[] AllowedCatcherRows() => [.. AllowedCatchers.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The prose-read scan with the allow-list bypassed, for the vacuity registry.</summary>
    internal static string[] DiscoveredDenialProseReads() => Reads.Value;

    /// <summary>The catch scan with the allow-list bypassed, for the vacuity registry.</summary>
    internal static string[] DiscoveredDenialExceptionCatchers() => Catchers.Value;

    private static readonly Lazy<string[]> Reads = new(
        () => AuditAppendSymbolInventory
            .Discover(Classify)
            .Select(site => site.File + "|" + site.Symbol + "|" + site.Kind)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string[]> Catchers = new(
        () => DiscoverCatchers().Order(StringComparer.Ordinal).ToArray(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>A read of the denial prose. Only the two display members count.</summary>
    internal static string? Classify(MethodBase method) =>
        method.DeclaringType == typeof(PermissionDecision.Denied)
            ? method.Name switch
            {
                "get_ReasonDisplay" => ReasonDisplayGetter,
                "get_Remediation" => RemediationGetter,
                _ => null,
            }
            : null;

    [Fact(DisplayName = "214 s1: the denial prose is read only where an allow-list row says why")]
    public void Every_denial_prose_read_is_allow_listed()
    {
        var offenders = Reads.Value.Where(site => !Allowed.ContainsKey(site)).ToArray();

        Assert.True(
            offenders.Length == 0,
            "A writer reads PermissionDecision.Denied prose outside AuthorizationRefusalRenderer. Ledger "
            + "L656 requires refusal text to be filtered by the acting principal's read projection — render "
            + "the refusal instead, or add the row here with the reason it discloses nothing. Offending "
            + "sites:\n  " + string.Join("\n  ", offenders));
    }

    [Fact(DisplayName = "214 s1: the denial-prose scan actually finds the resolver's audit read")]
    public void The_prose_scan_finds_the_resolver_audit_read()
    {
        // Anti-vacuity: an IL walk that resolved nothing would pass the assertion above over an empty set.
        Assert.Contains(
            Reads.Value,
            site => site.Contains("DefaultPermissionResolver.EmitDenialAsync", StringComparison.Ordinal));
        Assert.Equal(Allowed.Count, Reads.Value.Length);
    }

    [Fact(DisplayName = "214 s1: a planted prose reader is rejected")]
    public void A_planted_prose_reader_is_rejected()
    {
        const string planted =
            "apps/local-node-host/Health/ContactRoutes.cs"
            + "|Harborline.Api.LocalNodeHost.Health.ContactRoutes.Map(): System.Void"
            + "|" + ReasonDisplayGetter;

        var offenders = Reads.Value.Append(planted).Where(site => !Allowed.ContainsKey(site)).ToArray();

        Assert.Equal([planted], offenders);
    }

    [Fact(DisplayName = "214 s1: every catcher of the gate's denial exception is allow-listed")]
    public void Every_denial_exception_catcher_is_allow_listed()
    {
        var offenders = Catchers.Value.Where(type => !AllowedCatchers.ContainsKey(type)).ToArray();

        Assert.True(
            offenders.Length == 0,
            "A type catches AuthorizationDeniedException, whose message and decision are the UNFILTERED "
            + "reading of a refusal. A route that catches it can serialize it; render the refusal through "
            + "AuthorizationRefusalRenderer instead, or add the row here with the reason it writes no "
            + "response text. Offending types:\n  " + string.Join("\n  ", offenders));
    }

    [Fact(DisplayName = "214 s1: the catch scan actually finds the known seeders")]
    public void The_catch_scan_finds_the_known_seeders()
    {
        Assert.Contains("Harborline.Api.LocalNodeHost.FormsDevSeeder", Catchers.Value);
        Assert.Equal(AllowedCatchers.Count, Catchers.Value.Length);
    }

    [Fact(DisplayName = "214 s1: a planted denial-exception catcher is rejected")]
    public void A_planted_catcher_is_rejected()
    {
        const string planted = "Harborline.Api.LocalNodeHost.Health.InvoiceRoutes";

        var offenders = Catchers.Value.Append(planted).Where(type => !AllowedCatchers.ContainsKey(type)).ToArray();

        Assert.Equal([planted], offenders);
    }

    /// <summary>
    /// Every shipped type with a <c>catch (AuthorizationDeniedException)</c> clause, read from the IL
    /// exception-handling table (a compiler-generated state machine reports its outer type).
    /// </summary>
    private static IEnumerable<string> DiscoverCatchers()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .GroupBy(assembly => assembly.GetName().Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "Harborline*.dll")
                     .Where(file => !Path.GetFileName(file).Contains("Tests", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(file).Contains("Analyzers", StringComparison.OrdinalIgnoreCase)
                         && !Path.GetFileName(file).Contains("Tooling", StringComparison.OrdinalIgnoreCase)))
        {
            Assembly assembly;
            try
            {
                var simpleName = Path.GetFileNameWithoutExtension(path);
                assembly = loaded.TryGetValue(simpleName, out var existing)
                    ? existing
                    : AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            catch (Exception) { continue; }

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = [.. ex.Types.OfType<Type>()]; }
            catch (Exception) { continue; }

            foreach (var type in types)
            {
                const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                var methods = type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All));
                foreach (var method in methods)
                {
                    MethodBody? body;
                    try { body = method.GetMethodBody(); }
                    catch (Exception) { continue; }
                    if (body is null) continue;
                    if (!body.ExceptionHandlingClauses.Any(clause =>
                            clause.Flags == ExceptionHandlingClauseOptions.Clause
                            && SafeCatchType(clause) == typeof(AuthorizationDeniedException)))
                        continue;
                    var owner = Owner(type);
                    found.Add(owner.FullName ?? owner.Name);
                }
            }
        }

        return found;
    }

    private static Type? SafeCatchType(ExceptionHandlingClause clause)
    {
        try { return clause.CatchType; }
        catch (Exception) { return null; }
    }

    /// <summary>A compiler-generated async state machine is nested in the type that wrote the catch.</summary>
    private static Type Owner(Type type)
    {
        var owner = type;
        while (owner.IsNested && owner.DeclaringType is { } declaring && owner.Name.Contains('<'))
            owner = declaring;
        return owner;
    }
}
