using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>Resolves the exact projection definition to persist with a new form submission.</summary>
public interface IFormSubmissionBindingResolver
{
    /// <summary>Resolves projection coordinates for the admitted definition and submission instant.</summary>
    ValueTask<DefinitionCoordinates?> ResolveAsync(
        FormDefinition definition, DateTimeOffset submittedAt, CancellationToken ct = default);
}
