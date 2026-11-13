using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.Workflow.Interpreter;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

public sealed class WorkflowScheduleDaemonRefusalTests
{
    [Theory]
    [InlineData("authorization", "AuthorizationDeniedException")]
    [InlineData("gate", "WorkflowGateUnenforcedException")]
    [InlineData("admission", "WorkflowAdmissionException")]
    public async Task Refused_middle_trigger_is_logged_and_retried_without_blocking_other_triggers(string kind, string refusalType)
    {
        var triggers = new[]
        {
            WorkflowTrigger.For(WorkflowTriggerKind.Schedule, "first", "daily-first", DateTimeOffset.UnixEpoch),
            WorkflowTrigger.For(WorkflowTriggerKind.Schedule, "middle", "daily-middle", DateTimeOffset.UnixEpoch),
            WorkflowTrigger.For(WorkflowTriggerKind.Schedule, "third", "daily-third", DateTimeOffset.UnixEpoch),
        };
        var source = Substitute.For<IWorkflowScheduleSource>();
        var reads = 0;
        source.GetDueTriggersAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ => { reads++; return Task.FromResult<IReadOnlyList<WorkflowTrigger>>(triggers); });
        var store = Substitute.For<IWorkflowStore>();
        store.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult<WorkflowInstanceRecord?>(new()
        {
            Id = call.Arg<string>(), TenantId = "local", DefinitionKey = "definition", DefinitionVersion = "1",
            CurrentStep = "waiting", Status = WorkflowStatus.Running, StateJson = "{}",
        }));
        var dispatcher = new RecordingDispatcher(kind);
        var log = new ScheduleLog();
        var daemon = new WorkflowScheduleDaemon(source, dispatcher, TimeProvider.System, log,
            authorizationGate: TestAuthorization.Gate(request => kind != "authorization" || request.Target.RecordId != "middle"),
            store: store);

        var firstError = await Record.ExceptionAsync(() => daemon.TickAsync());
        var secondError = await Record.ExceptionAsync(() => daemon.TickAsync());

        Assert.True(firstError is null && secondError is null,
            $"Tick aborted: {firstError?.GetType().Name}; dispatched: {string.Join(",", dispatcher.Advanced)}; source reads: {reads}; second: {secondError?.GetType().Name}");
        Assert.Equal(2, reads);
        Assert.Equal(new[] { "first", "third", "first", "third" }, dispatcher.Advanced);
        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, entry =>
        {
            Assert.Contains("middle", entry.Message);
            Assert.Contains("daily-middle", entry.Message);
            Assert.Contains(refusalType, entry.Message);
            Assert.Equal(refusalType, entry.Error?.GetType().Name);
        });

        dispatcher.Cancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => daemon.TickAsync());
        Assert.Equal(new[] { "first", "third", "first", "third" }, dispatcher.Advanced);
    }

    private sealed class RecordingDispatcher(string kind) : IWorkflowTriggerDispatcher
    {
        public List<string> Advanced { get; } = [];
        public bool Cancel { get; set; }
        public Task<WorkflowDispatchResult> DispatchAsync(WorkflowTrigger trigger, CancellationToken ct = default)
        {
            if (Cancel) throw new OperationCanceledException();
            if (trigger.InstanceId == "middle" && kind == "gate") throw new WorkflowGateUnenforcedException("gate");
            if (trigger.InstanceId == "middle" && kind == "admission") throw new WorkflowAdmissionException(new WorkflowAdmissionResult
            {
                Violations = [new("workflow.admission.gate_on_autonomous_step", "refused", "daily-middle")],
            });
            Advanced.Add(trigger.InstanceId);
            return Task.FromResult(WorkflowDispatchResult.Advanced);
        }
        public Task<WorkflowDispatchResult> DispatchAsync(WorkflowTrigger trigger, AuthorizationDecision decision, CancellationToken ct = default)
            => DispatchAsync(trigger, ct);
        public Task<WorkflowDispatchResult> DispatchAsync(WorkflowTrigger trigger, WorkflowDispatchAuthority authority, CancellationToken ct = default)
            => DispatchAsync(trigger, ct);
    }

    private sealed class ScheduleLog : ILogger<WorkflowScheduleDaemon>
    {
        public List<(Exception? Error, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((exception, formatter(state, exception)));
    }
}
