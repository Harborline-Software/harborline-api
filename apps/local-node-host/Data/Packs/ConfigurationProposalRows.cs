namespace Harborline.Api.LocalNodeHost.Data.Packs;

/// <summary>
/// One Proposed change (T-461, DES-0044 governance-ck-1). The row is the author's working state: the
/// baseline generation it was started from, the autosaved edits, and the currently recorded check.
/// Nothing here is effective. <see cref="EditsJson"/> is the platform's edit list verbatim, so the
/// working digest is re-derived from the stored edits rather than trusted from a stored string.
/// </summary>
internal sealed class ConfigurationProposalRow
{
    public required string Tenant { get; set; }
    public required string ProposalId { get; set; }
    public required string BaselineDigest { get; set; }
    public required string EditsJson { get; set; }
    public required string StartedBy { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset AutosavedAt { get; set; }
    public int SavedVersionCount { get; set; }
    /// <summary>The working digest the recorded check observed, or null when no check is recorded.</summary>
    public string? CheckedDigest { get; set; }
    /// <summary>The verification receipt identity the host retained for that check (T-463 owns its content).</summary>
    public string? CheckReceiptId { get; set; }
}

/// <summary>
/// One immutable Saved version (T-461, DES-0044 governance-ck-1). Rows are insert-only: the store
/// never updates one, so a later edit to the Proposed change cannot reach a checkpoint already taken.
/// </summary>
internal sealed class ConfigurationSavedVersionRow
{
    public required string Tenant { get; set; }
    public required string ProposalId { get; set; }
    public int Ordinal { get; set; }
    public required string Digest { get; set; }
    public required string BaselineDigest { get; set; }
    public required string Author { get; set; }
    public required string Rationale { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public required string EditsJson { get; set; }
}

/// <summary>
/// One Released package (T-461, DES-0044 governance-ck-6). <see cref="Document"/> is the platform's
/// provider-neutral export, byte for byte; <see cref="Digest"/> is re-derived from those bytes on
/// every read, so the digest shown to the author is the artifact and not a label beside it.
/// The signature is the api's, over that digest: ADR 0097 decision 6 puts signing on this side.
/// </summary>
internal sealed class ConfigurationReleasedPackageRow
{
    public required string Tenant { get; set; }
    public required string Digest { get; set; }
    public required string ProposalId { get; set; }
    public required string SavedVersionDigest { get; set; }
    public required string BaselineDigest { get; set; }
    public required string PackageKey { get; set; }
    public required string Revision { get; set; }
    public required byte[] Document { get; set; }
    public required string SignatureJson { get; set; }
    public required string ReleasedBy { get; set; }
    public required string CheckReceiptId { get; set; }
    public DateTimeOffset ReleasedAt { get; set; }
}
