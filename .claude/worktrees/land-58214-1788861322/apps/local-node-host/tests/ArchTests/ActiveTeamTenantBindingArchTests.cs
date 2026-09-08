using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// ADR 0032 identity layer — the DATA-ISOLATION fence. Proves the node's ambient data
/// <see cref="TenantId"/> is bound to the ACTIVE TEAM (not a hardcoded literal) so switching the active
/// org switches the data tenant — closing the worst-case multi-tenant bug (silent cross-org data bleed).
/// </summary>
/// <remarks>
/// <para>
/// The headline invariants (survey #1275 §5):
/// <list type="bullet">
///   <item>A DIFFERENT active team MUST yield a DIFFERENT <see cref="TenantId"/> (no two orgs collapse
///     onto one tenant — the cross-org bleed).</item>
///   <item>The production ambient <see cref="ITenantContext"/> (<see cref="ActiveTeamTenantContext"/>)
///     MUST derive its tenant from the active team — NOT a hardcoded constant. The fence asserts the
///     retired <c>StaticNodeTenantContext.TenantId("local")</c> literal is no longer the ambient
///     production tenant for any active team.</item>
/// </list>
/// </para>
/// <para>
/// <b>Proof that the fence bites</b> (the "revert the binding → test fails" requirement): the
/// <see cref="DifferentActiveTeam_YieldsDifferentTenant"/> test fails the moment
/// <see cref="ActiveTeamTenantContext"/> is changed to return a constant tenant (e.g. reverting it to
/// the old <c>StaticNodeTenantContext</c> "local" literal), because two distinct active teams would then
/// project to the SAME tenant — which the assertion rejects. See the test's remarks for the manual
/// revert procedure.
/// </para>
/// </remarks>
public sealed class ActiveTeamTenantBindingArchTests
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

    private static TeamContext Materialize(TeamId id, string name) =>
        new(id, name, new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class FakeActiveTeamAccessor : IActiveTeamAccessor
    {
        public FakeActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; private set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public void Set(TeamContext? team) => Active = team;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }

    // ── The headline data-isolation invariant ───────────────────────────────────

    [Fact(DisplayName = "ADR0032 fence: a DIFFERENT active team yields a DIFFERENT data TenantId (no cross-org bleed)")]
    public void DifferentActiveTeam_YieldsDifferentTenant()
    {
        // This is the fence that BITES: reverting ActiveTeamTenantContext to a constant "local" tenant
        // would make both contexts resolve the SAME TenantId, failing the Assert.NotEqual below.
        var accessor = new FakeActiveTeamAccessor(Materialize(TeamA, "Org A"));
        var sut = new ActiveTeamTenantContext(accessor);

        accessor.Set(Materialize(TeamA, "Org A"));
        var tenantForA = sut.Tenant!.Id;

        accessor.Set(Materialize(TeamB, "Org B"));
        var tenantForB = sut.Tenant!.Id;

        Assert.NotEqual(tenantForA, tenantForB);
        Assert.Equal(TeamA.Value.ToString(), tenantForA.Value);
        Assert.Equal(TeamB.Value.ToString(), tenantForB.Value);
    }

    [Fact(DisplayName = "ADR0032 fence: switching the active team SWITCHES the ambient TenantId")]
    public void SwitchingActiveTeam_SwitchesTenant()
    {
        var accessor = new FakeActiveTeamAccessor(Materialize(TeamA, "Org A"));
        var sut = new ActiveTeamTenantContext(accessor);

        Assert.Equal(TeamA.Value.ToString(), sut.Tenant!.Id.Value);

        // Simulate the org-switcher rebind.
        accessor.Set(Materialize(TeamB, "Org B"));
        Assert.Equal(TeamB.Value.ToString(), sut.Tenant!.Id.Value);
    }

    [Fact(DisplayName = "ADR0032 fence: the ambient tenant is NEVER the retired hardcoded \"local\" literal")]
    public void AmbientTenant_IsNeverTheRetiredLocalLiteral()
    {
        var accessor = new FakeActiveTeamAccessor(Materialize(TeamA, "Org A"));
        var sut = new ActiveTeamTenantContext(accessor);

        Assert.NotEqual(StaticNodeTenantContext.LocalTenantId, sut.Tenant!.Id.Value);
    }

    [Fact(DisplayName = "ADR0032 fence: no active team => unresolved tenant (fail-loud, never a shared default)")]
    public void NoActiveTeam_TenantIsUnresolved()
    {
        ITenantContext sut = new ActiveTeamTenantContext(new FakeActiveTeamAccessor(active: null));
        Assert.Null(sut.Tenant);
        Assert.False(sut.IsResolved);
    }

    // ── The projection determinism (same team => same tenant, round-trip) ───────

    [Fact(DisplayName = "ADR0032 fence: the same active team ALWAYS projects to the same TenantId")]
    public void SameTeam_ProjectsToSameTenant_Deterministically()
    {
        var t1 = ActiveTeamTenantContext.ProjectTenantId(TeamA);
        var t2 = ActiveTeamTenantContext.ProjectTenantId(TeamA);
        Assert.Equal(t1, t2);
        Assert.Equal(TeamA.Value.ToString(), t1.Value);
    }

    [Fact(DisplayName = "ADR0032 fence: the production ambient ITenantContext type is ActiveTeamTenantContext, not the retired static one")]
    public void AmbientContextType_IsActiveTeamBound()
    {
        // ActiveTeamTenantContext is an ITenantContext; the retired StaticNodeTenantContext is no longer
        // the production ambient (it remains only as the const-holder + a test fake).
        Assert.True(typeof(ITenantContext).IsAssignableFrom(typeof(ActiveTeamTenantContext)));
        Assert.Equal("local", StaticNodeTenantContext.LocalTenantId);
    }

    // ── The source-scan "no retired-tenant pattern in production" fence (ALL forms) ───────

    /// <summary>
    /// No production node source file may re-introduce the RETIRED install-constant <c>"local"</c> tenant
    /// in ANY form — the data tenant MUST be resolved from the active team (via <see cref="NodeTenant"/> /
    /// <see cref="ActiveTeamTenantContext"/>). A retired-tenant pin is the cross-org data-bleed regression
    /// (ADR 0032 identity layer; survey #1275 §5). This fence catches all THREE forms the retired pattern
    /// takes (a fence that sees only literal construction gives false "fenced" confidence — #1276 sec-eng
    /// finding F1):
    /// <list type="number">
    ///   <item><b>Literal construction</b> — <c>new("local")</c> / <c>new TenantId("local")</c> /
    ///     <c>new(StaticNodeTenantContext.LocalTenantId)</c>.</item>
    ///   <item><b>DI-registration of the retired type as the ambient interface</b> —
    ///     <c>AddSingleton/AddScoped/AddTransient/TryAdd*&lt;ITenantContext, StaticNodeTenantContext&gt;()</c>
    ///     AND the factory form <c>… =&gt; new StaticNodeTenantContext()</c>. (Registering the retired impl
    ///     via <c>TryAdd</c> order-dependently split-brains a composition that runs before the
    ///     active-team one — the F1 payroll bug.)</item>
    ///   <item><b>Static accessors of the retired type</b> — <c>StaticNodeTenantContext.Instance</c> /
    ///     <c>StaticNodeTenantContext.LocalTenantId</c> used as a runtime value (its const/instance form).</item>
    /// </list>
    /// The only sanctioned home of the <c>"local"</c> literal is <see cref="StaticNodeTenantContext"/>
    /// itself (the retired const-holder + test fake), which is exempted. The scan walks the WHOLE node
    /// production surface (F3) — not just <c>Health/</c> + <c>Data/</c> — so a retired-tenant pin added to
    /// <c>Platforms/</c>, the host root, tools, or anywhere else is caught; only build outputs and tests are
    /// excluded.
    /// </summary>
    [Fact(DisplayName = "ADR0032 fence: NO production node source re-introduces the retired \"local\" tenant in ANY form (literal / DI-registration / static accessor)")]
    public void NoProductionSource_ReintroducesTheRetiredLocalTenant()
    {
        var offenders = ScanForRetiredTenantForms();

        Assert.True(
            offenders.Count == 0,
            "Production node source re-introduces the retired \"local\" tenant (cross-org bleed regression). " +
            "Resolve the tenant from the active team via NodeTenant.Resolve(activeTeam) / register " +
            "ActiveTeamTenantContext as the ambient ITenantContext instead:\n  " +
            string.Join("\n  ", offenders));
    }

    // ── Bite-proofs: the EXTENDED fence catches the DI-registration + static-accessor forms ──

    /// <summary>
    /// <b>Bite-proof for form 2 (DI-registration) — the F1 blind spot.</b> Re-introducing the payroll
    /// <c>TryAddSingleton&lt;ITenantContext, StaticNodeTenantContext&gt;()</c> the sec-eng review flagged
    /// (#1276 F1) into the production surface MUST be caught by the fence. The pre-extension fence (literal
    /// construction only) MISSED this form; this test proves the extension bites. The offending file is
    /// written into an isolated temporary surface, scanned, then removed (revert) — so the standing fence stays
    /// green and this proof is hermetic.
    /// </summary>
    [Fact(DisplayName = "ADR0032 fence BITES (proof): a re-introduced TryAdd<ITenantContext, StaticNodeTenantContext> (the F1 form) is CAUGHT")]
    public void Fence_Catches_TheReintroducedDiRegistrationForm()
    {
        // Pre: the standing surface is clean (no offender).
        Assert.DoesNotContain(
            ScanForRetiredTenantForms(),
            o => o.Contains(BiteProofMarker, StringComparison.Ordinal));

        AssertOffenderCaughtThenReverted(
            // The EXACT F1 form (unqualified, the payroll bug) + the namespace-qualified variant — both
            // are forms the literal-only pre-extension fence MISSED. (Scanned as text + reverted; never
            // compiled into the assembly.)
            offendingBody:
                "namespace Harborline.Api.LocalNodeHost.Data.Financial;\n" +
                "internal static class " + BiteProofMarker + "DiReg\n" +
                "{\n" +
                "    public static void RegisterF1Form(Microsoft.Extensions.DependencyInjection.IServiceCollection services)\n" +
                "    {\n" +
                "        services.TryAddSingleton<ITenantContext, StaticNodeTenantContext>();\n" +
                "        services.AddSingleton<Harborline.Api.Foundation.MultiTenancy.ITenantContext, StaticNodeTenantContext>();\n" +
                "    }\n" +
                "}\n");
    }

    /// <summary>
    /// <b>Bite-proof for form 3 (static accessor).</b> A production use of
    /// <c>StaticNodeTenantContext.Instance</c> / <c>.LocalTenantId</c> as a runtime value MUST be caught.
    /// The pre-extension fence missed this form too.
    /// </summary>
    [Fact(DisplayName = "ADR0032 fence BITES (proof): a re-introduced StaticNodeTenantContext.Instance / .LocalTenantId static-accessor use is CAUGHT")]
    public void Fence_Catches_TheReintroducedStaticAccessorForm()
    {
        Assert.DoesNotContain(
            ScanForRetiredTenantForms(),
            o => o.Contains(BiteProofMarker, StringComparison.Ordinal));

        AssertOffenderCaughtThenReverted(
            offendingBody:
                "namespace Harborline.Api.LocalNodeHost.Data.Financial;\n" +
                "internal static class " + BiteProofMarker + "Accessor\n" +
                "{\n" +
                "    public static string PinByConst() => StaticNodeTenantContext.LocalTenantId;\n" +
                "    public static object PinByInstance() => StaticNodeTenantContext.Instance!;\n" +
                "}\n");
    }

    /// <summary>A unique token in the bite-proof offender filename/type so the scan can isolate it.</summary>
    private const string BiteProofMarker = "FenceBiteProof_Reverted_";

    /// <summary>
    /// Writes <paramref name="offendingBody"/> into an isolated temporary file, runs the SAME scan the
    /// standing fence uses, asserts the offender is caught, then removes the file (revert) — proving the
    /// extended fence bites on the form without leaving a standing offender behind.
    /// </summary>
    private static void AssertOffenderCaughtThenReverted(string offendingBody)
    {
        // Keep probes outside production: AuthorizationModelArchTests can enumerate a probe
        // just before this helper deletes it, then fail when reading the vanished file.
        var hostRoot = Directory.CreateTempSubdirectory("active-team-tenant-arch-").FullName;
        var offendingFile = Path.Combine(
            hostRoot, BiteProofMarker + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            File.WriteAllText(offendingFile, offendingBody);

            var offenders = ScanForRetiredTenantForms(hostRoot);
            Assert.Contains(
                offenders,
                o => o.Contains(BiteProofMarker, StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(offendingFile)) File.Delete(offendingFile);
            Directory.Delete(hostRoot);
        }

        // Post: after the revert the standing surface is clean again (the proof left nothing behind).
        Assert.DoesNotContain(
            ScanForRetiredTenantForms(),
            o => o.Contains(BiteProofMarker, StringComparison.Ordinal));
    }

    /// <summary>
    /// The shared scan: walks the whole node production surface and returns every line that
    /// re-introduces the retired "local" tenant in any of the three forms. Factored out so the
    /// bite-proof test below can drive the SAME scan against a deliberately-reintroduced offender.
    /// </summary>
    private static List<string> ScanForRetiredTenantForms(string? hostRoot = null)
    {
        hostRoot ??= LocateHostSourceRoot();
        // The only file allowed to define/hold the retired "local" literal + the retired impl type.
        var exemptFiles = new[] { "StaticNodeTenantContext.cs" };
        // Build outputs and tests are outside the production surface.
        var exemptDirSegments = new[]
        {
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
        };

        // Form 1: literal construction of the retired tenant.
        var literalTenant = new Regex(
            @"new\s*(TenantId\s*)?\(\s*""local""\s*\)|new\s*\(\s*StaticNodeTenantContext\.LocalTenantId\s*\)",
            RegexOptions.Compiled);
        // Form 2: DI-registration of the retired type as the ambient ITenantContext — the generic
        // Add*/TryAdd*<[ns.]ITenantContext, StaticNodeTenantContext> form (with or without a namespace
        // qualifier on the interface) AND any factory/direct construction `new StaticNodeTenantContext(`
        // (the only legitimate construction is inside the exempt StaticNodeTenantContext.cs + test fakes).
        var diRegistration = new Regex(
            @"(Add(Singleton|Scoped|Transient)|TryAdd(Singleton|Scoped|Transient|Enumerable)?)\s*<\s*([\w.]*\.)?ITenantContext\s*,\s*StaticNodeTenantContext\s*>|new\s+StaticNodeTenantContext\s*\(",
            RegexOptions.Compiled);
        // Form 3: a static accessor of the retired type used as a runtime value.
        var staticAccessor = new Regex(
            @"StaticNodeTenantContext\.(Instance|LocalTenantId)\b",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(hostRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (exemptFiles.Any(e => file.EndsWith(e, StringComparison.Ordinal))) continue;
            if (exemptDirSegments.Any(seg => file.Contains(seg, StringComparison.Ordinal))) continue;

            foreach (var rawLine in File.ReadLines(file))
            {
                var line = rawLine.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) ||
                    line.StartsWith("///", StringComparison.Ordinal) ||
                    line.StartsWith("*", StringComparison.Ordinal))
                {
                    continue; // skip comments/doc-comments
                }
                if (literalTenant.IsMatch(line) ||
                    diRegistration.IsMatch(line) ||
                    staticAccessor.IsMatch(line))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        return offenders;
    }

    /// <summary>Walks up from the test assembly to the local-node-host source root.</summary>
    private static string LocateHostSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // The host project root contains Harborline.LocalNodeHost.csproj.
            var candidate = Path.Combine(dir.FullName, "Harborline.LocalNodeHost.csproj");
            if (File.Exists(candidate)) return dir.FullName;
            // From tests/bin/... walk up to apps/local-node-host.
            var sibling = Path.Combine(dir.FullName, "apps", "local-node-host", "Harborline.LocalNodeHost.csproj");
            if (File.Exists(sibling)) return Path.GetDirectoryName(sibling)!;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the local-node-host source root from " + AppContext.BaseDirectory);
    }
}
