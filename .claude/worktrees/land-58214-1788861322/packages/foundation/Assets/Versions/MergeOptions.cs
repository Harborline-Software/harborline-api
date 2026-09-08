using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Versions;

/// <summary>
/// Options for <see cref="IVersionStore.MergeAsync"/>. Phase A stubs the operation; see
/// plan D-CRDT-ROUTE.
/// </summary>
public sealed record MergeOptions(ActorId Actor, string? Resolver = null);
