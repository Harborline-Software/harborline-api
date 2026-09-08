using System;
using Harborline.Api.Kernel.Sync.Protocol;

namespace Harborline.Api.Kernel.Sync.Handshake;

/// <summary>
/// A BEHAVIOUR-NEUTRAL diagnostic DECORATOR over an <see cref="IPeerTrustPolicy"/> — it forwards the trust decision
/// UNCHANGED to the wrapped policy and, after the decision is made, invokes a no-secret observer callback so a host
/// can surface WHY a peer was trusted / <c>PEER_UNTRUSTED</c> in its console. The decision itself is the wrapped
/// policy's; this never changes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <see cref="MemberSetTrustPolicy"/> (and <see cref="SharedRootTrustPolicy"/>) answer only a
/// boolean and the handshake rejects with <see cref="ErrorCode.PeerUntrusted"/> — WHY (the peer's presented HELLO
/// transport key is not in this node's trusted set) is invisible. During pre-release development that hidden reason
/// cost long blind debugging slogs. This decorator makes the decision observable WITHOUT touching the trust LOGIC:
/// the wrapped policy decides; the observer only watches.
/// </para>
/// <para>
/// <b>No secret values.</b> The observer receives ONLY the decision (a boolean) and two LENGTHS / COUNTS — the
/// presented key's byte length and the size of the trusted set — never a key value. kernel-sync stays logging-free:
/// the callback is a plain delegate the host (local-node-host) bridges to its diagnostic logger, so this package
/// takes on no logging dependency.
/// </para>
/// <para>
/// <b>Behaviour-neutral + fail-safe.</b> <see cref="IsTrusted"/> returns EXACTLY what the inner policy returns. The
/// observer is invoked in a try/catch that swallows any observer fault — a diagnostic must NEVER change the trust
/// outcome or break the handshake. When the observer is null the decorator is a pure pass-through.
/// </para>
/// </remarks>
public sealed class DiagnosticPeerTrustPolicy : IPeerTrustPolicy
{
    private readonly IPeerTrustPolicy _inner;
    private readonly Func<int>? _trustedSetCount;
    private readonly Action<bool, int, int>? _observe;

    /// <summary>
    /// Wrap <paramref name="inner"/> with a diagnostic observer. The decision is the inner policy's; the observer is
    /// called AFTER with <c>(trusted, presentedKeyLength, trustedSetCount)</c> — all non-secret.
    /// </summary>
    /// <param name="inner">The real trust policy whose decision is forwarded unchanged.</param>
    /// <param name="observe">The no-secret observer callback: <c>(trusted, presentedKeyLength, trustedSetCount)</c>.
    /// Null ⇒ a pure pass-through (no diagnostics).</param>
    /// <param name="trustedSetCount">Optional accessor for the current trusted-set SIZE (a count, not the keys) — for
    /// the diagnostic only. Null ⇒ <c>-1</c> is reported (size unknown).</param>
    public DiagnosticPeerTrustPolicy(
        IPeerTrustPolicy inner,
        Action<bool, int, int>? observe,
        Func<int>? trustedSetCount = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _observe = observe;
        _trustedSetCount = trustedSetCount;
    }

    /// <inheritdoc />
    public bool IsTrusted(HelloMessage peerHello)
    {
        // The decision is the wrapped policy's — UNCHANGED. This decorator never alters trust.
        var trusted = _inner.IsTrusted(peerHello);

        // Observe AFTER, fail-safe: a diagnostic must never change the outcome or throw into the handshake.
        if (_observe is not null)
        {
            try
            {
                var presentedLen = peerHello?.PublicKey?.Length ?? 0;
                var setCount = _trustedSetCount is not null ? SafeCount(_trustedSetCount) : -1;
                _observe(trusted, presentedLen, setCount);
            }
            catch
            {
                // Swallow — the diagnostic is best-effort and must not affect trust or the handshake.
            }
        }

        return trusted;
    }

    private static int SafeCount(Func<int> count)
    {
        try { return count(); }
        catch { return -1; }
    }
}

/// <summary>
/// The host-supplied, no-secret OBSERVER the per-team registrar bridges peer-trust decisions to (so the host can
/// surface <c>PEER_UNTRUSTED</c> in its console). Kernel-sync owns the abstraction (it owns
/// <see cref="IPeerTrustPolicy"/>); the host (local-node-host) implements it over its diagnostic logger, keeping
/// kernel-sync logging-free. OPTIONAL: when no implementation is registered the registrar wraps no decorator and the
/// trust path is byte-identical to before.
/// </summary>
public interface IPeerTrustDiagnosticObserver
{
    /// <summary>
    /// Observe a peer-trust decision — <paramref name="trusted"/> + the presented HELLO key BYTE LENGTH +
    /// the trusted-set SIZE. NO key value is ever passed. Called AFTER the (unchanged) decision; must not throw.
    /// </summary>
    void OnPeerTrustDecision(bool trusted, int presentedKeyLength, int trustedSetCount);
}
