namespace Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;

/// <summary>Records explicit key-rotation deferral while the stored, distributable key hierarchy is unavailable.</summary>
public sealed class DeferredCompromiseKeyRotation : ICompromiseKeyRotation
{
    /// <summary>The exact key families that remain exposed when rotation is deferred.</summary>
    public static IReadOnlyList<string> RemainingExposedKeys { get; } =
    [
        "install root seed",
        "team transport signing subkey",
        "team direct-message encryption subkey",
        "team recovery subkey",
        "team X-Wing subkey",
        "SQLCipher store key",
        "tenant and subject content keys",
    ];

    /// <inheritdoc />
    public ValueTask<CompromiseKeyResponse> RespondAsync(
        CompromisedDeviceRevocation revocation,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return ValueTask.FromResult(new CompromiseKeyResponse(
            "deferred",
            RemainingExposedKeys));
    }
}
