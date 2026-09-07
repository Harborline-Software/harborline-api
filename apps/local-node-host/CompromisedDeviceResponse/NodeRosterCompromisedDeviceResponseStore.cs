using System.Text.Json;

using Harborline.Api.LocalNodeHost.Data.Roster;

using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;

/// <summary>Stores signed compromised-device response records in the node's durable roster database.</summary>
public sealed class NodeRosterCompromisedDeviceResponseStore : ICompromisedDeviceResponseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<NodeLocalRosterDbContext> _contextFactory;

    /// <summary>Creates a store over the roster database factory.</summary>
    public NodeRosterCompromisedDeviceResponseStore(
        IDbContextFactory<NodeLocalRosterDbContext> contextFactory) =>
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async ValueTask AppendAsync(
        CompromisedDeviceResponseRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        context.Set<CompromisedDeviceResponseRow>().Add(ToRow(record));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds a durable response by correlation identifier.</summary>
    public async ValueTask<CompromisedDeviceResponseRecord?> FindAsync(
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var row = await context.Set<CompromisedDeviceResponseRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.CorrelationId == correlationId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : FromRow(row);
    }

    private static CompromisedDeviceResponseRow ToRow(CompromisedDeviceResponseRecord record) => new()
    {
        CorrelationId = record.CorrelationId,
        PayloadJson = record.CanonicalPayloadJson,
        SignerPublicKey = record.SignerPublicKey,
        SignedAt = record.SignedAt,
        SigningNonce = record.SigningNonce,
        Signature = (byte[])record.Signature.Clone(),
    };

    private static CompromisedDeviceResponseRecord FromRow(CompromisedDeviceResponseRow row)
    {
        var payload = JsonSerializer.Deserialize<CompromisedDeviceResponsePayload>(row.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Compromised-device response '{row.CorrelationId}' has an invalid payload.");
        return new CompromisedDeviceResponseRecord(
            row.CorrelationId,
            payload,
            row.PayloadJson,
            row.SignerPublicKey,
            row.SignedAt,
            row.SigningNonce,
            (byte[])row.Signature.Clone());
    }
}

internal sealed class CompromisedDeviceResponseRow
{
    internal required string CorrelationId { get; init; }

    internal required string PayloadJson { get; init; }

    internal required string SignerPublicKey { get; init; }

    internal required DateTimeOffset SignedAt { get; init; }

    internal required string SigningNonce { get; init; }

    internal required byte[] Signature { get; init; }
}
