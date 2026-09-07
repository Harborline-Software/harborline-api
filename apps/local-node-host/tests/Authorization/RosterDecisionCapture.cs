using System.Diagnostics;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

internal sealed class RosterDecisionCapture : IDisposable
{
    private readonly Activity parent = new Activity("roster-test").Start();
    private readonly ActivityListener listener;
    private readonly KeyPair key = KeyPair.Generate();
    private readonly InMemoryAuditTrail trail = new();
    internal AuthorizationRefusalAudit Audit => new(trail, new Ed25519Signer(key), NullLogger<AuthorizationRefusalAudit>.Instance);
    internal List<AuthorizationDecisionEvidence> Evidence { get; } = [];

    internal RosterDecisionCapture()
    {
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Harborline.AuthorizationGate",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.Parent == parent && activity.GetCustomProperty("authorization.evidence") is AuthorizationDecisionEvidence evidence)
                    Evidence.Add(evidence);
            }
        };
        ActivitySource.AddActivityListener(listener);
    }

    internal AuthorizationDecisionEvidence AssertSingle(bool allowed)
    {
        var evidence = Assert.Single(Evidence);
        Assert.Equal(allowed, evidence.Allowed);
        Assert.NotNull(evidence.Roster);
        Assert.Contains(evidence.Project()[1].Facts, fact => fact.StartsWith("roster:party:"));
        return evidence;
    }

    internal async Task AssertAuditAsync(TenantId tenant)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in trail.QueryAsync(new AuditQuery(tenant))) records.Add(record);
        var denied = Evidence.Where(evidence => !evidence.Allowed).ToArray();
        Assert.Equal(denied.Length, records.Count);
        for (var index = 0; index < records.Count; index++)
        {
            Assert.Equal(false, records[index].Payload.Payload.Body["preDecision"]);
            var trace = Assert.IsAssignableFrom<IReadOnlyList<AuthorizationTraceStep>>(
                records[index].Payload.Payload.Body["decisionEvidence"]);
            Assert.Equal(denied[index].Project().SelectMany(step => step.Facts), trace.SelectMany(step => step.Facts));
        }
    }

    public void Dispose() { listener.Dispose(); parent.Dispose(); key.Dispose(); }
}
