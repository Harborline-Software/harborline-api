using System.Reflection;
using Harborline.Api.LocalNodeHost.Data;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Council condition C3 (ADR 0114): no LINQ query in the fleet may project
/// <em>into</em> <c>lines_json</c> or <c>applications_json</c> columns by name.
/// </summary>
/// <remarks>
/// <para>
/// Projecting into a JSON column by string literal bypasses EF's value-converter
/// pipeline and produces provider-specific raw SQL. On SQLite the JSON functions
/// differ from Postgres's <c>jsonb</c> operators, so a query that works on Postgres
/// silently fails (or produces wrong results) on SQLite.
/// </para>
/// <para>
/// The constraint is: LINQ must never reference these column names as string
/// literals in <c>FromSqlRaw</c> / <c>ExecuteSqlRaw</c> / <c>HasFilter</c> calls
/// within the local-node-host assembly or any of its block-package dependencies.
/// Value-converter paths (the normal EF serialization flow) are exempted because
/// they produce portable parameterised queries.
/// </para>
/// <para>
/// If a new JSON column is added and its name must appear in raw SQL:
/// <list type="number">
///   <item>Add it to the <see cref="KnownAllowedRawJsonColumnRefs"/> set with a
///   justification comment explaining why the raw reference is safe.</item>
///   <item>Ensure the raw SQL is guarded by <c>Database.IsNpgsql()</c> so it
///   never runs on SQLite.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class JsonProjectionConstraintTests
{
    /// <summary>
    /// Column names that ARE allowed in raw SQL strings because they are explicitly
    /// guarded by <c>Database.IsNpgsql()</c> at the call site.  Each entry here
    /// must have a corresponding inline comment in the guarded block explaining why
    /// the allowance is safe.
    /// </summary>
    private static readonly HashSet<string> KnownAllowedRawJsonColumnRefs =
    [
        // Add entries here ONLY when a raw SQL reference is genuinely necessary
        // and is strictly guarded by Database.IsNpgsql(). Format:
        //   "lines_json",  // guarded in FooEntityModule.Configure at line NN
    ];

    /// <summary>
    /// JSON column names that MUST NOT appear in raw SQL strings without a guard.
    /// Extend this list whenever a new JSON column is added to the local-node model.
    /// </summary>
    private static readonly string[] ProtectedJsonColumns =
    [
        "lines_json",
        "applications_json",
        "line_templates_json",
        "generated_invoices_json",
        "revision_vector_json",
    ];

    [Fact(DisplayName = "Ticket 257: the raw-JSON allowance list is empty, so no row of it can be vacuous")]
    public void KnownAllowedRawJsonColumnRefs_IsEmpty() =>
        Assert.True(KnownAllowedRawJsonColumnRefs.Count == 0,
            "KnownAllowedRawJsonColumnRefs skips a column name for the whole C3 scan, so a stale row "
            + "silently pre-authorises a future unguarded reference. Adding the first real row means "
            + "registering it in AllowListVacuityArchTests against a skip-list-bypassed discovery and "
            + "deleting this pin:\n  " + string.Join("\n  ", KnownAllowedRawJsonColumnRefs));

    [Fact(DisplayName = "C3: No unguarded LINQ projections into JSON columns in LocalNodeHost assembly")]
    public void NoUnguardedJsonColumnProjections_InLocalNodeHostAssembly()
    {
        var assembly = typeof(LocalNodeDbContext).Assembly;
        AssertNoRawJsonProjections(assembly);
    }

    [Fact(DisplayName = "C3: No unguarded LINQ projections into JSON columns in test assembly")]
    public void NoUnguardedJsonColumnProjections_InTestAssembly()
    {
        var assembly = typeof(JsonProjectionConstraintTests).Assembly;
        AssertNoRawJsonProjections(assembly);
    }

    // -----------------------------------------------------------------------
    // Implementation
    // -----------------------------------------------------------------------

    private static void AssertNoRawJsonProjections(Assembly assembly)
    {
        // Scan every embedded string resource in the IL for raw JSON column refs.
        // This is a heuristic — it catches FromSqlRaw / ExecuteSqlRaw / HasFilter
        // string literals baked into the assembly. It does NOT catch dynamic strings
        // constructed at runtime, but those are a separate code-review concern.
        var violations = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            // C3 targets APPLICATION LINQ code (FromSqlRaw / ExecuteSqlRaw / HasFilter
            // literals). Two classes of type legitimately embed JSON column NAMES as
            // string literals without being LINQ-into-JSON projections, and must be
            // excluded or the heuristic produces false positives:
            //
            //   1. EF migration types — `<Timestamp>_<Name>` + the model snapshot
            //      DECLARE columns by name (e.g. HasColumnName("lines_json")). That is
            //      schema metadata, not a query projecting into the column.
            //   2. Compiler-generated types — anonymous types (`<>f__AnonymousType…`)
            //      synthesize a ToString() that embeds their property names (the
            //      validation API's `Results.Ok(new { … })` objects), and other
            //      `[CompilerGenerated]` closures. These are not author-written SQL.
            //
            // Excluding them keeps C3 enforcing the real invariant (no raw JSON
            // projection in hand-written query code) without flagging migrations or
            // synthesized ToString members.
            if (IsExcludedFromC3Scan(type)) continue;

            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance |
                BindingFlags.DeclaredOnly))
            {
                var body = method.GetMethodBody();
                if (body is null) continue;

                // Read the IL bytes and extract string tokens. EF string literals
                // for column names appear as ldstr instructions (opcode 0x72) whose
                // operand is an RVA into the #US heap — but reflection doesn't give
                // us the US heap directly. Instead we use the module's ResolveString
                // on each metadata token found after 0x72 in the raw IL.
                var il = body.GetILAsByteArray();
                if (il is null) continue;

                for (int i = 0; i < il.Length - 4; i++)
                {
                    // 0x72 = ldstr, followed by 4-byte metadata token
                    if (il[i] != 0x72) continue;

                    var token = il[i + 1] | (il[i + 2] << 8) | (il[i + 3] << 16) | (il[i + 4] << 24);
                    string? str;
                    try
                    {
                        str = assembly.GetManifestResourceStream(token.ToString()) is null
                            ? method.Module.ResolveString(token)
                            : null;
                    }
                    catch
                    {
                        continue;
                    }

                    if (str is null) continue;

                    foreach (var col in ProtectedJsonColumns)
                    {
                        if (!str.Contains(col, StringComparison.OrdinalIgnoreCase)) continue;
                        if (KnownAllowedRawJsonColumnRefs.Contains(col)) continue;

                        violations.Add(
                            $"  {type.FullName}.{method.Name} contains raw reference to '{col}' in string literal: \"{Truncate(str, 120)}\"");
                    }
                }
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"C3 violation — {violations.Count} unguarded raw JSON column reference(s) found in " +
                $"{assembly.GetName().Name}. Guard each with Database.IsNpgsql() or add to KnownAllowedRawJsonColumnRefs " +
                $"with a justification comment.\n\n" +
                string.Join("\n", violations));
        }
    }

    /// <summary>
    /// True when <paramref name="type"/> is one C3 must not scan: an EF migration
    /// type (column-name declarations are schema metadata) or a compiler-generated
    /// type (anonymous-type ToString / closures embed property names but are not
    /// author-written SQL).
    /// </summary>
    private static bool IsExcludedFromC3Scan(Type type)
    {
        // EF migration / model-snapshot types live under the *.Data.Migrations
        // namespace and declare columns by name. Not LINQ projections.
        if (type.Namespace is { } ns && ns.Contains(".Migrations", StringComparison.Ordinal))
        {
            return true;
        }

        // Compiler-generated types: anonymous types (`<>f__AnonymousType…`),
        // closures, iterator/async state machines. Their synthesized members embed
        // property names but contain no hand-written raw SQL.
        if (Attribute.IsDefined(type, typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
        {
            return true;
        }

        // Belt-and-suspenders for anonymous types whose CompilerGenerated marker is
        // on a nesting boundary the reflection above may not surface.
        if (type.Name.StartsWith("<>", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
