namespace Harborline.Api.Foundation.Recovery;

/// <summary>
/// Default <see cref="IRecoveryClock"/> backed by
/// <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
public sealed class SystemRecoveryClock : IRecoveryClock
{
    private readonly TimeProvider _timeProvider;

    public SystemRecoveryClock(TimeProvider timeProvider) =>
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();
}
