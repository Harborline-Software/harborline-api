using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// ADR-0163 AM-16 G1 and AM-17 residual fences. The admission-less PEP state is safe only while
/// stale/own roster rows are the sole rows that can disappear, and while subject erasure remains
/// outside the roster store.
/// </summary>
public sealed class RosterAuthorizationBoundaryArchTests
{
    private static readonly Regex RosterRecordMutation = new(
        @"\.\s*(?<method>RemoveRange|Remove|ExecuteDelete(?:Async)?|ExecuteSql(?:Raw|Interpolated)?(?:Async)?|FromSql(?:Raw|Interpolated)?(?:Async)?)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex RawSqlCall = new(
        @"\.\s*(?:ExecuteSql(?:Raw|Interpolated)?|FromSql(?:Raw|Interpolated)?)(?:Async)?\s*\(",
        RegexOptions.Compiled);

    [Fact(DisplayName = "AM-16/G1: NodeRosterRecord deletion has one owner and two scoped predicates")]
    public void NodeRosterRecord_Deletion_Is_Only_Scoped_In_RosterProjection()
    {
        var root = NodeHostProjectRoot();
        var projectionPath = Path.Combine(root, "Data", "Roster", "RosterCrdtProjection.cs");
        var mutations = EnumerateProductionSource(root)
            .SelectMany(file => RosterRecordMutations(file, root))
            .ToArray();
        var rawSql = EnumerateProductionSource(root)
            .Where(file => HasRosterRawSql(CodeOnly(File.ReadAllText(file))))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.Empty(rawSql);
        Assert.Equal(2, mutations.Length);
        Assert.All(mutations, mutation => Assert.Equal(
            Path.GetRelativePath(root, projectionPath), mutation.RelativePath));
        Assert.All(mutations, mutation => Assert.True(
            HasTeamScope(mutation.Source, mutation.Index),
            $"Roster mutation '{mutation.Method}' at index {mutation.Index} is not scoped to a non-active team."));

        var projection = File.ReadAllText(projectionPath);
        var projectionCode = CodeOnly(projection);
        Assert.Contains(".Where(r => r.TeamId == ownTeamIdString)", projectionCode);
        Assert.Contains(".Where(row => staleRecordIds.Contains(row.Id))", projectionCode);

        // The stale-genesis predicate is record-id based, so its source set must prove the row's
        // team differs from the current/active team before it can reach RemoveRange.
        Assert.Contains("recordTeamId == currentTeamId", projectionCode);
    }

    [Fact(DisplayName = "AM-16/G1: an unscoped NodeRosterRecord delete is rejected by the fence")]
    public void Unscoped_Delete_Is_Not_Accepted_By_The_Fence()
    {
        const string plantedUnscopedDelete =
            "var rows = await ctx.Set<NodeRosterRecord>().ToListAsync(); ctx.Set<NodeRosterRecord>().RemoveRange(rows);";
        const string plantedScopedDelete =
            "var rows = await ctx.Set<NodeRosterRecord>().Where(r => r.TeamId == ownTeamId).ToListAsync(); " +
            "ctx.Set<NodeRosterRecord>().RemoveRange(rows);";

        const string plantedExecuteDelete =
            "var rows = ctx.Set<NodeRosterRecord>(); rows.ExecuteDeleteAsync();";
        const string plantedExecuteDeleteSync =
            "var rows = ctx.Set<NodeRosterRecord>(); rows.ExecuteDelete();";
        const string plantedBareRemove =
            "NodeRosterRecord row = GetRow(); ctx.Remove(row);";
        const string plantedRosterSql =
            "ctx.Database.ExecuteSqlRaw(\"DELETE FROM roster_records WHERE team_id = 'x'\");";

        Assert.True(RosterRecordMutationsIn(plantedUnscopedDelete).Count == 1);
        Assert.False(HasTeamScope(plantedUnscopedDelete, RosterRecordMutationsIn(plantedUnscopedDelete)[0].Index));
        Assert.True(RosterRecordMutationsIn(plantedScopedDelete).Count == 1);
        Assert.True(HasTeamScope(plantedScopedDelete, RosterRecordMutationsIn(plantedScopedDelete)[0].Index));
        Assert.True(RosterRecordMutationsIn(plantedExecuteDelete).Count == 1);
        Assert.False(HasTeamScope(plantedExecuteDelete, RosterRecordMutationsIn(plantedExecuteDelete)[0].Index));
        Assert.True(RosterRecordMutationsIn(plantedExecuteDeleteSync).Count == 1);
        Assert.False(HasTeamScope(plantedExecuteDeleteSync, RosterRecordMutationsIn(plantedExecuteDeleteSync)[0].Index));
        Assert.True(RosterRecordMutationsIn(plantedBareRemove).Count == 1);
        Assert.False(HasTeamScope(plantedBareRemove, RosterRecordMutationsIn(plantedBareRemove)[0].Index));
        Assert.True(HasRosterRawSql(plantedRosterSql));
    }

