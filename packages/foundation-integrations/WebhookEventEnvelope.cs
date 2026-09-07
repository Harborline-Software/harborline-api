using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Integrations;

/// <summary>
/// Normalized provider-initiated event. Adapters verify provider-specific
/// signatures before constructing the envelope; the dispatcher hands the
/// envelope to registered handlers without re-interpreting it.
/// </summary>
public sealed record WebhookEventEnvelope
{
    /// <summary>Provider that originated the event.</summary>
    public required string ProviderKey { get; init; }

    /// <summary>Provider-assigned event identifier (opaque).</summary>
    public required string EventId { get; init; }

    /// <summary>Provider-assigned event type (e.g. <c>payment.succeeded</c>).</summary>
    public required string EventType { get; init; }

    /// <summary>Ingestion timestamp (UTC).</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Raw request body, preserved for audit and signature re-verification.</summary>
    public required byte[] RawBody { get; init; }

    /// <summary>Optional tenant scope the event applies to, if determinable.</summary>
    public TenantId? TenantId { get; init; }

    /// <summary>Optional signature metadata (scheme, value, verified flag) for audit.</summary>
    public IReadOnlyDictionary<string, string> SignatureMetadata { get; init; }
        = new Dictionary<string, string>();
}
