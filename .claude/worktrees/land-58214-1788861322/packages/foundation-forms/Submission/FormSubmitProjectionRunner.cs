namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// Runs the registered <see cref="IFormSubmitProjection"/> hooks after a form submission has been
/// persisted (ADR 0101 Rev 3.1 Wave 2). The runner is the composition seam the forms engine (or a
/// node submit route) invokes; it holds the ordered set of projections and nothing else.
/// </summary>
public interface IFormSubmitProjectionRunner
{
    /// <summary>
    /// Invokes every registered projection in a deterministic order for the given
    /// <paramref name="context"/>. A projection that does not recognize the form is a no-op; a
    /// projection that throws aborts the run and surfaces to the caller. Returns the aggregated
    /// binding-declared captures that could NOT land across all projections (F3) — empty when
    /// everything landed.
    /// </summary>
    Task<IReadOnlyList<FormSubmitProjectionSkip>> RunAsync(
        FormSubmitContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default <see cref="IFormSubmitProjectionRunner"/>: runs the DI-registered projections in
/// registration order, sequentially, so the outcome is deterministic and each projection sees a
/// stable prior state.
/// </summary>
public sealed class FormSubmitProjectionRunner : IFormSubmitProjectionRunner
{
    private readonly IReadOnlyList<IFormSubmitProjection> _projections;

    /// <summary>Creates a runner over the registered projections (registration order preserved).</summary>
    public FormSubmitProjectionRunner(IEnumerable<IFormSubmitProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(projections);
        _projections = projections.ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FormSubmitProjectionSkip>> RunAsync(
        FormSubmitContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<FormSubmitProjectionSkip>? skips = null;
        foreach (var projection in _projections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reported = await projection.ProjectAsync(context, cancellationToken).ConfigureAwait(false);
            if (reported.Count > 0)
            {
                (skips ??= new List<FormSubmitProjectionSkip>()).AddRange(reported);
            }
        }

        return (IReadOnlyList<FormSubmitProjectionSkip>?)skips ?? Array.Empty<FormSubmitProjectionSkip>();
    }
}
