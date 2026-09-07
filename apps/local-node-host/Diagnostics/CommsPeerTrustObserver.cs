using Harborline.Api.Kernel.Sync.Handshake;

namespace Harborline.Api.LocalNodeHost.Diagnostics;

/// <summary>
/// Bridges the kernel-sync <see cref="IPeerTrustDiagnosticObserver"/> (resolved by the per-team registrar to wrap
/// the trust policy in a behaviour-neutral diagnostic decorator) to this host's <see cref="CommsDiagnostics"/>
/// logger — so a <c>PEER_UNTRUSTED</c>-vs-trusted decision shows in the node console (where the operator reads the
/// tauri:dev terminal). Kernel-sync stays logging-free; the host owns the logging here.
/// </summary>
/// <remarks>
/// No secret values: the observer receives only the decision + the presented key BYTE LENGTH + the trusted-set
/// COUNT, and forwards exactly those to <see cref="CommsDiagnostics.PeerTrustDecision"/>. When the diagnostic flag is
/// OFF the underlying <see cref="CommsDiagnostics"/> no-ops — so this bridge is inert in the production-release
/// posture even though the registrar wrapped the decorator.
/// </remarks>
internal sealed class CommsPeerTrustObserver : IPeerTrustDiagnosticObserver
{
    private readonly CommsDiagnostics _diag;

    internal CommsPeerTrustObserver(CommsDiagnostics diag)
    {
        _diag = diag ?? CommsDiagnostics.Disabled;
    }

    /// <inheritdoc />
    public void OnPeerTrustDecision(bool trusted, int presentedKeyLength, int trustedSetCount)
        => _diag.PeerTrustDecision(trusted, presentedKeyLength, trustedSetCount);
}
