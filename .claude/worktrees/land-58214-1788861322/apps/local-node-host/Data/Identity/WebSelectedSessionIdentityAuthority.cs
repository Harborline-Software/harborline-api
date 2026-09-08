using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.OrgBranding;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// Whether the signed-in account is this installation's designated root, another account, or a
/// question the installation cannot currently answer.
/// </summary>
/// <remarks>
/// Membership is an IDENTITY fact, not a permission: it says who you are, not what you may do. A
/// system that cannot tell the founder from a member has not identified anyone. What a membership
/// permits is <see cref="Member"/>'s and <see cref="Founder"/>'s business nowhere in this type.
/// <para>
/// <see cref="Unresolved"/> exists because the fact is genuinely absent on an installation that has
/// not yet designated a root. Since earlier repository ticket #3373 the bootstrap ceremony writes
/// <c>InstallationIdentityRootDesignationRecord</c> itself, alongside founder-bind and the v1
/// cutover, so a freshly bootstrapped install DOES have a row. <see cref="Unresolved"/> remains
/// reachable and correct for installs bootstrapped before that change: nothing backfills a
/// designation on replay, by design. Collapsing that case into <see cref="Member"/> would label
/// the founder a member, which is the wrong-label failure this whole surface exists to remove.
/// </para>
/// </remarks>
internal enum SelectedSessionMembership
{
    /// <summary>The installation has designated no root, so no membership can be stated.</summary>
    Unresolved = 0,

    /// <summary>A designation exists and names a different account.</summary>
    Member = 1,

    /// <summary>A designation exists and names this account.</summary>
    Founder = 2,
}

/// <summary>One live People-owned label for a tenant-scoped principal.</summary>
/// <param name="PartyId">The Party the binding resolved to — checked against the session's own.</param>
/// <param name="DisplayName">The People-owned display name.</param>
internal sealed record SelectedSessionMemberLabel(
    CanonicalPartyReference PartyId,
    string DisplayName);

/// <summary>
/// Reads the People pillar for the display label of one tenant-scoped principal, and nothing else.
/// </summary>
/// <remarks>
/// Deliberately narrower than <c>IPartyReadModel</c>: the identity plane has no business reading a
/// Party's contact rows, tax id, or date of birth, and a seam that can only return a name cannot be
/// widened by accident. The implementation is tenant-scoped; a miss is a legitimate outcome (see
/// <see cref="IWebSelectedSessionIdentityAuthority"/>), never an error.
/// </remarks>
internal interface ISelectedSessionMemberLabelReader
{
    Task<SelectedSessionMemberLabel?> ReadAsync(
        TenantId tenant,
        PrincipalUserId principal,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Who the browser is signed in as. Identity only — no permission, grant, epoch, or owner version.
/// </summary>
/// <param name="AccountId">The installation account id: the stable key everything here is indexed by.</param>
/// <param name="PartyId">The canonical Party reference. Opaque to the client.</param>
/// <param name="DisplayName">
/// The People-owned display name, or null when the join did not resolve. Null is a legitimate state
/// meaning "this session is real and its label has not resolved" — never a substitute label.
/// </param>
/// <param name="TenantId">The selected tenant.</param>
/// <param name="TenantDisplayName">The tenant's own label, or null when unset or unresolved.</param>
/// <param name="Membership">Founder, member, or unresolved.</param>
/// <param name="AdvisoryExpiresAtUtc">
/// When the session stops working absent further activity. ADVISORY: every selected-session request
/// revalidates the whole fact set before a principal exists, so the server is the only authority on
/// liveness. A client that gates on this value is making the same error as one that caches a
/// permission.
/// </param>
internal sealed record SelectedSessionIdentity(
    string AccountId,
    CanonicalPartyReference PartyId,
    string? DisplayName,
    TenantId TenantId,
    string? TenantDisplayName,
    SelectedSessionMembership Membership,
    DateTimeOffset AdvisoryExpiresAtUtc);

/// <summary>Describes the identity behind one already-revalidated selected session.</summary>
internal interface IWebSelectedSessionIdentityAuthority
{
    /// <summary>
    /// Returns the identity for <paramref name="principal"/>, or null when the presented handle does
    /// not name that principal's live session.
    /// </summary>
    Task<SelectedSessionIdentity?> DescribeAsync(
        string? selectedHandle,
        SelectedSessionRequestPrincipal principal,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Composes one identity view from the request's already-revalidated principal plus two joins whose
/// only failure mode is being less specific.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every field is derived, nothing is re-decided.</b> The listener gate has already reloaded the
/// account, membership, Party, trust, grant pins, epoch, revocation, and TTL before publishing the
/// principal this method is handed. This authority re-reads exactly two things the principal does
/// not carry — the session's expiry and the human-readable labels — and one installation fact (the
/// root designation). None of those reads can widen the answer: a miss removes a field, and the
/// session itself is never made valid, extended, or re-admitted here.
/// </para>
/// <para>
/// <b>It carries identity and stops.</b> The principal's <c>PinnedGrantOwnerVersions</c>,
/// <c>AuthorizationEpoch</c>, <c>MembershipOwnerVersion</c> and <c>MembershipId</c> are internal
/// revalidation machinery. They are not the client's business, they change under it, and putting
/// owner versions on the wire invites a client to cache and compare them.
/// </para>
/// </remarks>
internal sealed class WebSelectedSessionIdentityAuthority : IWebSelectedSessionIdentityAuthority
{
    private readonly WebSelectedSessionStore _sessions;
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly ISelectedSessionMemberLabelReader _labels;
    private readonly IOrgBrandingStore _branding;
    // PUBLIC, not internal: the DI container selects constructors with GetConstructors(),
    // which returns PUBLIC instance constructors only. An internal constructor on a
    // DI-registered type cannot be resolved at all -- the host dies at startup with
    // "a suitable constructor could not be located". The class stays internal sealed, so
    // this widens nothing outside the assembly.

    public WebSelectedSessionIdentityAuthority(
        WebSelectedSessionStore sessions,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        ISelectedSessionMemberLabelReader labels,
        IOrgBrandingStore branding)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _labels = labels ?? throw new ArgumentNullException(nameof(labels));
        _branding = branding ?? throw new ArgumentNullException(nameof(branding));
    }

    public async Task<SelectedSessionIdentity?> DescribeAsync(
        string? selectedHandle,
        SelectedSessionRequestPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (string.IsNullOrWhiteSpace(selectedHandle))
        {
            return null;
        }

        // The handle must name the SAME session the gate revalidated into this principal. Without
        // this the route would describe one caller's principal against another caller's expiry.
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selectedHandle)));
        var session = await _sessions.FindStoredAsync(digest, cancellationToken).ConfigureAwait(false);
        if (session is null ||
            !string.Equals(
                session.SessionCorrelationId,
                principal.SessionCorrelationId,
                StringComparison.Ordinal) ||
            !string.Equals(session.AccountId, principal.AccountId, StringComparison.Ordinal))
        {
            return null;
        }

        var displayName = await ReadDisplayNameAsync(principal, cancellationToken).ConfigureAwait(false);
        var profile = await _branding.GetAsync(principal.TenantId, cancellationToken).ConfigureAwait(false);
        var membership = await ReadMembershipAsync(principal.AccountId, cancellationToken).ConfigureAwait(false);

        // The session is dead at the EARLIER of the two ceilings, so that is the one a client can
        // honestly warn on. (WebSelectedSessionStore refuses on either.)
        var expires = session.IdleExpiresAtUtc <= session.AbsoluteExpiresAtUtc
            ? session.IdleExpiresAtUtc
            : session.AbsoluteExpiresAtUtc;

        return new SelectedSessionIdentity(
            principal.AccountId,
            principal.CanonicalParty,
            displayName,
            principal.TenantId,
            NullIfBlank(profile?.DisplayName),
            membership,
            expires);
    }

