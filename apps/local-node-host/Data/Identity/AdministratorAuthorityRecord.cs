using System.Security.Cryptography;
using System.Text;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// One APPEND-ONLY event in the node's administrator-authority log (ADR 0066 clauses 4–8, migration step 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The log is the state AND the audit.</b> ADR 0066 clause 5 requires the installer's state gate to be
/// evaluated "against the same canonical state every other read uses" and re-evaluated inside the transaction
/// that writes — never "a cached flag, a startup boolean, or a sentinel file". Clause 4 additionally requires
/// the establishing write to be permanently audited with its provenance. A single append-only, hash-chained
/// event log satisfies both at once, and it does so ATOMICALLY: the gate read, the establishing write, and the
/// audit entry are one table in one transaction. Two tables (mutable state + a separate audit) could not be
/// written atomically here — the node's other audit sink lives in a DIFFERENT <c>DbContext</c>, which is a
/// second connection and therefore a second transaction.
/// </para>
/// <para>
/// <b>Fold, do not overwrite.</b> Current administrator state is the fold of the log per party: the LATEST
/// event decides whether that party is an administrator, and <see cref="ExpiresAtUtc"/> decides whether the
/// authority is still in date. No row is ever updated or deleted, so:
/// </para>
/// <list type="bullet">
///   <item><description>the establishment facts (provenance, key, admission signature, instant) are
///     permanent — this row IS the audit entry ADR 0066 clause 4 requires;</description></item>
///   <item><description><b>removing the last administrator cannot re-arm the installer</b> (clause 8). The
///     bootstrap gate is "no <see cref="AdministratorAuthorityEvent.Established"/> event with provenance
///     <see cref="AdministratorProvenance.Bootstrap"/> has EVER been written", which a removal cannot make
///     true again, because removal APPENDS rather than deletes.</description></item>
/// </list>
/// <para>
/// <b>Where it lives.</b> <c>NodeLocalRosterDbContext</c> — the node-exclusive, SQLCipher-encrypted,
/// SC4-recoverable <c>local-node.db</c> that already carries the signed trust roster. Administrator authority
/// belongs beside the roster it is derived from, and that store is present on every node (the installation
/// identity store is not: its ceremony is skipped entirely when the web profile is off).
/// </para>
/// </remarks>
public sealed class AdministratorAuthorityRecord
{
    /// <summary>Monotonic sequence within the log; the chain order and the primary key.</summary>
    public long Sequence { get; set; }

    /// <summary>The team the authority is scoped to (canonical <c>D</c>-form Guid string).</summary>
    public required string TeamId { get; set; }

    /// <summary>The administrator principal (the roster party id).</summary>
    public required string PartyId { get; set; }

    /// <summary>What happened to this party's administrative authority.</summary>
    public AdministratorAuthorityEvent Event { get; set; }

    /// <summary>
    /// Where the authority came from. Meaningful only on
    /// <see cref="AdministratorAuthorityEvent.Established"/>; <see cref="AdministratorProvenance.None"/>
    /// otherwise. ADR 0066 clause 8: <c>recovery</c> is a DISTINCT value from <c>bootstrap</c>, and only
    /// <c>bootstrap</c> is gated on the installer state.
    /// </summary>
    public AdministratorProvenance Provenance { get; set; }

    /// <summary>
    /// base64url of the administrator's Ed25519 public key. Required on an establishing event — ADR 0066
    /// clause 3 keeps <c>MemberPublicKey</c> as evidence of admission, and the always-on mint this migration
    /// removes supplied none.
    /// </summary>
    public string MemberPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// base64url Ed25519 signature of the genesis-anchored admission that admitted this party. Required on an
    /// establishing event. Copied verbatim off the signed roster admission — this log never mints a signature.
    /// </summary>
    public string AdmissionSignature { get; set; } = string.Empty;

    /// <summary>base64url of the key that signed <see cref="AdmissionSignature"/> (the admitter/self).</summary>
    public string AdmittedByPublicKey { get; set; } = string.Empty;

    /// <summary>The party id that admitted this administrator (itself, for a genesis self-admission).</summary>
    public string AdmittedByPartyId { get; set; } = string.Empty;

    /// <summary>True iff the admission backing this establishment is the genesis self-admission.</summary>
    public bool IsGenesisAdmission { get; set; }

    /// <summary>When the event occurred (UTC).</summary>
    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>
    /// When this authority stops being USABLE, or null for no expiry. ADR 0066 clause 7 counts only USABLE
    /// administrators, and an expired credential does not count.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    /// <summary>Free-form stable reason code for the event (never free text from a caller).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The previous event's <see cref="Hash"/>, or <see cref="ZeroHash"/> for the first event.</summary>
    public required string PreviousHash { get; set; }

    /// <summary>SHA-256 over this event's canonical fields plus <see cref="PreviousHash"/> (lowercase hex).</summary>
    public required string Hash { get; set; }

