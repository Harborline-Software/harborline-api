using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Roster;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The typed disposition of one administrator-authority write.</summary>
public enum AdministratorAuthorityOutcome
{
    /// <summary>The write was applied.</summary>
    Applied,

    /// <summary>
    /// The installer's state gate was closed: a usable administrator already existed when the transaction
    /// re-evaluated the predicate. Nothing was written. The caller PROJECTS the existing administrator.
    /// </summary>
    AlreadyEstablished,

    /// <summary>
    /// The installer window (ADR 0066 clause 6) has expired. Nothing was written; establishing the first
    /// administrator now requires the offline recovery path.
    /// </summary>
    WindowExpired,

    /// <summary>
    /// The installer has already been used once in this installation's lifetime (ADR 0066 clause 8). Nothing
    /// was written, and no later removal can re-open it — recovery is the only remaining path.
    /// </summary>
    InstallerSealed,

    /// <summary>
    /// Refused: the write would leave the installation with no usable administrator (ADR 0066 clause 7).
    /// Nothing was written. Stable code <see cref="NodeAdministratorAuthority.LastUsableAdministratorCode"/>.
    /// </summary>
    RefusedLastUsableAdministrator,

    /// <summary>Refused: the establishing write carried no member public key or no admission signature.</summary>
    RefusedUnsignedAdmission,

    /// <summary>
    /// Refused: the append-only log's hash chain does not verify, so the state this write would have been
    /// decided against is not trustworthy. Nothing was written. Stable code
    /// <see cref="NodeAdministratorAuthority.BrokenChainCode"/>. Recovery is deliberately exempt.
    /// </summary>
    RefusedBrokenChain,
}

/// <summary>Non-secret result of one administrator-authority write.</summary>
/// <param name="Outcome">The typed disposition.</param>
/// <param name="Code">A stable, log-safe code. Never free text.</param>
/// <param name="Sequence">The appended event's sequence when one was written; otherwise null.</param>
public sealed record AdministratorAuthorityResult(
    AdministratorAuthorityOutcome Outcome,
    string Code,
    long? Sequence)
{
    /// <summary>True iff an event was appended.</summary>
    public bool Applied => Outcome == AdministratorAuthorityOutcome.Applied;
}

/// <summary>
/// The administrator this node will act as — resolved off the SIGNED, genesis-anchored roster admission, never
/// synthesized. Every establishing write takes one of these, so an unsigned establishment is unrepresentable.
/// </summary>
/// <param name="TeamId">The team the authority is scoped to (canonical <c>D</c>-form Guid string).</param>
/// <param name="PartyId">The administrator's roster party id.</param>
/// <param name="MemberPublicKey">base64url of the administrator's Ed25519 public key.</param>
/// <param name="AdmissionSignature">base64url Ed25519 signature of the admission that admitted them.</param>
/// <param name="AdmittedByPublicKey">base64url of the admitting key (itself, for a genesis self-admission).</param>
/// <param name="AdmittedByPartyId">The admitting party id.</param>
/// <param name="IsGenesisAdmission">True iff the backing admission is the genesis self-admission.</param>
public sealed record AdministratorCandidate(
    string TeamId,
    string PartyId,
    string MemberPublicKey,
    string AdmissionSignature,
    string AdmittedByPublicKey,
    string AdmittedByPartyId,
    bool IsGenesisAdmission);

