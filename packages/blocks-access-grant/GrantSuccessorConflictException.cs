namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>A scope-narrowing successor identity was already claimed; the atomic write is refused.</summary>
public sealed class GrantSuccessorConflictException(GrantId successor, Exception? innerException = null)
    : InvalidOperationException("The successor grant identity already exists.", innerException)
{
    /// <summary>The conflicting identity, available to diagnostics but not included in the wire refusal.</summary>
    public GrantId Successor { get; } = successor;
}
