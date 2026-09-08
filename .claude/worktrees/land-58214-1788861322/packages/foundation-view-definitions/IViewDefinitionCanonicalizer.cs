namespace Harborline.Api.Foundation.ViewDefinitions;

/// <summary>Canonicalizes a view definition before descriptor admission and persistence.</summary>
public interface IViewDefinitionCanonicalizer
{
    /// <summary>Returns the canonical representation of <paramref name="definition"/>.</summary>
    /// <param name="definition">The authored view definition.</param>
    /// <returns>The definition representation used for admission and persistence.</returns>
    ViewDefinition Canonicalize(ViewDefinition definition);
}

/// <summary>Default canonicalizer that preserves the authored definition without transformation.</summary>
public sealed class PassThroughViewDefinitionCanonicalizer : IViewDefinitionCanonicalizer
{
    /// <inheritdoc />
    public ViewDefinition Canonicalize(ViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition;
    }
}
