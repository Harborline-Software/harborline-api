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
}

/// <summary>Optional observer for behavioral write-pipeline fixtures and host diagnostics.</summary>
public interface IWritePipelineObserver
{
    /// <summary>Records that the real implementation entered a kernel write stage.</summary>
    void OnStage(WritePipelineStage stage);
}
