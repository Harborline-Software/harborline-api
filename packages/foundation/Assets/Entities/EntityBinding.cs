using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Entities;

/// <summary>
/// Immutable definition-and-engine provenance persisted on an entity at mint time.
/// </summary>
/// <param name="SchemaRef">The content-addressed schema the entity validated against.</param>
/// <param name="DefinitionId">The definition that produced the entity.</param>
/// <param name="DefinitionVersion">The exact definition revision in force at mint time.</param>
/// <param name="EngineVersion">The evaluator version in force at mint time.</param>
/// <param name="LocaleChain">The ordered locale-preference chain in force at mint time.</param>
/// <param name="SubmittedAt">The UTC instant at which the entity was submitted.</param>
public sealed record EntityBinding(
    SchemaId SchemaRef,
    string DefinitionId,
    string DefinitionVersion,
    string EngineVersion,
    IReadOnlyList<string> LocaleChain,
    DateTimeOffset SubmittedAt);
