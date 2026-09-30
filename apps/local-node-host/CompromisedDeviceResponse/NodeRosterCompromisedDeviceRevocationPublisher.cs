using System.Collections.Concurrent;
using System.Collections.Immutable;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;

internal static class MemberRevocationReasons
{
    internal const string Offboarding = "offboarding";
    internal const string DeviceLost = "device-lost";
}

/// <summary>
/// Ticket 290 — the revocation was refused WHOLE because the party is the last usable administrator of the
/// team (ADR 0066 clause 7 / ticket 211: an install is never left without an Administrator in force).
/// Nothing was written: neither the roster record nor the administrator-authority removal. Hand ownership
/// over first (ticket 211's Administrator handover), then revoke.
/// </summary>
internal sealed class LastUsableAdministratorRevocationRefusedException(string code)
    : InvalidOperationException(
        "The roster revocation was refused: the party is the last usable administrator of the team (" + code
        + "). Hand ownership over first, then revoke.")
{
    /// <summary>The administrator authority's own stable refusal code.</summary>
    internal string Code { get; } = code;
}

internal interface INodeRosterMemberRevocationAuthority
{
    ValueTask<CompromisedDeviceRevocation?> RevokeAsync(
        TenantId tenant,
        string decisionTargetId,
        string revokedPartyId,
        string revokedByPartyId,
        string reason,
        string? correlationId,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default);
}

/// <summary>The roster projection surface the member-revocation authority writes and reads through.</summary>
internal interface IRosterRevocationProjection
{
    /// <summary>
    /// Persists and publishes <paramref name="record"/>. <paramref name="stageWithRecord"/> stages rows on the roster
    /// context before the one save that writes the record, so they commit together or not at all; a fault it throws
    /// leaves the record unwritten (<see cref="RosterCrdtProjection.PublishLocalAsync"/>).
    /// </summary>
    Task PublishLocalAsync(
        RosterRecordCrdtState record,
        CancellationToken cancellationToken,
        Func<NodeLocalRosterDbContext, CancellationToken, ValueTask> stageWithRecord);

    /// <summary>The converged roster records, in list order.</summary>
    IReadOnlyList<RosterRecordCrdtState> Snapshot();
}

internal sealed class RosterRevocationProjection(RosterCrdtProjection inner) : IRosterRevocationProjection
{
    public Task PublishLocalAsync(
        RosterRecordCrdtState record,
        CancellationToken cancellationToken,
        Func<NodeLocalRosterDbContext, CancellationToken, ValueTask> stageWithRecord) =>
        inner.PublishLocalAsync(record, cancellationToken, stageWithRecord);

    public IReadOnlyList<RosterRecordCrdtState> Snapshot() => inner.Snapshot();
}

