using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// Appends one envelope to the installation-identity tamper-evident audit chain: it extends the
/// SHA-256 hash chain from the current head and advances the head record. This is the single durable
/// audit path shared by the installation-chain writers — it does NOT fork a parallel logging path.
///
/// Callers invoke it from inside their own serializable transaction and persist it with their
/// existing <c>SaveChangesAsync</c>/<c>CommitAsync</c>, so the audit envelope commits atomically with
/// the durable mutation it records (the durable-layer audit-envelope pattern, per the fleet's
/// financial-audit convention). The whole chain is re-validated before extension, so the writer
/// refuses to append onto a corrupted or non-authoritative chain.
/// </summary>
internal static class InstallationIdentityAuditChain
{
    internal static async Task AppendEventAsync(
        NodeLocalInstallationIdentityDbContext context,
        string eventType,
        string actorKind,
        string actorId,
        string correlationId,
        string commandFingerprint,
        string payloadDigest,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDigest);

        var installation = await context.InstallationIdentities.SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var root = await context.RootKeyEpochs.SingleAsync(
                item => item.InstallationIdentityId == installation.InstallationIdentityId &&
                    item.EpochNumber == installation.ActiveRootEpoch,
                cancellationToken)
            .ConfigureAwait(false);
        var head = await context.AuditHeads.SingleAsync(
                item => item.InstallationIdentityId == installation.InstallationIdentityId,
                cancellationToken)
            .ConfigureAwait(false);
        var chain = await context.AuditEnvelopes.AsNoTracking()
            .Where(item => item.InstallationIdentityId == installation.InstallationIdentityId)
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (root.Status != InstallationRootEpochStatus.Active ||
            !InstallationAuditIntegrity.HasValidChain(chain, head, installation.InstallationIdentityId))
        {
            throw new InvalidOperationException(
                "installation-identity.audit_chain_invalid: installation audit is not authoritative.");
        }

        var envelope = new InstallationAuditEnvelopeRecord
        {
            InstallationIdentityId = installation.InstallationIdentityId,
            Sequence = head.Sequence + 1,
            CorrelationId = correlationId,
            CommandFingerprint = commandFingerprint,
            EventType = eventType,
            ActorKind = actorKind,
            ActorId = actorId,
            RootEpoch = root.EpochNumber,
            RootPublicKeyFingerprint = root.RootPublicKeyFingerprint,
            PreviousHash = head.HeadHash,
            EnvelopeHash = string.Empty,
            PayloadDigest = payloadDigest,
            OccurredAtUtc = occurredAtUtc,
        };
        envelope.EnvelopeHash = InstallationAuditIntegrity.ComputeEnvelopeHash(envelope);
        context.AuditEnvelopes.Add(envelope);

        head.Sequence = envelope.Sequence;
        head.HeadHash = envelope.EnvelopeHash;
        head.OwnerVersion++;
        head.UpdatedAtUtc = occurredAtUtc;
    }
}
