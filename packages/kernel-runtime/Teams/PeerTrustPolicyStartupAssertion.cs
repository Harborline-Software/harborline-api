using Harborline.Api.Kernel.Sync.Handshake;

namespace Harborline.Api.Kernel.Runtime.Teams;

/// <summary>Fails startup when a networked team would otherwise inherit the sync daemon's allow-all default.</summary>
public static class PeerTrustPolicyStartupAssertion
{
    /// <summary>Returns the configured policy, or throws before the team service provider is activated.</summary>
    public static IPeerTrustPolicy Require(IPeerTrustPolicy? policy) =>
        policy ?? throw new PeerTrustPolicyConfigurationException();
}

/// <summary>Raised when a team is composed without the mandatory peer trust gate.</summary>
public sealed class PeerTrustPolicyConfigurationException : InvalidOperationException
{
    /// <summary>Creates the fail-closed startup error.</summary>
    public PeerTrustPolicyConfigurationException()
        : base("A non-null peer trust policy is required before the sync daemon starts.")
    {
    }

    /// <summary>The stable configuration error code.</summary>
    public string ErrorCode => "sync.peer_trust_policy_required";
}
