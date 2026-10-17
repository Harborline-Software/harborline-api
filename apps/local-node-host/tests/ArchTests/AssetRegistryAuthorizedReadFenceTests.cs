using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 358 slice 1 — the gated asset-registry entity READS, discovered by symbol. The inventory is the
/// set of route handlers in <see cref="AssetRegistryRoutes"/> that resolve a
/// <see cref="RequestAuthorization.RefusalAsync"/> decision before reading
/// <see cref="IRegistryEntityRepository"/>, the edges store, condition store or submission store: the entity
/// list, detail, tree, condition and submission reads. Symbol discovery inventories every store call;
/// source inspection additionally requires an awaited records:read refusal and its early return before
/// each read. An unrelated capability never satisfies that requirement.
/// </summary>
public sealed class AssetRegistryAuthorizedReadFenceTests
{
    [Fact]
    public void Entity_read_route_inventory_is_exact_and_every_read_reaches_the_gate_first()
    {
        // Class: authorized read. Reason: ADR 0060 — the typed-entity rows are projected only after the
        // records:read decision the route resolves for the record it addresses.
        var reads = RawMutationPortSymbolInventoryTests.DiscoverCalls(
            [typeof(AssetRegistryRoutes).Assembly],
            IsStoreRead,
            type => IsNestedWithin(type, typeof(AssetRegistryRoutes)));
        var guards = RawMutationPortSymbolInventoryTests.DiscoverCalls(
            [typeof(AssetRegistryRoutes).Assembly],
            target => target.DeclaringType == typeof(RequestAuthorization)
                && target.Name == nameof(RequestAuthorization.RefusalAsync),
            type => IsNestedWithin(type, typeof(AssetRegistryRoutes)));

        Assert.NotEmpty(reads);
        Assert.All(reads, read => Assert.Equal("apps/local-node-host/Health/AssetRegistryRoutes.cs", read.Path));
        Assert.All(guards, guard => Assert.Equal("apps/local-node-host/Health/AssetRegistryRoutes.cs", guard.Path));
        // Independent literal capability oracle, including the descriptor used by the four by-id routes.
        Assert.Equal("records:read", TeamRolePermissions.RecordsRead);
        Assert.Equal("records:read", AssetRegistryRoutes.ReadEntityRequest.AuthorizationCapability);
        var source = File.ReadAllText(AuditAppendSymbolInventory.ResolveSourcePath(
            "apps/local-node-host/Health/AssetRegistryRoutes.cs"));
        var sourceReads = InspectSourceReads(source);
        Assert.Equal(13, reads.Length);
        Assert.Equal(reads.Length, sourceReads.Length);
        Assert.All(sourceReads, read => Assert.True(read.HasRecordsReadDecision,
            $"{read.StoreMethod} at line {read.Line}: no awaited records:read refusal/return before the store read."));

        // Every handler that reads any of the four stores decides first, including a companion-only reader
        // that never calls the entity repository. Source inspection above verifies the capability itself.
        var ungated = reads
            .Where(read => !guards.Any(guard => guard.Symbol == read.Symbol))
            .Select(read => $"{read.Symbol} -> {TargetName(read.Target)} at line {read.Line}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(ungated.Length == 0,
            "Registry store reads with no records:read decision in the same handler:"
            + Environment.NewLine + string.Join(Environment.NewLine, ungated));
        var byIdReaders = reads
            .Where(read => StoreReadName(read.Target) == "IRegistryEntityRepository.GetByIdAsync")
            .Select(read => read.Symbol)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(4, byIdReaders.Length);
        Assert.All(byIdReaders, symbol => Assert.Contains(guards, guard => guard.Symbol == symbol));

        // Literal per-handler call inventory, including the tree's separate parent/child entity reads.
        var gated = reads
            .Where(read => guards.Any(guard => guard.Symbol == read.Symbol))
            .GroupBy(read => read.Symbol, StringComparer.Ordinal)
            .Select(handler => string.Join(";", handler.Select(read => StoreReadName(read.Target))
                .Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "IRegistryEntityRepository.GetByIdAsync;ITypedRelationshipStore.GetContainerAsAtAsync;ITypedRelationshipStore.GetContainmentPathAsAtAsync",
                "IRegistryEntityRepository.GetByIdAsync;IRegistryEntityRepository.GetByIdAsync;ITypedRelationshipStore.GetContainmentPathAsAtAsync;ITypedRelationshipStore.GetContentsAsAtAsync",
                "IConditionAssessmentStore.GetHistoryAsync;IRegistryEntityRepository.GetByIdAsync",
                "IFormSubmissionRecordStore.ListForEntityAsync;IRegistryEntityRepository.GetByIdAsync",
                "IRegistryEntityRepository.ListByTenantAsync;IRegistryEntityRepository.ListByTypeAsync",
            }.Order(StringComparer.Ordinal).ToArray(), gated);

        // In every gated handler the decision is resolved BEFORE any of the four stores is touched.
        foreach (var read in reads.Where(read => guards.Any(guard => guard.Symbol == read.Symbol)))
        {
            var first = guards.Where(guard => guard.Symbol == read.Symbol).Min(guard => guard.Line);
            Assert.True(first < read.Line, $"{read.Symbol}: read at line {read.Line} precedes its guard at {first}.");
        }
    }