/// <summary>One decision-bearing authority for every locally-originated roster revocation.</summary>
/// <remarks>
/// T-1000 (DES-0029 kernel-core-ck-6): the revocation's <see cref="AuditEventType.MemberRevoked"/> entry is staged
/// through <paramref name="recorder"/>, bound to the roster save and the admitted decision, so the roster record and
/// its audit commit together or neither does, and the ticket 290 administrator removal commits in that same save. An
/// append or signing fault refuses the revocation and writes no roster record and no removal. The committed entry
/// reaches the trail through the audit outbox's drain; delivery is not durability.
/// </remarks>
internal sealed class NodeRosterMemberRevocationAuthority(
    NodeTeamRoster roster,
    IRosterRevocationProjection projection,
    IOperationSigner signer,
    IOperationVerifier verifier,
    IAuthorizedAuditTrail audit,
    NodeAdministratorAuthority administrators,
    IEnrollmentCompensatingControlRecorder recorder) : INodeRosterMemberRevocationAuthority
{
    private static readonly AuthorizationOperation MembersManage =
        AuthorizationOperation.Parse(TeamRolePermissions.MembersManage);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _revocationGates =
        new(StringComparer.Ordinal);

    public async ValueTask<CompromisedDeviceRevocation?> RevokeAsync(
        TenantId tenant,
        string decisionTargetId,
        string revokedPartyId,
        string revokedByPartyId,
        string reason,
        string? correlationId,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default)
    {
        var reaction = new AuthorizationWriteContext(
            admittedDecision.Request.Principal, tenant, admittedDecision.DecidedAt)
            .Request(MembersManage, "members", decisionTargetId);
        admittedDecision.RequireAllowedReaction(
            MembersManage, tenant, reaction.Target.RecordKind, reaction.Target.RecordId);

        var gate = _revocationGates.GetOrAdd(
            tenant.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = roster.Current;
            if (current.TeamId != Guid.Parse(tenant.Value))
                throw new InvalidOperationException("The member revocation team does not match the live roster.");

            try
            {
                if (current.Contains(revokedPartyId))
                {
                    // Ticket 290 ordering: the administrator guard answers before the roster signs, so a
                    // last-usable-administrator refusal is named ahead of the roster's own floor.
                    RequireRemovalApplied(await administrators.PreviewRemovalUnderDecisionAsync(
                        tenant.Value, revokedPartyId, admittedDecision, cancellationToken).ConfigureAwait(false));
                    var (afterRevocation, signedRevocation) = current.SignRevoke(
                        revokedByPartyId, signer, revokedPartyId, verifier, admittedDecision.DecidedAt, Guid.NewGuid());
                    var state = RosterRecordCrdtState.FromRevocation(signedRevocation);
                    // Ticket 290 and T-1000 (DES-0029 ck-6): the administrator removal, the signed revocation record
                    // and its MemberRevoked audit are one save on the roster context, inside the one BEGIN IMMEDIATE
                    // transaction PublishLocalAsync opens. The last-usable-administrator guard reads the log under
                    // that write lock, and a refusal or a fault in any leg leaves all three unwritten. A refusal is
                    // clause 7's: the whole revocation refuses so the install is never left without an
                    // Administrator in force; the caller hands over first (ticket 211) and retries.
                    await projection.PublishLocalAsync(state, cancellationToken, async (write, token) =>
                    {
                        RequireRemovalApplied(await administrators.StageRemovalUnderDecisionAsync(
                            write, tenant.Value, revokedPartyId, AdministratorAuthorityEvent.Revoked, reason,
                            admittedDecision, token).ConfigureAwait(false));
                        await recorder.Within(new AuthorizedEnrollmentWrite(write, admittedDecision, reason))
                            .RecordMemberRevokedAsync(
                                tenant, state.TeamId, revokedByPartyId, revokedPartyId, correlationId, token)
                            .ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    roster.AdoptSyncedRoster(afterRevocation);
                    return ToEvidence(state);
                }

                // Not a live member, so there is no roster record to write and the removal is the whole act; it
                // commits alone. A replay of a revocation that already committed finds it here, and its audit
                // committed in that revocation's save, so there is nothing to record again.
                RequireRemovalApplied(await administrators.AppendRemovalUnderDecisionAsync(
                    tenant.Value, revokedPartyId, AdministratorAuthorityEvent.Revoked, reason, admittedDecision,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (LastUsableAdministratorRevocationRefusedException refused)
            {
                await AppendRefusalAuditAsync(
                    tenant, reaction, refused.Code, admittedDecision, cancellationToken).ConfigureAwait(false);
                throw;
            }

            var committed = projection.Snapshot().LastOrDefault(record =>
                record.Kind == RosterRecordKind.Revocation
                && string.Equals(record.TeamId, tenant.Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.PartyId, revokedPartyId, StringComparison.Ordinal)
                && string.Equals(record.AdmittedByPartyId, revokedByPartyId, StringComparison.Ordinal)
                && record.ToRevocationOrNull() is not null);
            return committed is null ? null : ToEvidence(committed);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Throws unless the administrator removal applied: <see cref="LastUsableAdministratorRevocationRefusedException"/>
    /// when the party is the team's last usable administrator, <see cref="InvalidOperationException"/> for any other
    /// refusal. Thrown inside the roster save, it rolls the whole revocation back.
    /// </summary>
    private static void RequireRemovalApplied(AdministratorAuthorityResult removal)
    {
        if (removal.Outcome is AdministratorAuthorityOutcome.RefusedLastUsableAdministrator)
            throw new LastUsableAdministratorRevocationRefusedException(removal.Code);
        if (removal.Outcome is not AdministratorAuthorityOutcome.Applied)
        {
            throw new InvalidOperationException(
                "The roster revocation could not write its administrator removal: " + removal.Code);
        }
    }

    /// <summary>The refusal event type; a refusal is never de-duplicated (every attempt is evidence).</summary>
    private static readonly AuditEventType RevocationRefused = new("CapabilityRevocationRefused");

    /// <summary>
    /// One permanent audit row for a revocation the last-usable-administrator guard refused, carrying the
    /// SAME admitted decision the act was gated on — never a second decision (the act decided once).
    /// </summary>
    private async Task AppendRefusalAuditAsync(
        TenantId tenant,
        AuthorizationGateRequest reaction,
        string code,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken)
    {
        var payload = await signer.SignAsync(new AuditPayload(new Dictionary<string, object?>
        {
            ["party_id"] = reaction.Target.RecordId,
            ["reason"] = code,
        }), admittedDecision.DecidedAt, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        await audit.AppendAuthorizedAsync(new AuditRecord(
            Guid.NewGuid(), tenant, RevocationRefused, admittedDecision.DecidedAt, payload,
            ImmutableArray<AttestingSignature>.Empty, Actor: admittedDecision.Request.Principal,
            Target: reaction.Target, Act: reaction.Act), admittedDecision, cancellationToken).ConfigureAwait(false);
    }

    private static CompromisedDeviceRevocation ToEvidence(RosterRecordCrdtState state)
    {
        var revocation = state.ToRevocationOrNull()
            ?? throw new InvalidOperationException("The durable roster revocation is malformed.");
        return new CompromisedDeviceRevocation(
            state.RecordId, state.TeamId, state.PartyId, state.AdmittedByPartyId,
            revocation.Signed.IssuedAt, state.SignatureB64Url);
    }
}

/// <summary>Expresses the device-lost workflow through the shared member-revocation authority.</summary>
internal sealed class NodeRosterCompromisedDeviceRevocationPublisher(
    INodeRosterMemberRevocationAuthority authority) : ICompromisedDeviceRevocationPublisher
{
    /// <inheritdoc />
    public async ValueTask<CompromisedDeviceRevocation> RevokeAsync(
        CompromisedDeviceResponseRequest request,
        string correlationId,
        AuthorizationDecision admittedDecision,
        CancellationToken cancellationToken = default) =>
        await authority.RevokeAsync(
            admittedDecision.Request.Tenant,
            request.RevokedPartyId,
            request.RevokedPartyId,
            request.RevokedByPartyId,
            MemberRevocationReasons.DeviceLost,
            correlationId,
            admittedDecision,
            cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The compromised device is not a live roster member.");
}
