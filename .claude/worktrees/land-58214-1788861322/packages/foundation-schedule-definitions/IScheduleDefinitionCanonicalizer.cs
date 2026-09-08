namespace Harborline.Api.Foundation.ScheduleDefinitions;

/// <summary>Canonicalizes a schedule definition before descriptor admission and persistence.</summary>
public interface IScheduleDefinitionCanonicalizer
{
    /// <summary>Returns the canonical representation of <paramref name="definition"/>.</summary>
    /// <param name="definition">The authored schedule definition.</param>
    /// <returns>The definition representation used for admission and persistence.</returns>
    ScheduleDefinition Canonicalize(ScheduleDefinition definition);
}

/// <summary>Default canonicalizer that preserves the authored definition without transformation.</summary>
public sealed class PassThroughScheduleDefinitionCanonicalizer : IScheduleDefinitionCanonicalizer
{
    /// <inheritdoc />
    public ScheduleDefinition Canonicalize(ScheduleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition;
    }
}
