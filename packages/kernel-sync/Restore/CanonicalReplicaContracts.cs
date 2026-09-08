using Harborline.Api.Kernel.Sync.Identity;

namespace Harborline.Api.Kernel.Sync.Restore;

/// <summary>Creates a fresh sync identity for a restored replica.</summary>
public interface INodeIdentityFactory
{
    /// <summary>Creates an identity that has never published under the replaced node id.</summary>
    NodeIdentity CreateFresh();
}

/// <summary>Canonical document bytes and their holder-published SHA-256 digest.</summary>
public sealed record CanonicalReplicaDocument(
    string DocumentId,
    byte[] Snapshot,
    string Sha256);

/// <summary>Raised when canonical holder bytes do not match their published digest.</summary>
public sealed class ContentHashMismatchException : Exception
{
    /// <summary>Creates a mismatch for the named canonical document.</summary>
    public ContentHashMismatchException(string documentId)
        : base($"Canonical document '{documentId}' failed content-hash verification.")
    {
        DocumentId = documentId;
    }

    /// <summary>The logical document whose canonical bytes failed verification.</summary>
    public string DocumentId { get; }
}
