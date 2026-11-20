using System.Diagnostics;
using System.Diagnostics.Metrics;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.Audit;

[CollectionDefinition("Trace enforcement disagreement", DisableParallelization = true)]
public sealed class TraceEnforcementDisagreementCollection;

[Collection("Trace enforcement disagreement")]
public sealed class TraceEnforcementDisagreementTests
{
    public TraceEnforcementDisagreementTests() => TraceEnforcementDisagreement.Initialize();

    private static readonly TenantId Tenant = new("t575-tenant");
    private static readonly DateTimeOffset At = new(2026, 9, 2, 14, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("audit.authority.decision_denied")]
    [InlineData("audit.authority.tenant_mismatch")]
    [InlineData("audit.authority.principal_mismatch")]
    [InlineData("audit.authority.instant_mismatch")]
    [InlineData("audit.authority.target_mismatch")]
    [InlineData("audit.authority.act_mismatch")]
    public async Task Each_refusal_code_adds_one_and_marks_an_error_span(string code)
    {
        using var keys = KeyPair.Generate();
        using var otherKeys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var other = new Ed25519Signer(otherKeys);
        var (allowed, _) = await AuthoritySnapshotTests.DecisionAsync(signer, Tenant, At, ("grant", 1));
        var (denied, _) = await AuthoritySnapshotTests.DecisionAsync(signer, Tenant, At);
        var record = await AuthoritySnapshotTests.RecordAsync(
            code == "audit.authority.principal_mismatch" ? other : signer,
            code == "audit.authority.tenant_mismatch" ? new TenantId("other") : Tenant,
            code == "audit.authority.instant_mismatch" ? At.AddTicks(1) : At,
            recordId: code == "audit.authority.target_mismatch" ? "other" : "audit-record",
            operation: code == "audit.authority.act_mismatch" ? "records:delete" : "records:write");
        using var fixture = AuditTrailFixture.Create(false, signer);
        using var observer = new Observer();
        var before = observer.Read();
        var exception = await Assert.ThrowsAsync<AuthorizedAuditRefusedException>(async () =>
            await fixture.Authorized.AppendAuthorizedAsync(record, code == "audit.authority.decision_denied" ? denied : allowed));
        Assert.Equal(code, exception.Code);
        Assert.Equal(before + 1, observer.Read());
        var span = Assert.Single(observer.Spans);
        Assert.Equal("authorization.trace.disagreement", span.OperationName);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(code, span.GetTagItem("error.type"));
        Assert.Equal(new[] { "error.type" }, span.TagObjects.Select(tag => tag.Key));
        Assert.Empty(observer.MeasurementTags);
    }

    [Fact]
    public async Task A_refused_append_recorded_against_an_allow_is_counted()
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var (allowed, _) = await AuthoritySnapshotTests.DecisionAsync(signer, Tenant, At, ("grant", 1));
        var record = await AuthoritySnapshotTests.RecordAsync(signer, Tenant, At);
        using var fixture = AuditTrailFixture.Create(false, signer);
        using var observer = new Observer();
        var before = observer.Read();
        await Assert.ThrowsAsync<AuthorizedAuditRefusedException>(async () =>
            await ((IRefusedAuditTrail)fixture.Trail).AppendRefusedAsync(record, allowed));
        Assert.Equal(before + 1, observer.Read());
    }

    [Fact]
    public async Task A_captured_authority_that_disagrees_is_counted()
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var (allowed, _) = await AuthoritySnapshotTests.DecisionAsync(signer, Tenant, At, ("grant", 1));
        var record = await AuthoritySnapshotTests.RecordAsync(signer, Tenant, At);
        using var observer = new Observer();
        var before = observer.Read();
        Assert.Throws<AuthorizedAuditRefusedException>(() => CapturedAuditAuthority.Capture(
            new TenantId("other"), record.Actor!.Value, At, record.Target!.Value, record.Act!.Value, allowed));
        Assert.Equal(before + 1, observer.Read());
    }

    [Fact]
    public async Task An_agreeing_append_adds_nothing()
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var (allowed, _) = await AuthoritySnapshotTests.DecisionAsync(signer, Tenant, At, ("grant", 1));
        var record = await AuthoritySnapshotTests.RecordAsync(signer, Tenant, At);
        using var fixture = AuditTrailFixture.Create(false, signer);
        using var observer = new Observer();
        var before = observer.Read();
        await fixture.Authorized.AppendAuthorizedAsync(record, allowed);
        Assert.Equal(before, observer.Read());
        Assert.Empty(observer.Spans);
    }

    [Fact]
    public async Task Without_a_source_listener_the_current_activity_is_marked()
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var (denied, _) = await AuthoritySnapshotTests.DecisionAsync(signer, Tenant, At);
        var record = await AuthoritySnapshotTests.RecordAsync(signer, Tenant, At);
        using var fixture = AuditTrailFixture.Create(false, signer);
        using var activity = new Activity("parent").Start();
        await Assert.ThrowsAsync<AuthorizedAuditRefusedException>(async () =>
            await fixture.Authorized.AppendAuthorizedAsync(record, denied));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("audit.authority.decision_denied", activity.GetTagItem("error.type"));
        Assert.Equal(new[] { "error.type" }, activity.TagObjects.Select(tag => tag.Key));
    }

    [Fact]
    public void The_counter_is_reported_before_any_disagreement()
    {
        using var listener = new MeterListener();
        var values = new List<long>();
        var enableNew = false;
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (enableNew && instrument.Meter.Name == "Harborline.Kernel.Audit" &&
                instrument.Name == "authorization.trace.disagreements")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Assert.True(tags.IsEmpty);
            values.Add(value);
        });
        listener.Start();
        enableNew = true;
        using var fresh = new TraceEnforcementDisagreement();
        listener.RecordObservableInstruments();
        Assert.Equal(0, Assert.Single(values));
    }

    private sealed class Observer : IDisposable
    {
        private readonly MeterListener _meter = new();
        private readonly ActivityListener _activities = new();
        private readonly List<long> _values = [];
        public List<Activity> Spans { get; } = [];
        public List<string> MeasurementTags { get; } = [];
        public Observer()
        {
            _meter.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Harborline.Kernel.Audit" && instrument.Name == "authorization.trace.disagreements")
                    listener.EnableMeasurementEvents(instrument);
            };
            _meter.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                _values.Add(value);
                foreach (var tag in tags) MeasurementTags.Add(tag.Key);
            });
            _meter.Start();
            _activities.ShouldListenTo = source => source.Name == "Harborline.Kernel.Audit";
            _activities.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData;
            _activities.ActivityStopped = activity => Spans.Add(activity);
            ActivitySource.AddActivityListener(_activities);
        }
        public long Read()
        {
            _values.Clear();
            _meter.RecordObservableInstruments();
            return Assert.Single(_values);
        }
        public void Dispose() { _activities.Dispose(); _meter.Dispose(); }
    }
}