    [Theory]
    [InlineData(typeof(ITypedRelationshipStore), "GetContentsAsAtAsync")]
    [InlineData(typeof(ITypedRelationshipStore), "GetContainmentPathAsAtAsync")]
    [InlineData(typeof(IConditionAssessmentStore), "GetHistoryAsync")]
    [InlineData(typeof(IFormSubmissionRecordStore), "ListForEntityAsync")]
    public void Companion_store_symbol_controls_are_included_without_an_entity_lookup(Type port, string method)
    {
        var target = port.GetMethod(method);
        Assert.NotNull(target);
        Assert.True(IsStoreRead(target));
    }

    [Theory]
    [InlineData("edges.GetContentsAsAtAsync(tenant, id, at, ct)", "edges.GetContentsAsAtAsync")]
    [InlineData("edges.GetLocationAsAtAsync(tenant, id, at, ct)", "edges.GetLocationAsAtAsync")]
    [InlineData("conditions.GetHistoryAsync(tenant, id, null, ct)", "conditions.GetHistoryAsync")]
    [InlineData("submissions.ListForEntityAsync(tenant, id, ct)", "submissions.ListForEntityAsync")]
    public void Companion_only_read_controls_require_the_specific_capability(string read, string expectedStoreMethod)
    {
        // Independent planted snippets have no entity lookup. Each companion store must be inventoried
        // on its own, and a convincing unrelated refusal must still leave that read unguarded.
        var allowed = Assert.Single(InspectSourceReads(Probe(read, "TeamRolePermissions.RecordsRead")));
        Assert.Equal(expectedStoreMethod, allowed.StoreMethod);
        Assert.True(allowed.HasRecordsReadDecision);
        var descriptor = Assert.Single(InspectSourceReads(Probe(read, "ReadEntityRequest.AuthorizationCapability")));
        Assert.True(descriptor.HasRecordsReadDecision);
        var literal = Assert.Single(InspectSourceReads(Probe(read, "\"records:read\"")));
        Assert.True(literal.HasRecordsReadDecision);

        var wrongCapability = Assert.Single(InspectSourceReads(Probe(read, "TeamRolePermissions.RecordsWrite")));
        Assert.Equal(expectedStoreMethod, wrongCapability.StoreMethod);
        Assert.False(wrongCapability.HasRecordsReadDecision);
        var noDecision = Assert.Single(InspectSourceReads(Probe(read, null)));
        Assert.Equal(expectedStoreMethod, noDecision.StoreMethod);
        Assert.False(noDecision.HasRecordsReadDecision);
    }

    [Fact]
    public void Correct_capability_elsewhere_or_after_the_read_does_not_satisfy_the_fence()
    {
        const string read = "conditions.GetHistoryAsync(tenant, id, null, ct)";
        var unrelated = Probe(read, "TeamRolePermissions.RecordsWrite")
            .Replace("var denied =", "var decoy = TeamRolePermissions.RecordsRead; var denied =", StringComparison.Ordinal);
        Assert.False(Assert.Single(InspectSourceReads(unrelated)).HasRecordsReadDecision);
        var late = Probe(read, "TeamRolePermissions.RecordsRead")
            .Replace("var denied =", "await " + read + "; var denied =", StringComparison.Ordinal);
        var lateReads = InspectSourceReads(late);
        Assert.Equal(2, lateReads.Length);
        Assert.False(lateReads[0].HasRecordsReadDecision);
        Assert.True(lateReads[1].HasRecordsReadDecision);
        var ignoredRefusal = Probe(read, "TeamRolePermissions.RecordsRead")
            .Replace("if (denied is not null) return denied;", "", StringComparison.Ordinal);
        Assert.False(Assert.Single(InspectSourceReads(ignoredRefusal)).HasRecordsReadDecision);
    }

