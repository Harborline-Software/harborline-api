using System.Collections.Concurrent;
using System.Collections.Immutable;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Audit.Payloads;
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

internal interface IRosterRevocationProjection
{
    Task PublishLocalAsync(RosterRecordCrdtState record, CancellationToken cancellationToken);
    IReadOnlyList<RosterRecordCrdtState> Snapshot();
}

internal sealed class RosterRevocationProjection(RosterCrdtProjection inner) : IRosterRevocationProjection
{
    public Task PublishLocalAsync(RosterRecordCrdtState record, CancellationToken cancellationToken) =>
        inner.PublishLocalAsync(record, cancellationToken);

    public IReadOnlyList<RosterRecordCrdtState> Snapshot() => inner.Snapshot();
}

/// <summary>One decision-bearing authority for every locally-originated roster revocation.</summary>
internal sealed class NodeRosterMemberRevocationAuthority(
    NodeTeamRoster roster,
    IRosterRevocationProjection projection,
    IOperationSigner signer,
    IOperationVerifier verifier,
    IAuthorizedAuditTrail audit,
    NodeAdministratorAuthority administrators) : INodeRosterMemberRevocationAuthority
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

            // Ticket 290 — the removal leg runs FIRST, under the SAME decision, BEFORE the roster leg
            // publishes anything. The two writes do not share a transaction (the roster record is a CRDT
            // publish, the removal a serializable append on the roster store), so ordering is the only unit
            // of work available: a refusal here leaves BOTH structures untouched, where the reverse order
            // committed the roster revocation and then threw, and the converged fold retried the refusal
            // forever. The one refusal that can happen is clause 7's — this party is the team's last usable
            // administrator — and per ticket 211 the whole revocation refuses so the install is never left
            // without an Administrator in force; the caller hands over first (211's handover) and retries.
            var removal = await administrators.AppendRemovalUnderDecisionAsync(
                tenant.Value,
                revokedPartyId,
                AdministratorAuthorityEvent.Revoked,
                reason,
                admittedDecision,
                cancellationToken).ConfigureAwait(false);
            if (removal.Outcome is AdministratorAuthorityOutcome.RefusedLastUsableAdministrator)
            {
                await AppendRefusalAuditAsync(
                    tenant, reaction, removal.Code, admittedDecision, cancellationToken).ConfigureAwait(false);
                throw new LastUsableAdministratorRevocationRefusedException(removal.Code);
            }

            if (removal.Outcome is not AdministratorAuthorityOutcome.Applied)
            {
                throw new InvalidOperationException(
                    "The roster revocation could not write its administrator removal: " + removal.Code);
            }

            RosterRecordCrdtState? state;
            if (current.Contains(revokedPartyId))
            {
                var (afterRevocation, signedRevocation) = current.SignRevoke(
                    revokedByPartyId, signer, revokedPartyId, verifier, admittedDecision.DecidedAt, Guid.NewGuid());
                state = RosterRecordCrdtState.FromRevocation(signedRevocation);
                await projection.PublishLocalAsync(state, cancellationToken).ConfigureAwait(false);
                roster.AdoptSyncedRoster(afterRevocation);
            }
            else
            {
                state = projection.Snapshot().LastOrDefault(record =>
                    record.Kind == RosterRecordKind.Revocation
                    && string.Equals(record.TeamId, tenant.Value, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(record.PartyId, revokedPartyId, StringComparison.Ordinal)
                    && string.Equals(record.AdmittedByPartyId, revokedByPartyId, StringComparison.Ordinal)
                    && record.ToRevocationOrNull() is not null);
                if (state is null) return null;
            }

            await foreach (var existing in audit.QueryAsync(
                               new AuditQuery(tenant, AuditEventType.MemberRevoked), cancellationToken)
                               .ConfigureAwait(false))
            {
                if (existing.Target == reaction.Target)
                    return ToEvidence(state);
            }

            var body = new Dictionary<string, object?>(new EnrollmentCompensatingControlPayloads.MemberRevokedPayload(
                tenant, state.TeamId, revokedByPartyId, revokedPartyId, correlationId).ToBody())
            {
                ["reason"] = reason,
            };
            var signedAudit = await signer.SignAsync(
                new AuditPayload(body), admittedDecision.DecidedAt, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            await audit.AppendAuthorizedAsync(new AuditRecord(
                Guid.NewGuid(), tenant, AuditEventType.MemberRevoked, admittedDecision.DecidedAt, signedAudit,
                ImmutableArray<AttestingSignature>.Empty, Actor: admittedDecision.Request.Principal,
                Target: reaction.Target, Act: reaction.Act), admittedDecision, cancellationToken).ConfigureAwait(false);

            return ToEvidence(state);
        }
        finally
        {
            gate.Release();
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
