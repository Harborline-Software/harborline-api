namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>Shared monitor for in-memory Administrator append and authorization bootstrap commit.</summary>
internal sealed class InMemoryAuthorizationBootstrapFence
{
    internal object Gate { get; } = new();
}