    private static bool IsStoreRead(MethodBase target) =>
        (target.DeclaringType == typeof(IRegistryEntityRepository)
            || target.DeclaringType == typeof(ITypedRelationshipStore)
            || target.DeclaringType == typeof(IConditionAssessmentStore)
            || target.DeclaringType == typeof(IFormSubmissionRecordStore))
        && (target.Name.StartsWith("Get", StringComparison.Ordinal)
            || target.Name.StartsWith("List", StringComparison.Ordinal));

    private sealed record SourceRead(string StoreMethod, int Line, bool HasRecordsReadDecision);

    private static SourceRead[] InspectSourceReads(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax receiver,
            } member && receiver.Identifier.ValueText is "entities" or "edges" or "conditions" or "submissions"
                && (member.Name.Identifier.ValueText.StartsWith("Get", StringComparison.Ordinal)
                    || member.Name.Identifier.ValueText.StartsWith("List", StringComparison.Ordinal)))
            .Select(read => new SourceRead(read.Expression.ToString(),
                read.GetLocation().GetLineSpan().StartLinePosition.Line + 1, HasPriorReadDecision(read)))
            .ToArray();
    }

    private static bool HasPriorReadDecision(InvocationExpressionSyntax read)
    {
        if (read.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault()
            is not LambdaExpressionSyntax { Body: BlockSyntax body }) return false;
        foreach (var declaration in body.Statements.OfType<LocalDeclarationStatementSyntax>())
        foreach (var variable in declaration.Declaration.Variables)
        {
            if (declaration.Span.End >= read.SpanStart
                || variable.Initializer?.Value is not AwaitExpressionSyntax awaited) continue;
            var decision = awaited.Expression;
            if (decision is InvocationExpressionSyntax
                { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" } configured })
                decision = configured.Expression;
            if (decision is not InvocationExpressionSyntax
                { Expression: MemberAccessExpressionSyntax
                    { Expression: IdentifierNameSyntax { Identifier.ValueText: "RequestAuthorization" },
                      Name.Identifier.ValueText: "RefusalAsync" } } guard
                || guard.ArgumentList.Arguments.Count < 3) continue;
            var capability = guard.ArgumentList.Arguments[2].Expression;
            var isRead = capability is LiteralExpressionSyntax { Token.Value: "records:read" }
                || capability.ToString() is "TeamRolePermissions.RecordsRead" or "ReadEntityRequest.AuthorizationCapability";
            if (!isRead) continue;
            var name = variable.Identifier.ValueText;
            if (body.Statements.OfType<IfStatementSyntax>().Any(refusal =>
                refusal.SpanStart > declaration.Span.End && refusal.Span.End < read.SpanStart
                && refusal.Condition.NormalizeWhitespace().ToString() == name + " is not null"
                && refusal.Statement is ReturnStatementSyntax { Expression: IdentifierNameSyntax returned }
                && returned.Identifier.ValueText == name)) return true;
        }
        return false;
    }

    private static string Probe(string read, string? capability)
    {
        var decision = capability is null ? "" :
            $"var denied = await RequestAuthorization.RefusalAsync(http, tenant, {capability}, record, ct); "
            + "if (denied is not null) return denied;";
        return $$"""
            class Probe
            {
                void Map()
                {
                    app.MapGet("/probe", async (HttpContext http, CancellationToken ct) =>
                    {
                        {{decision}}
                        await {{read}};
                    });
                }
            }
            """;
    }

    private static string StoreReadName(string target) =>
        string.Join(".", target.Split('(', 2)[0].Split('.').TakeLast(2));

    private static string TargetName(string target) =>
        target.Split('(', 2)[0].Split('.')[^1];

    private static bool IsNestedWithin(Type candidate, Type owner)
    {
        for (var type = candidate; type is not null; type = type.DeclaringType)
            if (type == owner) return true;
        return false;
    }
}
