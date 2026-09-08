using System.Security.Cryptography;

using Harborline.Api.LocalNodeHost;

namespace Harborline.Api.NodeHosting;

/// <summary>Owns the in-process node lifecycle while preserving the loopback HTTP boundary.</summary>
public interface INodeHost
{
    /// <summary>Starts the node and returns its loopback address and in-memory session token.</summary>
    Task<(Uri NodeAddress, string SessionToken)> StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops the node and releases its listener.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>In-process implementation of <see cref="INodeHost"/>.</summary>
public sealed class NodeHost : INodeHost
{
    private const string NodeDataDirectoryVariable = "LocalNode__DataDirectory";
    private const string ClientDataDirectoryVariable = "HARBORLINE_NODE_DATA_DIRECTORY";

    // The pre-rename spelling. Read only, and only when the new name is unset, so a launcher script that
    // still exports the old name keeps working for one release; the new name is the one this host writes.
    private const string LegacyClientDataDirectoryVariable = "SHIPYARD_CARRIER_NODE_DATA_DIRECTORY";

    private readonly object _gate = new();
    private string? _previousNodeDataDirectory;
    private string? _previousClientDataDirectory;
    private bool _started;

    /// <inheritdoc />
    public async Task<(Uri NodeAddress, string SessionToken)> StartAsync(CancellationToken cancellationToken)
    {
        string token;
        string dataDirectory;
        lock (_gate)
        {
            if (_started)
                throw new InvalidOperationException("The node is already running.");

            _previousNodeDataDirectory = Environment.GetEnvironmentVariable(NodeDataDirectoryVariable);
            _previousClientDataDirectory = Environment.GetEnvironmentVariable(ClientDataDirectoryVariable);

            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            dataDirectory = Environment.GetEnvironmentVariable(ClientDataDirectoryVariable)
                ?? Environment.GetEnvironmentVariable(LegacyClientDataDirectoryVariable)
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                dataDirectory = Path.Combine(AppContext.BaseDirectory, "carrier-node-data");
                Environment.SetEnvironmentVariable(ClientDataDirectoryVariable, dataDirectory);
            }

            Environment.SetEnvironmentVariable(NodeDataDirectoryVariable, dataDirectory);
            _started = true;
        }

        try
        {
            var nodeAddress = await LocalNodeHostRuntime.StartAsync(token, dataDirectory, cancellationToken)
                .ConfigureAwait(false);
            return (nodeAddress, token);
        }
        catch
        {
            RestoreEnvironment();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        bool shouldStop;
        lock (_gate) shouldStop = _started;
        if (!shouldStop) return;

        try
        {
            await LocalNodeHostRuntime.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RestoreEnvironment();
        }
    }

    private void RestoreEnvironment()
    {
        lock (_gate)
        {
            if (!_started) return;

            Environment.SetEnvironmentVariable(NodeDataDirectoryVariable, _previousNodeDataDirectory);
            Environment.SetEnvironmentVariable(ClientDataDirectoryVariable, _previousClientDataDirectory);
            _previousNodeDataDirectory = null;
            _previousClientDataDirectory = null;
            _started = false;
        }
    }
}