    /// <summary>The chain root — the <see cref="PreviousHash"/> of the first event.</summary>
    public const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Compute the chain hash for an event. Field order is pinned: changing it breaks every existing chain,
    /// which is the point — the chain is what makes a silently edited log detectable.
    /// </summary>
    public static string ComputeHash(AdministratorAuthorityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var canonical = string.Join(
            // ASCII unit separator: it cannot occur in any base64url / id / hex field below, so the
            // concatenation is unambiguous and no field can impersonate a boundary.
            '\u001f',
            record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.TeamId,
            record.PartyId,
            ((int)record.Event).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)record.Provenance).ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.MemberPublicKey,
            record.AdmissionSignature,
            record.AdmittedByPublicKey,
            record.AdmittedByPartyId,
            record.IsGenesisAdmission ? "1" : "0",
            record.OccurredAtUtc.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.ExpiresAtUtc?.ToUnixTimeMilliseconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            record.Reason,
            record.PreviousHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>
    /// Verify the whole chain: sequences contiguous from 1, every <see cref="Hash"/> recomputable, and every
    /// <see cref="PreviousHash"/> equal to its predecessor's hash (the first rooted at <see cref="ZeroHash"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A chain nobody verifies is decoration. Until this existed, <see cref="ComputeHash"/> had exactly ONE
    /// production caller — the writer — so a row inserted by direct SQL with <c>recovery</c> provenance folded
    /// in as a usable administrator undetected, and a deleted row left no trace. The node's other chains
    /// (<c>InstallationAuditIntegrity.Verify</c>, <c>NodeAuditHashChain</c>) are verified; this one now is too.
    /// </para>
    /// <para>
    /// <b>What a broken chain does.</b> Establishment and every removal REFUSE
    /// (<see cref="NodeAdministratorAuthority.BrokenChainCode"/>), and the boot refuses to PROJECT — granting
    /// administrative authority off history that has been edited underneath the node is the worse of the two
    /// failures. Recovery is deliberately NOT blocked: a broken chain is one of the states recovery exists to
    /// get out of, and refusing there would turn the detector into the brick.
    /// </para>
    /// </remarks>
    /// <param name="log">The log, ordered by <see cref="Sequence"/>.</param>
    public static bool VerifyChain(IReadOnlyList<AdministratorAuthorityRecord> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var expectedPrevious = ZeroHash;
        for (var i = 0; i < log.Count; i++)
        {
            var record = log[i];
            if (record.Sequence != i + 1 ||
                !string.Equals(record.PreviousHash, expectedPrevious, StringComparison.Ordinal) ||
                !string.Equals(record.Hash, ComputeHash(record), StringComparison.Ordinal))
            {
                return false;
            }

            expectedPrevious = record.Hash;
        }

        return true;
    }
}

/// <summary>What an <see cref="AdministratorAuthorityRecord"/> asserts about a party's authority.</summary>
public enum AdministratorAuthorityEvent
{
    /// <summary>The party holds administrative authority from this instant (subject to expiry).</summary>
    Established = 0,

    /// <summary>The authority was revoked (<c>members:revoke</c>).</summary>
    Revoked = 1,

    /// <summary>The principal was disabled — authority suspended without removing the party.</summary>
    Disabled = 2,

    /// <summary>The principal was deleted outright (delete-principal).</summary>
    PrincipalDeleted = 3,

    /// <summary>The party's role was changed to a non-administrative one (role change).</summary>
    Demoted = 4,

    /// <summary>
    /// An import / restore / sync projection asserted this party is no longer an administrator. A distinct
    /// event because ADR 0066 clause 7 names import, restore and sync as paths the invariant must cover, and
    /// Microsoft Entra's equivalent invariant is documented as holed exactly there.
    /// </summary>
    ReplacedByProjection = 5,

    /// <summary>
    /// NOT a statement about any party — the marker row that anchors the installer window (ADR 0066
    /// clause 6). It is appended, once, by the first installer attempt that reads an EMPTY log, and its
    /// <see cref="AdministratorAuthorityRecord.OccurredAtUtc"/> is the window's start.
    /// </summary>
    /// <remarks>
    /// The anchor used to be <c>Directory.GetCreationTimeUtc(DataDirectory)</c>, which anyone who can write
    /// the data directory can move with <c>Directory.SetCreationTimeUtc</c>, which predates the feature on
    /// every existing directory, and whose IO-fault fallback re-opened a fresh window on EVERY boot rather
    /// than once. A row in the log the window is about has none of those properties, and it is covered by the
    /// same hash chain. It carries an empty team and party id: the installer seal is installation-wide, so
    /// its window is too. <see cref="AdministratorAuthorityEvent.Established"/> is the only event the usable
    /// fold considers, so this row is invisible to every authority read.
    /// </remarks>
    InstallerWindowOpened = 6,
}

/// <summary>
/// Where an establishing write's authority came from. ADR 0066 clause 8: recovery must be a DISTINCT
/// provenance value from bootstrap, never a re-arm of the installer.
/// </summary>
public enum AdministratorProvenance
{
    /// <summary>Not an establishing event.</summary>
    None = 0,

    /// <summary>
    /// The installer principal (ADR 0066 clause 4) — environment-derived, state-gated, time-bounded, and
    /// usable at most ONCE in the lifetime of an installation.
    /// </summary>
    Bootstrap = 1,

    /// <summary>
    /// The offline recovery path (ADR 0066 clause 8) — performed by the owner of the node's data directory
    /// with the node stopped. Never gated on the installer state, and never able to re-arm it.
    /// </summary>
    Recovery = 2,
}
