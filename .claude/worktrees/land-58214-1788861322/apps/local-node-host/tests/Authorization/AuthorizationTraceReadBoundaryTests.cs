using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Tests.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationTraceReadBoundaryTests
{
    [Fact]
    public void Http_trace_read_reaches_the_authorized_reader_and_the_reader_reaches_the_gate()
    {
        var reads = AuditAppendSymbolInventory.Discover(method =>
            method.DeclaringType == typeof(AuthorizationTraceReader)
                && method.Name == nameof(AuthorizationTraceReader.ReadWithDecisionAsync) ? "read" : null);
        var http = Assert.Single(reads, site => site.File.StartsWith("apps/", StringComparison.Ordinal));
        Assert.Equal("apps/local-node-host/Health/AuthorizationAdminRoutes.cs", http.File);
        Assert.True(http.Line > 0);
        var decisions = AuditAppendSymbolInventory.Discover(method =>
            method.DeclaringType == typeof(AuthorizationGate) && method.Name == nameof(AuthorizationGate.DecideAsync)
                ? "gate" : null);
        Assert.Single(decisions, site => site.File == "packages/kernel-audit/AuthorizationTraceReader.cs");
    }
}