    /// <summary>
    /// Resolves the acting member's People label. A missing, archived, duplicate, or wrong-tenant
    /// binding returns null — the session stays valid and the Harborline App renders an honest unresolved
    /// state. It must NEVER fall back to a founder, an operator, or a generic local-user label:
    /// that is the constant-actor failure this surface exists to remove, on the one screen a human
    /// actually reads.
    /// </summary>
    private async Task<string?> ReadDisplayNameAsync(
        SelectedSessionRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        var label = await _labels
            .ReadAsync(principal.TenantId, principal.PrincipalUserId, cancellationToken)
            .ConfigureAwait(false);

        // A binding that now resolves to a DIFFERENT Party than the session was minted against is a
        // changed fact, not a label. Drop it rather than describe the session as someone else.
        return label is null || !label.PartyId.Equals(principal.CanonicalParty)
            ? null
            : NullIfBlank(label.DisplayName);
    }

    /// <summary>
    /// Compares the account against the installation's initial-root designation. An absent row, an
    /// unreadable one, or one naming a different account each answer without inventing a founder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Readability matches the installation's own predicate.</b>
    /// <c>InstallationIdentityCutoverOrchestrator.HasReadableV2CandidateAsync</c> treats a designation
    /// as readable only when it carries BOTH an account id and a <c>VerifiedAtUtc</c>; a designated but
    /// unverified row is not yet authority. Reading membership off the account id alone would answer
    /// founder-or-member where the installation itself answers not-readable — more specifically than
    /// the evidence supports, which is the one direction this surface may never fail in.
    /// </para>
    /// <para>
    /// <b>Deliberately NOT adopted from that predicate:</b> its account-Active, grant-Active and
    /// authority-version checks. Those decide whether the designated root may still ACT. Membership says
    /// who someone IS — a founder whose installation grant was revoked is still the founder — and
    /// folding a liveness or permission read in here would quietly make this a permission surface.
    /// </para>
    /// </remarks>
    private async Task<SelectedSessionMembership> ReadMembershipAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        await using var identity = await _identityFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var designation = await identity.RootDesignations.AsNoTracking()
            .Where(row => row.SingletonKey ==
                InstallationIdentityRootDesignationRecord.SingletonKeyValue)
            .Select(row => new { row.AccountId, row.VerifiedAtUtc })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (designation is null ||
            designation.VerifiedAtUtc is null ||
            string.IsNullOrWhiteSpace(designation.AccountId))
        {
            return SelectedSessionMembership.Unresolved;
        }

        return string.Equals(designation.AccountId, accountId, StringComparison.Ordinal)
            ? SelectedSessionMembership.Founder
            : SelectedSessionMembership.Member;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