/// <summary>One currently-usable administrator, folded out of the append-only authority log.</summary>
/// <param name="TeamId">The team the authority is scoped to.</param>
/// <param name="PartyId">The administrator's roster party id.</param>
/// <param name="MemberPublicKey">base64url of the administrator's Ed25519 public key.</param>
/// <param name="AdmissionSignature">base64url of the admission signature that rooted this authority.</param>
/// <param name="AdmittedByPublicKey">base64url of the admitting key.</param>
/// <param name="AdmittedByPartyId">The admitting party id.</param>
/// <param name="IsGenesisAdmission">True iff the backing admission is the genesis self-admission.</param>
/// <param name="Provenance">Whether the authority came from the installer or from recovery.</param>
/// <param name="EstablishedAtUtc">When it was established.</param>
/// <param name="ExpiresAtUtc">When it stops being usable, or null.</param>
public sealed record UsableAdministrator(
    string TeamId,
    string PartyId,
    string MemberPublicKey,
    string AdmissionSignature,
    string AdmittedByPublicKey,
    string AdmittedByPartyId,
    bool IsGenesisAdmission,
    AdministratorProvenance Provenance,
    DateTimeOffset EstablishedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// The data layer for administrative authority on this node (ADR 0066 migration step 1). It is the ONLY way
/// administrative authority is created, removed or disabled, and every one of those paths is checked HERE
/// rather than in a caller, a route, or a UI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Clause 5 — the state gate is attacker-facing.</b> The predicate is "does a usable administrator exist",
/// evaluated against the append-only authority log — the same canonical state <see cref="UsableAsync"/> serves
/// to every other reader — and RE-EVALUATED inside the serializable transaction that writes. It is not a
/// cached flag, not a startup boolean, not a sentinel file, and not a route check. The prior art the clause is
/// written against is ScreenConnect CVE-2024-1709, where a setup-wizard guard compared a path string the
/// router canonicalized differently and let a request mint an administrator on a configured server.
/// </para>
/// <para>
/// <b>Everything except the seal is PER-TEAM.</b> The establish gate, the last-usable-administrator
/// invariant and the expiry trial all count administrators <i>of the team being written</i>. Counting across
/// all teams was wrong in both directions: team 2 could never be established at all (team 1's administrator
/// closed the gate), and with administrators in teams T and U, revoking T's last one was APPLIED because the
/// global count was 2 — leaving team T permanently authority-less, which is the exact state clause 7 exists
/// to prevent. The INSTALLER SEAL stays installation-wide, and so does the installer window that anchors it:
/// the installer runs at most once per installation, not once per team, so a second team is established
/// through the recovery path.
/// </para>
/// <para>
/// <b>Clause 7 — the last usable administrator cannot be removed.</b> Every removing or disabling event goes
/// through <see cref="AppendRemovalAsync"/>, which counts the TEAM's usable administrators inside the same
/// transaction and refuses with <see cref="LastUsableAdministratorCode"/> when the count would reach zero.
/// "Usable"
/// excludes revoked, disabled, deleted, demoted, projection-replaced, and EXPIRED. The event enum enumerates
/// the paths clause 7 names — revoke, expire, disable, delete-principal, role change, import/restore/sync — so
/// a new path cannot be added without choosing one of them and therefore inheriting the check.
/// </para>
/// <para>
/// <b>Clause 8 — recovery is not a re-arm.</b> <see cref="EstablishByInstallerAsync"/> additionally refuses
/// whenever a <see cref="AdministratorProvenance.Bootstrap"/> establishment has EVER been appended, whether or
/// not that administrator is still usable. Because the log is append-only, deleting the last administrator
/// cannot make that condition false again. Offline recovery is a separate factory-confined mutation with a
/// separate provenance value that never consults the installer seal — and it never clears it either.
/// </para>
/// </remarks>
public sealed class NodeAdministratorAuthority
{
    /// <summary>The stable refusal code for the last-usable-administrator invariant (ADR 0066 clause 7).</summary>
    public const string LastUsableAdministratorCode = "administrator.last_usable_administrator_refused";

    /// <summary>Refusal code for an establishing write with no key or no admission signature.</summary>
    public const string UnsignedAdmissionCode = "administrator.unsigned_admission_refused";

    /// <summary>Code recorded when the installer establishes the first administrator.</summary>
    public const string BootstrapEstablishedCode = "administrator.established_by_installer";

    /// <summary>Code recorded when offline recovery establishes an administrator.</summary>
    public const string RecoveryEstablishedCode = "administrator.established_by_recovery";

    /// <summary>Code returned when the installer state gate was closed by a concurrent or earlier start.</summary>
    public const string AlreadyEstablishedCode = "administrator.already_established";

    /// <summary>Code returned when the installer window has expired (ADR 0066 clause 6).</summary>
    public const string WindowExpiredCode = "administrator.installer_window_expired";

    /// <summary>Reason recorded on the marker row that anchors the installer window.</summary>
    public const string InstallerWindowOpenedCode = "administrator.installer_window_opened";

    /// <summary>Code returned when the installer has already been used once (ADR 0066 clause 8).</summary>
    public const string InstallerSealedCode = "administrator.installer_sealed";

    /// <summary>Refusal code for a write decided against a log whose hash chain does not verify.</summary>
    public const string BrokenChainCode = "administrator.authority_chain_broken";

    /// <summary>Stable refusal code when an offline recovery scope no longer owns the node run lock.</summary>
    public const string RecoveryScopeUnavailableCode = "administrator.recovery_scope_unavailable";

    private const int BusyRetryCount = 8;

    private readonly IDbContextFactory<NodeLocalRosterDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly AuthorizationGate? _gate;

    internal DateTimeOffset CurrentInstant => _timeProvider.GetUtcNow();

    /// <summary>Constructs the authority over the node-exclusive roster store and a clock.</summary>
    /// <param name="contextFactory">Factory for the SQLCipher-keyed roster context.</param>
    /// <param name="timeProvider">The clock expiry and event instants are read from.</param>
    public NodeAdministratorAuthority(
        IDbContextFactory<NodeLocalRosterDbContext> contextFactory,
        TimeProvider timeProvider,
        AuthorizationGate gate)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    private NodeAdministratorAuthority(
        IDbContextFactory<NodeLocalRosterDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// The currently USABLE administrators, folded out of the append-only log. This is the canonical read the
    /// installer's state gate uses, and it is the same read the boot projection uses — clause 5's "the same
    /// canonical state every other read uses" is satisfied by there being exactly one such read.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<IReadOnlyList<UsableAdministrator>> UsableAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var log = await context.AdministratorAuthority.AsNoTracking()
            .OrderBy(record => record.Sequence)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return FoldUsable(log, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// True iff the installer principal has ever established an administrator on this installation. Append-only
    /// history, so this never goes back to false — that is precisely what stops a deleted administrator from
    /// re-arming bootstrap (ADR 0066 clause 8).
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<bool> InstallerHasRunAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await context.AdministratorAuthority.AsNoTracking()
            .AnyAsync(
                record => record.Event == AdministratorAuthorityEvent.Established &&
                          record.Provenance == AdministratorProvenance.Bootstrap,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// THE INSTALLER PRINCIPAL (ADR 0066 clause 4). Establishes the first administrator, once, when the
    /// installation has none — and does nothing on every later boot.
    /// </summary>
    /// <param name="candidate">The administrator to establish, carrying its signed admission.</param>
    /// <param name="installerWindow">
    /// How long the installer window stays open (ADR 0066 clause 6), or null / non-positive for no bound. It
    /// is a DURATION, not a deadline: the anchor is the log's own <c>InstallerWindowOpened</c> marker, written
    /// here inside the same transaction the first time an installer attempt reads an empty log, so no caller
    /// can supply a deadline derived from an attacker-writable filesystem timestamp.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task<AdministratorAuthorityResult> EstablishByInstallerAsync(
        AdministratorCandidate candidate,
        TimeSpan? installerWindow,
        CancellationToken cancellationToken = default)
        => EstablishAsync(
            candidate,
            AdministratorProvenance.Bootstrap,
            installerWindow,
            expiresAtUtc: null,
            cancellationToken);

    /// <summary>
    /// Record — ONCE, for the lifetime of the installation — that this node has observed itself with no
    /// usable administrator. That instant is the installer window's anchor (ADR 0066 clause 6).
    /// </summary>
    /// <remarks>
    /// The boot calls this whenever a team it is materializing has no usable administrator, WHETHER OR NOT
    /// it can go on to establish one. Anchoring inside the establishing write alone would have meant the
    /// window only ever started on the boot that closed it: a node that sits for a month unable to establish
    /// (no roster yet, say) would have opened a fresh 24-hour window on the day it finally could. The anchor
    /// is a fact about the log, so it survives restarts, ignores the filesystem, and cannot be moved.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task<AdministratorAuthorityResult> ObserveEmptyLogAsync(
        CancellationToken cancellationToken = default)
        => WithRetryAsync(
            (context, now) => TryObserveEmptyLogAsync(context, now, cancellationToken),
            cancellationToken);

    /// <summary>
    /// The only construction boundary for ungated offline recovery. The returned scope owns the same
    /// <c>node.lock</c> a running host owns and serializes disposal against the recovery mutation.
    /// </summary>
    internal static class NodeAdministratorOfflineRecoveryFactory
    {
        internal static RecoveryScope? TryAcquire(
            string dataDirectory,
            out NodeRunLockFailure failure,
            bool createDirectory = false) =>
            RecoveryScope.TryAcquire(dataDirectory, out failure, createDirectory);

        internal sealed class RecoveryScope : IDisposable
        {
            private readonly FileStream _runLock;
            private readonly SemaphoreSlim _lifetime = new(1, 1);
            private bool _disposed;

            private RecoveryScope(FileStream runLock) => _runLock = runLock;

            internal static RecoveryScope? TryAcquire(
                string dataDirectory,
                out NodeRunLockFailure failure,
                bool createDirectory)
            {
                var runLock = NodeRunLock.TryAcquire(dataDirectory, out failure, createDirectory);
                return runLock is null ? null : new RecoveryScope(runLock);
            }

            internal async Task<AdministratorAuthorityResult> RecoverAsync(
                IDbContextFactory<NodeLocalRosterDbContext> contextFactory,
                TimeProvider timeProvider,
                AdministratorCandidate candidate,
                CancellationToken cancellationToken = default)
            {
                await _lifetime.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    RequireRunLock();
                    return await RecoveryAuthority.ExecuteAsync(
                        contextFactory, timeProvider, candidate, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _lifetime.Release();
                }
            }

            private void RequireRunLock()
            {
                if (_disposed || _runLock.SafeFileHandle.IsClosed || _runLock.SafeFileHandle.IsInvalid)
                    throw new InvalidOperationException(RecoveryScopeUnavailableCode);
            }

            public void Dispose()
            {
                _lifetime.Wait();
                try
                {
                    if (_disposed)
                        return;
                    _disposed = true;
                    _runLock.Dispose();
                }
                finally
                {
                    _lifetime.Release();
                }
            }
        }

        private sealed class RecoveryAuthority
        {
            private readonly NodeAdministratorAuthority _authority;

            private RecoveryAuthority(
                IDbContextFactory<NodeLocalRosterDbContext> contextFactory,
                TimeProvider timeProvider) =>
                _authority = new NodeAdministratorAuthority(contextFactory, timeProvider);

            internal static Task<AdministratorAuthorityResult> ExecuteAsync(
                IDbContextFactory<NodeLocalRosterDbContext> contextFactory,
                TimeProvider timeProvider,
                AdministratorCandidate candidate,
                CancellationToken cancellationToken)
            {
                var authority = new RecoveryAuthority(contextFactory, timeProvider);
                return authority.EstablishByRecoveryAsync(candidate, cancellationToken);
            }

            private Task<AdministratorAuthorityResult> EstablishByRecoveryAsync(
                AdministratorCandidate candidate,
                CancellationToken cancellationToken) =>
                _authority.EstablishAsync(
                    candidate,
                    AdministratorProvenance.Recovery,
                    installerWindow: null,
                    expiresAtUtc: null,
                    cancellationToken);
        }
    }

    /// <summary>
    /// True iff the append-only log's hash chain verifies. The boot projection consults this BEFORE granting
    /// anything: projecting administrative authority off history that has been edited underneath the node is
    /// worse than starting with none, and recovery is the way back.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<bool> ChainVerifiesAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return AdministratorAuthorityRecord.VerifyChain(
            await ReadLogAsync(context, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Remove or disable a party's administrative authority. THE ONLY path that does so, and the one place the
    /// last-usable-administrator invariant is enforced (ADR 0066 clause 7).
    /// </summary>
    /// <param name="teamId">The team the authority is scoped to.</param>
    /// <param name="partyId">The party losing administrative authority.</param>
    /// <param name="removal">Which removal path this is — revoke, disable, delete, demote, or projection.</param>
    /// <param name="reason">A stable, log-safe reason code.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<AdministratorAuthorityResult> AppendRemovalAsync(
        string teamId,
        string partyId,
        AdministratorAuthorityEvent removal,
        string reason,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        await AuthorizeAdministrationAsync(teamId, partyId, authority, cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);
        if (removal is AdministratorAuthorityEvent.Established
            or AdministratorAuthorityEvent.InstallerWindowOpened)
        {
            throw new ArgumentOutOfRangeException(
                nameof(removal),
                "Established is not a removal (use EstablishByInstallerAsync or EstablishByRecoveryAsync), " +
                "and InstallerWindowOpened is a window marker rather than a statement about a party.");
        }

        return await WithRetryAsync(
            (context, now) => TryAppendRemovalAsync(
                context, teamId, partyId, removal, reason, now, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The removal leg of a roster revocation the caller has ALREADY decided (ticket 290). The signed roster
    /// revocation and this removal are ONE unit of work: the caller made the <c>members:manage</c> decision
    /// once, required it once, and carries that same decision object here rather than making the gate decide a
    /// second time on a second target shape.
    /// </summary>
    /// <param name="teamId">The team the authority is scoped to; must be the decision's own tenant.</param>
    /// <param name="partyId">The party losing administrative authority.</param>
    /// <param name="removal">Which removal path this is.</param>
    /// <param name="reason">A stable, log-safe reason code.</param>
    /// <param name="admittedDecision">The one decision the roster revocation was admitted under.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    internal Task<AdministratorAuthorityResult> AppendRemovalUnderDecisionAsync(
        string teamId,
        string partyId,
        AdministratorAuthorityEvent removal,
        string reason,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admittedDecision);
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);
        if (!string.Equals(teamId, admittedDecision.Request.Tenant.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The administrator team does not match the admitted decision's tenant.", nameof(teamId));
        }

        admittedDecision.RequireAllowed();
        return WithRetryAsync(
            (context, now) => TryAppendRemovalAsync(
                context, teamId, partyId, removal, reason, now, cancellationToken),
            cancellationToken,
            admittedInstant: admittedDecision.DecidedAt);
    }

    /// <summary>
    /// The (team, party) pairs whose LATEST administrator-authority event is an establishment — the log fold
    /// WITHOUT the clock (ticket 290). The roster's reconcile runs inside whatever act published the roster
    /// record, and ticket 216 requires that an act read the host clock exactly once, at the decision; an
    /// expiry-aware <see cref="UsableAsync"/> there would be a second read. Expiry is not needed for what the
    /// reconcile asks — an expired establishment is already not usable, and the removal it would append is
    /// what stops this fold from selecting the party again.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal async Task<IReadOnlyList<(string TeamId, string PartyId)>> EstablishedPartiesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var log = await ReadLogAsync(context, cancellationToken).ConfigureAwait(false);
        var latest = new Dictionary<(string Team, string Party), AdministratorAuthorityEvent>();
        foreach (var record in log)
        {
            // A window marker is not a statement about a party (AppendRemovalAsync refuses it as a removal
            // for the same reason), so it must not shadow the establishment it shares a key with.
            if (record.Event == AdministratorAuthorityEvent.InstallerWindowOpened) continue;
            latest[(record.TeamId, record.PartyId)] = record.Event;
        }

        return latest
            .Where(entry => entry.Value == AdministratorAuthorityEvent.Established)
            .Select(entry => (entry.Key.Team, entry.Key.Party))
            .ToArray();
    }

    /// <summary>
    /// The removal leg of a revocation this node CONVERGED from a peer (ticket 290). There is no local
    /// principal to decide: the authority is the peer's signed revocation, already re-validated to genesis by
    /// <see cref="MemberRoster.FromSyncedRecords"/> before the live roster adopted it. That is exactly what
    /// <see cref="AdministratorAuthorityEvent.ReplacedByProjection"/> names — ADR 0066 clause 7 lists sync as
    /// a path the invariant must cover, and the invariant is still enforced inside the transaction.
    /// </summary>
    /// <remarks>
    /// Re-running this is harmless and is the REPAIR for a locally-originated revocation whose removal leg
    /// failed after the roster leg committed: the two writes do not share a transaction, so the roster is
    /// written first and the next fold appends whatever removal is still missing.
    /// </remarks>
    /// <param name="teamId">The team the authority is scoped to.</param>
    /// <param name="partyId">The party the converged roster no longer carries.</param>
    /// <param name="reason">A stable, log-safe reason code.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    internal Task<AdministratorAuthorityResult> AppendConvergedRevocationRemovalAsync(
        string teamId,
        string partyId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);
        return WithRetryAsync(
            (context, now) => TryAppendRemovalAsync(
                context, teamId, partyId, AdministratorAuthorityEvent.ReplacedByProjection, reason, now,
                cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Set (or clear) an administrator's expiry. Tightening an expiry is a removal path in disguise — ADR 0066
    /// clause 7 names "expire" explicitly — so it is refused when the resulting state would leave no usable
    /// administrator at the moment it takes effect.
    /// </summary>
    /// <param name="teamId">The team the authority is scoped to.</param>
    /// <param name="partyId">The administrator whose expiry is changing.</param>
    /// <param name="expiresAtUtc">The new expiry, or null to clear it.</param>
    /// <param name="reason">A stable, log-safe reason code.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<AdministratorAuthorityResult> SetExpiryAsync(
        string teamId,
        string partyId,
        DateTimeOffset? expiresAtUtc,
        string reason,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        await AuthorizeAdministrationAsync(teamId, partyId, authority, cancellationToken).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);

        return await WithRetryAsync(
            (context, now) => TrySetExpiryAsync(
                context, teamId, partyId, expiresAtUtc, reason, now, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AuthorizeAdministrationAsync(
        string teamId,
        string partyId,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(teamId, authority.Tenant.Value, StringComparison.Ordinal))
            throw new ArgumentException("The administrator team does not match the write authority.", nameof(teamId));
        var gate = _gate ?? throw new InvalidOperationException(
            "Ordinary administrator mutations require the authorization gate.");
        var decision = await gate.DecideAsync(
            authority.Request(
                AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
                "members",
                InstallationAuditIntegrity.Hash("administrator-member/v1", teamId, partyId)),
            cancellationToken).ConfigureAwait(false);
        decision.RequireAllowed();
    }

    // ── the transactional core ───────────────────────────────────────────────────────────────────────────

    private Task<AdministratorAuthorityResult> EstablishAsync(
        AdministratorCandidate candidate,
        AdministratorProvenance provenance,
        TimeSpan? installerWindow,
        DateTimeOffset? expiresAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // An establishing write with no key or no signature is exactly what the always-on mint did. Refuse it
        // here, at the data layer, so no caller can reintroduce it.
        if (string.IsNullOrWhiteSpace(candidate.MemberPublicKey) ||
            string.IsNullOrWhiteSpace(candidate.AdmissionSignature))
        {
            return Task.FromResult(new AdministratorAuthorityResult(
                AdministratorAuthorityOutcome.RefusedUnsignedAdmission, UnsignedAdmissionCode, null));
        }

        return WithRetryAsync(
            (context, now) => TryEstablishAsync(
                context, candidate, provenance, installerWindow, expiresAtUtc, now, cancellationToken),
            cancellationToken);
    }

    private static async Task<AdministratorAuthorityResult> TryEstablishAsync(
        NodeLocalRosterDbContext context,
        AdministratorCandidate candidate,
        AdministratorProvenance provenance,
        TimeSpan? installerWindow,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        var log = await ReadLogAsync(context, cancellationToken).ConfigureAwait(false);

        // ── THE STATE GATE, re-evaluated INSIDE the writing transaction (ADR 0066 clause 5). ─────────────
        // Two concurrent starts both read "no administrator" outside a transaction; only one can read it
        // inside a serializable one, so only one establishes and the loser is told AlreadyEstablished.
        // Keyed on the CANDIDATE'S TEAM: a global gate makes a second team unestablishable by any path.
        if (UsableIn(log, now, candidate.TeamId).Count > 0)
        {
            return new AdministratorAuthorityResult(
                AdministratorAuthorityOutcome.AlreadyEstablished, AlreadyEstablishedCode, null);
        }

        if (provenance == AdministratorProvenance.Bootstrap)
        {
            // A log that does not verify is not a state the installer may decide against. Recovery is
            // exempt (below): it is the way out of exactly this condition.
            if (!AdministratorAuthorityRecord.VerifyChain(log))
            {
                return new AdministratorAuthorityResult(
                    AdministratorAuthorityOutcome.RefusedBrokenChain, BrokenChainCode, null);
            }

            // ── THE SEAL (ADR 0066 clause 8). The installer runs at most once per installation, EVER. The
            // log is append-only, so removing the last administrator appends a removal rather than deleting
            // the establishment — and this condition stays true. That is what stops "the installer exists
            // while no administrator exists" from becoming a privilege-escalation path. Deliberately global:
            // "once per installation", not once per team.
            if (log.Any(record =>
                    record.Event == AdministratorAuthorityEvent.Established &&
                    record.Provenance == AdministratorProvenance.Bootstrap))
            {
                return new AdministratorAuthorityResult(
                    AdministratorAuthorityOutcome.InstallerSealed, InstallerSealedCode, null);
            }

            // ── THE WINDOW (ADR 0066 clause 6). A node left indefinitely in "no administrator yet" is a
            // standing administrator-creation opportunity, so the installer state expires — anchored on the
            // log's OWN first observation of itself as empty, appended here, in this transaction, once.
            var anchor = EnsureWindowAnchor(context, ref log, now);

            if (installerWindow is { } window && window > TimeSpan.Zero &&
                now >= anchor.OccurredAtUtc + window)
            {
                // Commit anyway: if the marker was just written it must survive the refusal, or the next
                // boot re-opens a fresh window — the fail-open-forever bug the filesystem anchor had.
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new AdministratorAuthorityResult(
                    AdministratorAuthorityOutcome.WindowExpired, WindowExpiredCode, null);
            }
        }

        var appended = Append(context, log, new AdministratorAuthorityRecord
        {
            TeamId = candidate.TeamId,
            PartyId = candidate.PartyId,
            Event = AdministratorAuthorityEvent.Established,
            Provenance = provenance,
            MemberPublicKey = candidate.MemberPublicKey,
            AdmissionSignature = candidate.AdmissionSignature,
            AdmittedByPublicKey = candidate.AdmittedByPublicKey,
            AdmittedByPartyId = candidate.AdmittedByPartyId,
            IsGenesisAdmission = candidate.IsGenesisAdmission,
            OccurredAtUtc = now,
            ExpiresAtUtc = expiresAtUtc,
            Reason = provenance == AdministratorProvenance.Bootstrap
                ? BootstrapEstablishedCode
                : RecoveryEstablishedCode,
            PreviousHash = string.Empty,
            Hash = string.Empty,
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AdministratorAuthorityResult(
            AdministratorAuthorityOutcome.Applied, appended.Reason, appended.Sequence);
    }

    private static async Task<AdministratorAuthorityResult> TryAppendRemovalAsync(
        NodeLocalRosterDbContext context,
        string teamId,
        string partyId,
        AdministratorAuthorityEvent removal,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        var log = await ReadLogAsync(context, cancellationToken).ConfigureAwait(false);
        if (!AdministratorAuthorityRecord.VerifyChain(log))
        {
            return new AdministratorAuthorityResult(
                AdministratorAuthorityOutcome.RefusedBrokenChain, BrokenChainCode, null);
        }

        // Counted WITHIN THE TEAM. A global count says "two administrators exist" while the team losing its
        // only one goes to zero — the invariant reading as if it held while the team is bricked.
        var usable = UsableIn(log, now, teamId);

        // Removing a party that is not a usable administrator changes nothing and is not a brick risk, so it
        // is applied without the invariant check (it keeps the log honest about attempted removals).
        var targetIsUsable = usable.Any(administrator =>
            string.Equals(administrator.PartyId, partyId, StringComparison.Ordinal));

        // ── THE INVARIANT (ADR 0066 clause 7), enforced at the data layer for EVERY removal path. ────────
        if (targetIsUsable && usable.Count <= 1)
        {
            return new AdministratorAuthorityResult(
                AdministratorAuthorityOutcome.RefusedLastUsableAdministrator,
                LastUsableAdministratorCode,
                null);
        }

        var appended = Append(context, log, new AdministratorAuthorityRecord
        {
            TeamId = teamId,
            PartyId = partyId,
            Event = removal,
            Provenance = AdministratorProvenance.None,
            OccurredAtUtc = now,
            Reason = reason ?? string.Empty,
            PreviousHash = string.Empty,
            Hash = string.Empty,
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AdministratorAuthorityResult(
            AdministratorAuthorityOutcome.Applied, appended.Reason, appended.Sequence);
    }

    private static async Task<AdministratorAuthorityResult> TrySetExpiryAsync(
        NodeLocalRosterDbContext context,
        string teamId,
        string partyId,
        DateTimeOffset? expiresAtUtc,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        var log = await ReadLogAsync(context, cancellationToken).ConfigureAwait(false);
        if (!AdministratorAuthorityRecord.VerifyChain(log))
        {
            return new AdministratorAuthorityResult(
                AdministratorAuthorityOutcome.RefusedBrokenChain, BrokenChainCode, null);
        }

        var current = UsableIn(log, now, teamId)
            .FirstOrDefault(administrator =>
                string.Equals(administrator.PartyId, partyId, StringComparison.Ordinal));
        if (current is null)
        {
            // Not a usable administrator: there is no authority to date-bound.
            return new AdministratorAuthorityResult(
                AdministratorAuthorityOutcome.Applied, "administrator.expiry_noop", null);
        }

        // Re-establishing the same party with a new expiry is the expiry write. Fold the log AS IF it were
        // applied and refuse when that leaves nobody usable — "expire" is a removal path (ADR 0066 clause 7).
        var candidateEvent = new AdministratorAuthorityRecord
        {
            // Sequenced PAST the current tip before the trial fold below. The fold takes the LATEST event
            // per party, so a candidate left at the default 0 would sort BEFORE the establishment it is
            // meant to supersede and the trial would report the pre-expiry state — i.e. the check would
            // pass exactly when it should refuse. Append() re-derives this from the tip it reads.
            Sequence = (log.Length == 0 ? 0 : log[^1].Sequence) + 1,
            TeamId = teamId,
            PartyId = partyId,
            Event = AdministratorAuthorityEvent.Established,
            Provenance = current.Provenance,
            MemberPublicKey = current.MemberPublicKey,
            AdmissionSignature = current.AdmissionSignature,
            AdmittedByPublicKey = current.AdmittedByPublicKey,
            AdmittedByPartyId = current.AdmittedByPartyId,
            IsGenesisAdmission = current.IsGenesisAdmission,
            OccurredAtUtc = now,
            ExpiresAtUtc = expiresAtUtc,
            Reason = reason ?? string.Empty,
            PreviousHash = string.Empty,
            Hash = string.Empty,
        };
        // Folded at EVERY instant that matters, not just at `now`. SetExpiry(last, now + 1s) folds to one
        // usable administrator at `now` and to ZERO a second later, so a fold at `now` alone accepts exactly
        // the write that empties the team — with no event marking the moment it happens. The instants that
        // matter are `now` and each expiry the trial sets or leaves standing; if the team has nobody whose
        // authority outlasts all of them, the expiry is a removal of the last administrator on a timer.
        var trial = log.Append(candidateEvent).ToArray();
        var instants = UsableIn(trial, now, teamId)
            .Where(administrator => administrator.ExpiresAtUtc is not null)
            .Select(administrator => administrator.ExpiresAtUtc!.Value)
            .Append(now)
            .Distinct();
        if (instants.Any(instant => UsableIn(trial, instant, teamId).Count == 0))
        {
            return new AdministratorAuthorityResult(
                AdministratorAuthorityOutcome.RefusedLastUsableAdministrator,
                LastUsableAdministratorCode,
                null);
        }

        var appended = Append(context, log, candidateEvent);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AdministratorAuthorityResult(
            AdministratorAuthorityOutcome.Applied, appended.Reason, appended.Sequence);
    }

    private static async Task<AdministratorAuthorityResult> TryObserveEmptyLogAsync(
        NodeLocalRosterDbContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        var log = await ReadLogAsync(context, cancellationToken).ConfigureAwait(false);
        var anchor = EnsureWindowAnchor(context, ref log, now);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AdministratorAuthorityResult(
            AdministratorAuthorityOutcome.Applied, InstallerWindowOpenedCode, anchor.Sequence);
    }

    /// <summary>
    /// Return the installer window's anchor, appending it when the log does not carry one yet. Idempotent
    /// and transaction-local: the FIRST caller inside a serializable transaction writes it, every later one
    /// reads it, and nothing ever moves it.
    /// </summary>
    private static AdministratorAuthorityRecord EnsureWindowAnchor(
        NodeLocalRosterDbContext context,
        ref AdministratorAuthorityRecord[] log,
        DateTimeOffset now)
    {
        var anchor = log.FirstOrDefault(record =>
            record.Event == AdministratorAuthorityEvent.InstallerWindowOpened);
        if (anchor is not null)
        {
            return anchor;
        }

        anchor = Append(context, log, new AdministratorAuthorityRecord
        {
            TeamId = string.Empty,
            PartyId = string.Empty,
            Event = AdministratorAuthorityEvent.InstallerWindowOpened,
            Provenance = AdministratorProvenance.None,
            OccurredAtUtc = now,
            Reason = InstallerWindowOpenedCode,
            PreviousHash = string.Empty,
            Hash = string.Empty,
        });
        log = [.. log, anchor];
        return anchor;
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────────

    private static Task<AdministratorAuthorityRecord[]> ReadLogAsync(
        NodeLocalRosterDbContext context,
        CancellationToken cancellationToken)
        => context.AdministratorAuthority.AsNoTracking()
            .OrderBy(record => record.Sequence)
            .ToArrayAsync(cancellationToken);

    private static AdministratorAuthorityRecord Append(
        NodeLocalRosterDbContext context,
        IReadOnlyList<AdministratorAuthorityRecord> log,
        AdministratorAuthorityRecord record)
    {
        var tip = log.Count == 0 ? null : log[^1];
        record.Sequence = (tip?.Sequence ?? 0) + 1;
        record.PreviousHash = tip?.Hash ?? AdministratorAuthorityRecord.ZeroHash;
        record.Hash = AdministratorAuthorityRecord.ComputeHash(record);
        context.AdministratorAuthority.Add(record);
        return record;
    }

    /// <summary>
    /// The usable administrators OF ONE TENANT at <paramref name="now"/>. Every gate and every invariant in
    /// this class counts through here: authority is tenant-scoped, so counting across tenants either blocks
    /// a tenant that has none or permits one to be emptied because a DIFFERENT tenant still has an
    /// administrator.
    /// </summary>
    /// <remarks>
    /// <b>Vocabulary.</b> This store spells the tenant axis <c>TeamId</c> / <c>team_id</c>, and
    /// <c>ActiveTeamTenantContext.ProjectTenantId</c> shows the two are one axis under two names (a
    /// <c>TenantId</c> is the team guid stringified). The ratified corpus vocabulary is <i>tenant</i>, so
    /// what this change ADDS says tenant; renaming what already exists is its own change.
    /// </remarks>
    private static IReadOnlyList<UsableAdministrator> UsableIn(
        IReadOnlyList<AdministratorAuthorityRecord> log,
        DateTimeOffset now,
        string tenantId) =>
        FoldUsable(log, now)
            .Where(administrator => string.Equals(administrator.TeamId, tenantId, StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// Fold the append-only log into the set of USABLE administrators at <paramref name="now"/>. The latest
    /// event per (team, party) decides; "usable" excludes every removal event and every expired authority.
    /// Exposed internally so a test can fold a synthetic log without a database.
    /// </summary>
    internal static IReadOnlyList<UsableAdministrator> FoldUsable(
        IReadOnlyList<AdministratorAuthorityRecord> log,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(log);
        var latest = new Dictionary<(string Team, string Party), AdministratorAuthorityRecord>();
        foreach (var record in log.OrderBy(record => record.Sequence))
        {
            latest[(record.TeamId, record.PartyId)] = record;
        }

        return latest.Values
            .Where(record => record.Event == AdministratorAuthorityEvent.Established)
            .Where(record => record.ExpiresAtUtc is null || record.ExpiresAtUtc > now)
            .OrderBy(record => record.Sequence)
            .Select(record => new UsableAdministrator(
                record.TeamId,
                record.PartyId,
                record.MemberPublicKey,
                record.AdmissionSignature,
                record.AdmittedByPublicKey,
                record.AdmittedByPartyId,
                record.IsGenesisAdmission,
                record.Provenance,
                record.OccurredAtUtc,
                record.ExpiresAtUtc))
            .ToArray();
    }

    private async Task<AdministratorAuthorityResult> WithRetryAsync(
        Func<NodeLocalRosterDbContext, DateTimeOffset, Task<AdministratorAuthorityResult>> operation,
        CancellationToken cancellationToken,
        DateTimeOffset? admittedInstant = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                // ticket 216: inside an admitted act the instant is the DECISION's, read once by the caller —
                // a write that re-read the host clock here would be a second read of the act's clock
                // (KernelClockIntegrationTests measures exactly that on the production composition).
                return await operation(
                    context, admittedInstant ?? _timeProvider.GetUtcNow()).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableContention(exception) && attempt < BusyRetryCount)
            {
                // SQLite serializes writers; a concurrent establisher shows up as BUSY/LOCKED, not as a
                // second winner. Back off and let the loser re-read — it will find AlreadyEstablished.
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// BUSY (5), LOCKED (6) and CONSTRAINT (19). The first two are the ordinary shape of a lost race. The
    /// third is the shape it takes when the loser gets as far as <c>SaveChanges</c>: the sequence it derived
    /// from the tip it read is already taken, and SQLite reports a primary-key violation. That is still a
    /// lost race, and it was previously FATAL — it crashed host startup where <c>AlreadyEstablished</c> was
    /// intended. Retrying re-reads the log inside a fresh transaction, which is exactly the "re-read and
    /// return the already-established result" the caller expects. Bounded, so a genuine constraint defect
    /// still surfaces rather than looping.
    /// </summary>
    private static bool IsRetryableContention(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: 5 or 6 or 19 } ||
        exception.InnerException is SqliteException { SqliteErrorCode: 5 or 6 or 19 };
}
