namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// The marker "user" type for the node's <c>IPasswordHasher&lt;NodeWebUser&gt;</c> (ADR 0097
/// Argon2id substrate). The Argon2id hasher is <c>TUser</c>-agnostic — it never reads the user —
/// so this is an empty type used only to bind the generic. A single shared instance is enough.
/// </summary>
public sealed class NodeWebUser
{
    /// <summary>The shared instance passed to the hasher (the hasher ignores it).</summary>
    public static readonly NodeWebUser Instance = new();

    private NodeWebUser() { }
}
