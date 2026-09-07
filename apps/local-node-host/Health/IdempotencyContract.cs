namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Shared bounds for the two node-local idempotency regimes.</summary>
internal static class IdempotencyContract
{
    internal const string HeaderName = "Idempotency-Key";
    internal const int MaxKeyLength = 200;
}
