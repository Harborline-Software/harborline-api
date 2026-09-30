using System.Collections.ObjectModel;

namespace Harborline.Api.Kernel.Runtime;

/// <summary>The kernel-owned ADR-0038 stages for every admitted write.</summary>
public enum WritePipelineStage
{
    Authorize,
    Bind,
    Mutate,
    Validate,
    Commit,
    React,
}

/// <summary>Canonical order and stable caller-facing names for ADR-0038 write stages.</summary>
public static class WritePipeline
{
    private static readonly WritePipelineStage[] Stages =
    [
        WritePipelineStage.Authorize,
        WritePipelineStage.Bind,
        WritePipelineStage.Mutate,
        WritePipelineStage.Validate,
        WritePipelineStage.Commit,
        WritePipelineStage.React,
    ];

    // Array.AsReadOnly wraps the backing array once, so a caller holding Order cannot recover the
    // array by casting (unlike returning the array itself as IReadOnlyList<T>, which permits an
    // `(WritePipelineStage[])WritePipeline.Order` cast back to a mutable array) and cannot mutate
    // the kernel's own declaration of the order.
    private static readonly ReadOnlyCollection<WritePipelineStage> OrderValue = Array.AsReadOnly(Stages);

    /// <summary>Stages in the only permitted write-pipeline order.</summary>
    public static IReadOnlyList<WritePipelineStage> Order => OrderValue;

    /// <summary>The stable names of every stage in order: what a write that reached react has completed.</summary>
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(Stages.Select(NameOf).ToArray());

    /// <summary>Renders the stable result name used by existing write callers.</summary>
    public static string NameOf(WritePipelineStage stage) => stage switch
    {
        WritePipelineStage.Authorize => "authorize",
        WritePipelineStage.Bind => "bind",
        WritePipelineStage.Mutate => "mutate",
        WritePipelineStage.Validate => "validate",
        WritePipelineStage.Commit => "commit",
        WritePipelineStage.React => "react",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown write-pipeline stage."),
    };

    /// <summary>
    /// The one ADR-0038 executor (DES-0029 ck-10). Runs <paramref name="write"/>'s stages in
    /// <see cref="Order"/> and is the only caller of them. A null bind result means the write is already
    /// settled: the pipeline stops after bind and returns default. A refusal is an exception from its stage,
    /// and no later stage runs.
    /// </summary>
    public static async ValueTask<TResult?> RunAsync<TBound, TMutation, TSealed, TResult>(
        KernelWrite<TBound, TMutation, TSealed, TResult> write,
        IWritePipelineObserver? observer,
        CancellationToken ct)
        where TBound : class
    {
        ArgumentNullException.ThrowIfNull(write);
        Enter(WritePipelineStage.Authorize, observer, ct);
        await write.AuthorizeAsync(ct).ConfigureAwait(false);
        Enter(WritePipelineStage.Bind, observer, ct);
        var bound = await write.BindAsync(ct).ConfigureAwait(false);
        if (bound is null) return default;
        Enter(WritePipelineStage.Mutate, observer, ct);
        var mutation = await write.MutateAsync(bound, ct).ConfigureAwait(false);
        Enter(WritePipelineStage.Validate, observer, ct);
        var validated = await write.ValidateAsync(bound, mutation, ct).ConfigureAwait(false);
        Enter(WritePipelineStage.Commit, observer, ct);
        await write.CommitAsync(validated, ct).ConfigureAwait(false);
        Enter(WritePipelineStage.React, observer, ct);
        return await write.ReactAsync(validated, ct).ConfigureAwait(false);
    }

    private static void Enter(WritePipelineStage stage, IWritePipelineObserver? observer, CancellationToken ct)
    {
        observer?.OnStage(stage);
        ct.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// One admitted write as its six ADR-0038 stages. Only <see cref="WritePipeline.RunAsync"/> calls them.
/// The signatures carry the order: mutate takes the bound state, validate takes the mutation, and commit
/// and react take only the sealed value validate returned, so nothing after validate can amend the record.
/// </summary>
public abstract class KernelWrite<TBound, TMutation, TSealed, TResult>
    where TBound : class
{
    /// <summary>Refuses an unauthorized write before anything is read.</summary>
    protected internal abstract ValueTask AuthorizeAsync(CancellationToken ct);

    /// <summary>Reads the state the write applies to; null when the write is already settled.</summary>
    protected internal abstract ValueTask<TBound?> BindAsync(CancellationToken ct);

    /// <summary>Derives the record as it will be stored (defaults, calculations, early automations).</summary>
    protected internal abstract ValueTask<TMutation> MutateAsync(TBound bound, CancellationToken ct);

    /// <summary>Admits the mutated record and seals it; fails closed.</summary>
    protected internal abstract ValueTask<TSealed> ValidateAsync(TBound bound, TMutation mutation, CancellationToken ct);

    /// <summary>Persists exactly the sealed value.</summary>
    protected internal abstract ValueTask CommitAsync(TSealed validated, CancellationToken ct);

    /// <summary>Creates new work after commit; it cannot amend the committed record.</summary>
    protected internal abstract ValueTask<TResult> ReactAsync(TSealed validated, CancellationToken ct);
}

/// <summary>Optional observer for behavioral write-pipeline fixtures and host diagnostics.</summary>
public interface IWritePipelineObserver
{
    /// <summary>Records that the real implementation entered a kernel write stage.</summary>
    void OnStage(WritePipelineStage stage);
}
