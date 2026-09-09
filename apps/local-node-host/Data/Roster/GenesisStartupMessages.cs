namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>
/// Startup message catalogue consumed by the durable genesis reader. Codes are stable; each entry
/// documents the observed state and the operator action that can resolve it. No refusal repairs data.
/// </summary>
public static class GenesisStartupMessages
{
    /// <summary>Ticket 294 slice 2b: a signed roster record predates the unified-party-key wire format.</summary>
    public const string RosterWireFormatPre294Code = "ROSTER_WIRE_FORMAT_PRE_294";

    /// <summary>
    /// This install's signed admission log predates the unified-party key; re-found the install (no production
    /// installs exist — ADR 0066) or re-enrol every device against a fresh genesis.
    /// </summary>
    public const string RosterWireFormatPre294 = RosterWireFormatPre294Code +
        ": this install's signed admission log predates the unified-party key; re-found the install (no production " +
        "installs exist — ADR 0066) or re-enrol every device against a fresh genesis.";

    /// <summary>The current verifier rejected an admission that verifies in the pre-291 format.</summary>
    public const string LegacyFormatCode = "genesis_log_legacy_format";

    /// <summary>Development-only reset: old backups carry the same unsupported signatures.</summary>
    public const string LegacyFormat = LegacyFormatCode +
        ": This store contains pre-291 version-1 admission signatures. Re-initialise this development install: " +
        "stop the host, delete local-node.db and its -wal and -shm sidecars from the data directory, then " +
        "restart to mint a new genesis or re-enrol through a current member. " +
        "A pre-291 backup has the same version-1 signatures and cannot repair this format break. " +
        "This reset discards the local development data; no production installs use this format.";

    /// <summary>An existing log is malformed, tampered with, or has no unique verified genesis.</summary>
    public const string InvalidLogCode = "genesis_log_invalid";

    /// <summary>Restore a verifiable current-format log, preserving its matching identity and tenant.</summary>
    public const string InvalidLog = InvalidLogCode +
        ": Restore the install's verified roster backup with its matching root seed " +
        "and tenant configuration before restarting; do not create another genesis.";

    /// <summary>The verified log admitted this key, but it is absent from the current membership.</summary>
    public const string RemovedCode = "genesis_membership_removed";

    /// <summary>A current member must authorise readmission; changing the account cannot undo removal.</summary>
    public const string Removed = RemovedCode +
        ": This node has been removed from the tenant's roster. Have a current member re-admit this node " +
        "through enrollment before restarting with the updated roster; do not overwrite the roster log.";

    /// <summary>
    /// Ticket 294 slice 2a. The install carries People state for its genesis tenant but no canonical
    /// tenant principal for its founder, so the one party key (the canonical tenant principal id — see
    /// <c>NodeGatePrincipal</c>) cannot be derived. There is no shell-derived fallback: minting an
    /// <c>os:&lt;user&gt;</c> party here is what put the roster and the grant store on two key spaces.
    /// </summary>
    public const string PartyUnresolvedCode = "GENESIS_PARTY_UNRESOLVED";

    /// <summary>Run founder setup before starting the node, or re-found the install.</summary>
    public const string PartyUnresolved = PartyUnresolvedCode +
        ": This install has no canonical tenant principal for its founder, so the genesis party key " +
        "cannot be derived. Run founder setup (the founder bootstrap ceremony and its tenant-membership " +
        "attach) before starting the node, or re-found the install against a fresh data directory. " +
        "The node will not mint a shell-derived party id; that key is not the key the grant store reads.";

    /// <summary>The derived key has no admission in this tenant; a changed seed is indistinguishable.</summary>
    public const string MismatchCode = "genesis_identity_mismatch";

    /// <summary>Readmit an unknown key, or recover the original seed after an account/keystore change.</summary>
    public const string Mismatch = MismatchCode +
        ": This node is not an admitted member of the tenant's roster. Have a current member re-admit this node " +
        "through enrollment. If an OS-account change selected a different root seed, " +
        "Restore the root seed belonging to this install's signed genesis, " +
        "or use a separate data directory for a new install; do not overwrite the roster log.";
}
