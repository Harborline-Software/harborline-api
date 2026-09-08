namespace Harborline.Api.Foundation.ReportDefinitions;

/// <summary>Canonicalizes a report definition before descriptor admission and persistence.</summary>
public interface IReportDefinitionCanonicalizer
{
    /// <summary>Returns the canonical representation of <paramref name="definition"/>.</summary>
    /// <param name="definition">The authored report definition.</param>
    /// <returns>The definition representation used for admission and persistence.</returns>
    ReportDefinition Canonicalize(ReportDefinition definition);
}

/// <summary>Default canonicalizer that preserves the authored definition without transformation.</summary>
public sealed class PassThroughReportDefinitionCanonicalizer : IReportDefinitionCanonicalizer
{
    /// <inheritdoc />
    public ReportDefinition Canonicalize(ReportDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition;
    }
}
