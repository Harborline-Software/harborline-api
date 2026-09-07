using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Assets.Versions;

/// <summary>
/// Options for <see cref="IVersionStore.BranchAsync"/>. Phase A stubs the operation; see
/// plan D-CRDT-ROUTE for the reasoning.
/// </summary>
public sealed record BranchOptions(ActorId Actor, string? Label = null);
