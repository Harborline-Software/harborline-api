namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>The shared backup handle that fences key destruction and byte sweeping (ADR 0127).</summary>
public sealed class ContentStoreBackupActivity : IDisposable
{
    private readonly SemaphoreSlim _destruction = new(1, 1);
    private readonly object _gate = new();
    private int _backups;

    /// <inheritdoc />
    public void Dispose() => _destruction.Dispose();

    /// <summary>Opens a backup before copying; disposal closes it, including after a failed copy.</summary>
    public async ValueTask<IDisposable> OpenAsync(CancellationToken cancellationToken = default)
    {
        await _destruction.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate) _backups++;
            return new Handle(() => { lock (_gate) _backups--; });
        }
        finally
        {
            _destruction.Release();
        }
    }

    internal async ValueTask<IDisposable?> TryEnterDestructionAsync(CancellationToken cancellationToken)
    {
        await _destruction.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_backups == 0) return new Handle(() => _destruction.Release());
        }
        _destruction.Release();
        return null;
    }

    private sealed class Handle(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