    [Fact(DisplayName = "AM-17/WW-1: subject erasure never reaches the roster context")]
    public void SubjectErasure_Flow_Never_Touches_Roster_Context()
    {
        var root = NodeHostProjectRoot();
        // Composition roots are intentionally excluded: their DI registrations name the interface but do
        // not implement erasure. The implementation classes below are the surfaces that can reach a store.
        var erasureFlowFiles = EnumerateProductionSource(root)
            .Where(file =>
            {
                var code = CodeOnly(File.ReadAllText(file));
                return Regex.IsMatch(
                    code,
                    @"\bclass\s+\w+(?:\s*<[^>]+>)?\s*:\s*[^\r\n{]*(?:ISubjectErasurePropagator|ISubjectErasureService)\b");
            })
            .ToArray();

        Assert.NotEmpty(erasureFlowFiles);
        Assert.All(erasureFlowFiles, file =>
            Assert.DoesNotContain(
                "NodeLocalRosterDbContext",
                CodeOnly(File.ReadAllText(file)),
                StringComparison.Ordinal));
    }

    [Fact(DisplayName = "AM-17/WW-1: roster columns carry no subject-key encryption")]
    public void Roster_Columns_Have_No_SubjectKey_Encryption()
    {
        var root = NodeHostProjectRoot();
        var rosterRoot = Path.Combine(root, "Data", "Roster");
        var forbiddenMarkers = new[]
        {
            "SubjectKey",
            "SubjectScoped",
            "ISubjectFieldEncryptor",
            "IFieldEncryptor",
            "EncryptedField",
        };

        var rosterSources = Directory.EnumerateFiles(rosterRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(rosterSources);
        foreach (var file in rosterSources)
        {
            var code = CodeOnly(File.ReadAllText(file));
            foreach (var marker in forbiddenMarkers)
            {
                Assert.DoesNotContain(marker, code, StringComparison.Ordinal);
            }
        }
    }

    [Fact(DisplayName = "AM-17 residual: supersession receives only a non-active own team id")]
    public void Supersession_Requires_OwnTeam_Different_From_JoinedTeam()
    {
        var root = NodeHostProjectRoot();
        var enrollment = CodeOnly(File.ReadAllText(
            Path.Combine(root, "Enrollment", "NodeWireEnrollmentClient.cs")));
        var guard = enrollment.IndexOf("ownTeamIdBeforeAdopt != plan.TeamId", StringComparison.Ordinal);
        var call = enrollment.IndexOf("SupersedeOwnTeamRecordsAsync(ownTeamIdBeforeAdopt", StringComparison.Ordinal);
        var adopt = enrollment.IndexOf("_roster.AdoptEnrollment(", StringComparison.Ordinal);

        Assert.True(guard >= 0, "The join path must reject an own-team id equal to the joined/active team.");
        Assert.True(call > guard, "Supersession must be dominated by the own-team != joined-team guard.");
        Assert.True(adopt > call, "The in-memory active-team switch must happen after supersession.");
    }

    private sealed record RosterMutation(string Source, int Index, string Method, string RelativePath);

    private static IReadOnlyList<RosterMutation> RosterRecordMutations(string file, string root)
    {
        var source = CodeOnly(File.ReadAllText(file));
        if (!source.Contains("NodeRosterRecord", StringComparison.Ordinal)
            && !source.Contains("RosterRecords", StringComparison.Ordinal))
        {
            return [];
        }

        return RosterRecordMutationsIn(source)
            .Select(mutation => mutation with { Source = source, RelativePath = Path.GetRelativePath(root, file) })
            .ToArray();
    }

    private static IReadOnlyList<RosterMutation> RosterRecordMutationsIn(string source) =>
        RosterRecordMutation.Matches(source)
            .Select(match => new RosterMutation(source, match.Index, match.Groups["method"].Value, string.Empty))
            .ToArray();

    private static bool HasRosterRawSql(string source) =>
        RawSqlCall.IsMatch(source)
            && source.Contains("roster_records", StringComparison.OrdinalIgnoreCase);

    private static bool HasTeamScope(string source, int mutationIndex)
    {
        var statementStart = Math.Max(
            Math.Max(source.LastIndexOf(';', mutationIndex), source.LastIndexOf('{', mutationIndex)),
            source.LastIndexOf('}', mutationIndex)) + 1;
        var statement = source[statementStart..mutationIndex];

        // A direct Remove(entity) has no predicate and is never accepted. Query-terminal deletes must
        // carry their team restriction in the same statement as the typed NodeRosterRecord expression.
        if (!statement.Contains("Set<NodeRosterRecord>()", StringComparison.Ordinal)
            && !statement.Contains("RosterRecords", StringComparison.Ordinal))
        {
            return false;
        }

        return IsRosterQuery(statement)
            && (HasTeamPredicate(statement) || HasTeamPredicate(PreviousStatement(source, statementStart)));
    }

    private static bool IsRosterQuery(string statement) =>
        statement.Contains("Set<NodeRosterRecord>()", StringComparison.Ordinal)
            || statement.Contains("RosterRecords", StringComparison.Ordinal);

    private static bool HasTeamPredicate(string statement) =>
        statement.Contains(".Where(", StringComparison.Ordinal)
            && (statement.Contains("TeamId ==", StringComparison.Ordinal)
                || statement.Contains("staleRecordIds.Contains", StringComparison.Ordinal));

    private static string PreviousStatement(string source, int beforeIndex)
    {
        var previousEnd = source.LastIndexOf(';', Math.Max(0, beforeIndex - 1));
        var previousStart = Math.Max(
            Math.Max(source.LastIndexOf(';', Math.Max(0, previousEnd - 1)),
                source.LastIndexOf('{', Math.Max(0, previousEnd - 1))),
            source.LastIndexOf('}', Math.Max(0, previousEnd - 1))) + 1;
        return previousEnd < 0 ? string.Empty : source[previousStart..previousEnd];
    }

    private static string CodeOnly(string source) => string.Join(
        Environment.NewLine,
        Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline)
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment < 0 ? line : line[..comment];
            }));

    private static string NodeHostProjectRoot([CallerFilePath] string thisFile = "")
    {
        var archTestsDir = Path.GetDirectoryName(thisFile)!;
        var testsDir = Path.GetDirectoryName(archTestsDir)!;
        var hostRoot = Path.GetDirectoryName(testsDir)!;
        Assert.True(
            File.Exists(Path.Combine(hostRoot, "Program.cs"))
                && File.Exists(Path.Combine(hostRoot, "LocalNodeOptions.cs")),
            $"Could not locate the node-host project root from '{thisFile}'.");
        return hostRoot;
    }

    private static IEnumerable<string> EnumerateProductionSource(string hostRoot) =>
        Directory.EnumerateFiles(hostRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var relative = Path.GetRelativePath(hostRoot, file);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !segments.Any(segment =>
                    segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals(".worktrees", StringComparison.OrdinalIgnoreCase));
            });
}
