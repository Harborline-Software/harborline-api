using System.Security.Cryptography;

namespace Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;

/// <summary>A replacement team subkey distributed to a remaining peer.</summary>
public sealed class TeamSubkeyRotation
{
    private readonly byte[] _newSubkey;

    /// <summary>Creates a rotation for a strictly newer team-key epoch.</summary>
    public TeamSubkeyRotation(string teamId, long epoch, byte[] newSubkey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentNullException.ThrowIfNull(newSubkey);
        if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (newSubkey.Length == 0) throw new ArgumentException("A replacement subkey is required.", nameof(newSubkey));
        TeamId = teamId;
        Epoch = epoch;
        _newSubkey = (byte[])newSubkey.Clone();
    }

    /// <summary>The team whose key changes.</summary>
    public string TeamId { get; }

    /// <summary>The monotonically increasing replacement-key epoch.</summary>
    public long Epoch { get; }

    /// <summary>The replacement subkey, returned as a defensive copy.</summary>
    public byte[] NewSubkey => (byte[])_newSubkey.Clone();
}

/// <summary>Peer-side active team subkey state that replaces exposed material on rotation.</summary>
public sealed class PeerTeamSubkeyState
{
    private readonly object _gate = new();
    private byte[] _activeSubkey;
    private long _epoch;

    /// <summary>Creates peer state at the supplied team-key epoch.</summary>
    public PeerTeamSubkeyState(string teamId, long epoch, byte[] activeSubkey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        ArgumentNullException.ThrowIfNull(activeSubkey);
        if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (activeSubkey.Length == 0) throw new ArgumentException("An active subkey is required.", nameof(activeSubkey));
        TeamId = teamId;
        _epoch = epoch;
        _activeSubkey = (byte[])activeSubkey.Clone();
    }

    /// <summary>The team whose active subkey this peer holds.</summary>
    public string TeamId { get; }

    /// <summary>The active key epoch.</summary>
    public long Epoch
    {
        get
        {
            lock (_gate) return _epoch;
        }
    }

    /// <summary>Replaces the active subkey with a strictly newer rotation for this team.</summary>
    public void ApplyRotation(TeamSubkeyRotation rotation)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        if (!string.Equals(TeamId, rotation.TeamId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A team subkey rotation cannot cross team boundaries.");
        }

        lock (_gate)
        {
            if (rotation.Epoch <= _epoch)
            {
                throw new InvalidOperationException("A team subkey rotation must advance the active epoch.");
            }

            CryptographicOperations.ZeroMemory(_activeSubkey);
            _activeSubkey = (byte[])rotation.NewSubkey.Clone();
            _epoch = rotation.Epoch;
        }
    }

    /// <summary>Returns whether the supplied bytes are the peer's currently active subkey.</summary>
    public bool UsesSubkey(ReadOnlySpan<byte> candidate)
    {
        lock (_gate)
        {
            return candidate.Length == _activeSubkey.Length
                && CryptographicOperations.FixedTimeEquals(candidate, _activeSubkey);
        }
    }
}
