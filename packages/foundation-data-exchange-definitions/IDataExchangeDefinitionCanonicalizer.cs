namespace Harborline.Api.Foundation.DataExchangeDefinitions;

/// <summary>Canonicalizes a data-exchange definition before descriptor admission and persistence.</summary>
public interface IDataExchangeDefinitionCanonicalizer
{
    /// <summary>Returns the canonical representation of <paramref name="definition"/>.</summary>
    /// <param name="definition">The authored data-exchange definition.</param>
    /// <returns>The definition representation used for admission and persistence.</returns>
    DataExchangeDefinition Canonicalize(DataExchangeDefinition definition);
}

/// <summary>Default canonicalizer that preserves the authored definition without transformation.</summary>
public sealed class PassThroughDataExchangeDefinitionCanonicalizer : IDataExchangeDefinitionCanonicalizer
{
    /// <inheritdoc />
    public DataExchangeDefinition Canonicalize(DataExchangeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition;
    }
}
