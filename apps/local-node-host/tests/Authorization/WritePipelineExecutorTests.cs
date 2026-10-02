using Harborline.Api.Kernel.Runtime;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// ck-10 (DES-0029): the shared ADR-0038 executor, <see cref="WritePipeline.RunAsync"/>. Every stage has an
/// observable effect, a refusal at any stage stops the write there, and commit and react receive exactly
/// the value validate sealed.
/// </summary>
public sealed class WritePipelineExecutorTests
{
    [Fact(DisplayName = "ck-10 executor: the six stages run in the declared ADR 0038 order, observed and entered alike")]
    public async Task RunsTheSixStagesInTheDeclaredOrder()
    {
        var observer = new Observer();
        var write = new ProbeWrite();

        var result = await WritePipeline.RunAsync(write, observer, CancellationToken.None);

        Assert.Equal(WritePipeline.Order, write.Entered);
        Assert.Equal(WritePipeline.Order, observer.Stages);
        Assert.Equal("reacted:sealed:mutated:bound", result);
        Assert.True(write.Committed);
    }

    [Theory(DisplayName = "ck-10 executor: a refusal at a stage stops the write there, and only a react refusal leaves the commit")]
    [InlineData(WritePipelineStage.Authorize)]
    [InlineData(WritePipelineStage.Bind)]
    [InlineData(WritePipelineStage.Mutate)]
    [InlineData(WritePipelineStage.Validate)]
    [InlineData(WritePipelineStage.Commit)]
    [InlineData(WritePipelineStage.React)]
    public async Task ARefusalStopsTheWriteAtItsStage(WritePipelineStage refusing)
    {
        var write = new ProbeWrite { Refuse = refusing };

        var refusal = await Assert.ThrowsAsync<StageRefused>(() =>
            WritePipeline.RunAsync(write, null, CancellationToken.None).AsTask());

        Assert.Equal(refusing, refusal.Stage);
        Assert.Equal(WritePipeline.Order.TakeWhile(stage => stage != refusing).Append(refusing), write.Entered);
        Assert.Equal(refusing == WritePipelineStage.React, write.Committed);
    }

    [Fact(DisplayName = "ck-10 executor: validate admits the mutate output, and commit and react get exactly the sealed value")]
    public async Task CommitAndReactReceiveExactlyTheSealedValue()
    {
        var write = new ProbeWrite();

        await WritePipeline.RunAsync(write, null, CancellationToken.None);

        Assert.Same(write.Mutation, write.Validated);
        Assert.Same(write.Sealed, write.CommittedValue);
        Assert.Same(write.Sealed, write.ReactedValue);
    }

    [Fact(DisplayName = "ck-10 executor: a settled bind stops after bind, commits nothing and returns default")]
    public async Task ASettledBindStopsAfterBind()
    {
        var observer = new Observer();
        var write = new ProbeWrite { Settled = true };

        var result = await WritePipeline.RunAsync(write, observer, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(WritePipeline.Order.Take(2), write.Entered);
        Assert.Equal(WritePipeline.Order.Take(2), observer.Stages);
        Assert.False(write.Committed);
    }

    [Fact(DisplayName = "ck-10 executor: a cancelled write enters no stage")]
    public async Task ACancelledWriteEntersNoStage()
    {
        var write = new ProbeWrite();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WritePipeline.RunAsync(write, null, new CancellationToken(canceled: true)).AsTask());

        Assert.Empty(write.Entered);
    }

    [Fact(DisplayName = "ck-10 executor: cancellation after commit does not stop react, which runs with a token that cannot cancel")]
    public async Task CancellationAfterCommitStillRunsReact()
    {
        using var cancellation = new CancellationTokenSource();
        var write = new ProbeWrite { OnCommit = cancellation.Cancel };

        var result = await WritePipeline.RunAsync(write, null, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("reacted:sealed:mutated:bound", result);
        Assert.Equal(WritePipeline.Order, write.Entered);
        Assert.False(write.ReactToken.CanBeCanceled);
    }

    [Fact(DisplayName = "ck-10 executor: cancellation before commit still stops the write before it commits")]
    public async Task CancellationBeforeCommitStopsTheWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var write = new ProbeWrite { OnValidate = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WritePipeline.RunAsync(write, null, cancellation.Token).AsTask());

        Assert.False(write.Committed);
        Assert.Equal(WritePipelineStage.Validate, write.Entered[^1]);
    }

    [Fact(DisplayName = "ck-10 executor: a missing write is refused before any stage")]
    public async Task AMissingWriteIsRefused()
    {
        var observer = new Observer();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            WritePipeline.RunAsync<string, object, object, string>(null!, observer, CancellationToken.None).AsTask());

        Assert.Empty(observer.Stages);
    }

    private sealed class StageRefused(WritePipelineStage stage) : Exception(stage.ToString())
    {
        public WritePipelineStage Stage { get; } = stage;
    }

    private sealed class Observer : IWritePipelineObserver
    {
        public List<WritePipelineStage> Stages { get; } = [];
        public void OnStage(WritePipelineStage stage) => Stages.Add(stage);
    }

    private sealed class ProbeWrite : KernelWrite<string, object, object, string>
    {
        public WritePipelineStage? Refuse { get; init; }
        public bool Settled { get; init; }
        public Action? OnValidate { get; init; }
        public Action? OnCommit { get; init; }
        public CancellationToken ReactToken { get; private set; }
        public List<WritePipelineStage> Entered { get; } = [];
        public object? Mutation { get; private set; }
        public object? Validated { get; private set; }
        public object? Sealed { get; private set; }
        public object? CommittedValue { get; private set; }
        public object? ReactedValue { get; private set; }
        public bool Committed => CommittedValue is not null;

        protected override ValueTask AuthorizeAsync(CancellationToken ct)
        {
            Enter(WritePipelineStage.Authorize);
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<string?> BindAsync(CancellationToken ct)
        {
            Enter(WritePipelineStage.Bind);
            return ValueTask.FromResult(Settled ? null : "bound");
        }

        protected override ValueTask<object> MutateAsync(string bound, CancellationToken ct)
        {
            Enter(WritePipelineStage.Mutate);
            Mutation = new Box("mutated:" + bound);
            return ValueTask.FromResult(Mutation);
        }

        protected override ValueTask<object> ValidateAsync(string bound, object mutation, CancellationToken ct)
        {
            Enter(WritePipelineStage.Validate);
            OnValidate?.Invoke();
            Validated = mutation;
            Sealed = new Box("sealed:" + mutation);
            return ValueTask.FromResult(Sealed);
        }

        protected override ValueTask CommitAsync(object validated, CancellationToken ct)
        {
            Enter(WritePipelineStage.Commit);
            CommittedValue = validated;
            OnCommit?.Invoke();
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<string> ReactAsync(object validated, CancellationToken ct)
        {
            ReactedValue = validated;
            ReactToken = ct;
            Enter(WritePipelineStage.React);
            return ValueTask.FromResult("reacted:" + validated);
        }

        private void Enter(WritePipelineStage stage)
        {
            Entered.Add(stage);
            if (stage == Refuse) throw new StageRefused(stage);
        }

        private sealed record Box(string Value)
        {
            public override string ToString() => Value;
        }
    }
}
