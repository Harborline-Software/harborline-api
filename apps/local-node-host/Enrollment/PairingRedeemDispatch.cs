using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// MTW-2 #3167 — the WEB-ADMITTED PAIRING dispatch bundle the redeem route consumes when the pairing path is wired
/// (production). Groups the token-gated pairing admitter, the pairing-binding lookup, the R2 mode-exclusivity
/// predicate, the R5 rate limiter, and the active-team accessor (for the per-tenant scope). When this bundle is
/// absent (legacy / plain-only hosts + tests), the redeem route behaves exactly as before (plain
/// <see cref="WireEnrollmentAdmitter"/> only) — the dispatch is additive.
/// </summary>
/// <param name="Admitter">The token-gated pairing admitter (R1.3 + R4-a).</param>
/// <param name="Bindings">The pairing-binding lookup — a token id with a binding routes to the pairing path.</param>
/// <param name="WebPlaneState">The R2 predicate — when web-plane admission is enabled for the tenant, the plain
/// path is mode-exclusively OFF (a binding-absent redeem refuses opaque).</param>
/// <param name="RateLimiter">The R5 per-source + per-tenant rate limiter for the enrollment route.</param>
/// <param name="ActiveTeam">The active-team accessor (resolves the per-tenant scope for R2 + R5).</param>
internal interface IEnrollmentRedeemDispatch
{
    Task<PairingDispatchOutcome> DispatchAsync(
        EnrollmentRequest request,
        string source,
        CancellationToken ct);
}

internal sealed record PairingRedeemDispatch(
    PairingTokenGatedAdmitter Admitter,
    IWebPairingInviteBindingStore Bindings,
    IWebPlaneAdmissionState WebPlaneState,
    PairingRedeemRateLimiter RateLimiter,
    IActiveTeamAccessor ActiveTeam) : IEnrollmentRedeemDispatch
{
    /// <summary>
    /// Apply the shared R5 rate limit and R2 mode-exclusive pairing decision for EVERY enrollment transport.
    /// The loopback HTTP route and the cross-machine 7473 handler both call this method; neither transport may
    /// bypass the controls by calling an admitter directly.
    /// </summary>
    /// <remarks>
    /// M4 transition semantics: the completed <see cref="IWebPlaneAdmissionState.IsEnabledAsync"/> read is the
    /// linearization point for a binding-absent attempt. An attempt that observes disabled may finish on the plain
    /// path even if the first web grant commits immediately afterward; every attempt whose read completes after
    /// that GRANT transition refuses. The epoch is no longer part of the transition — a live grant whose epoch row
    /// is absent now closes this path on its own, because an authorization that cannot be checked against the
    /// current epoch cannot be revoked. This deliberately avoids holding a cross-DbContext transaction
    /// across the admission store and the web authorization store.
    /// </remarks>
    public async Task<PairingDispatchOutcome> DispatchAsync(
        EnrollmentRequest request,
        string source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var binding = Bindings.Lookup(request.TokenId);
            var tenantValue = binding?.Membership.TenantId ?? NodeTenant.Resolve(ActiveTeam).Value;
            if (!RateLimiter.TryAcquire(source, tenantValue))
            {
                return PairingDispatchOutcome.RateLimited();
            }

            if (binding is not null)
            {
                var outcome = await Admitter.AdmitAsync(request, binding, ct).ConfigureAwait(false);
                return outcome.Accepted && outcome.Response is not null
                    ? PairingDispatchOutcome.Accept(outcome.Response)
                    : PairingDispatchOutcome.Refuse(outcome.RejectReason);
            }

            return await WebPlaneState.IsEnabledAsync(NodeTenant.Resolve(ActiveTeam), ct).ConfigureAwait(false)
                ? PairingDispatchOutcome.Refuse("plain_path_disabled")
                : PairingDispatchOutcome.PlainPath();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // R6/C — a durable-store / web-plane lookup fault fails CLOSED to the same opaque refusal. The
            // exception type is retained only as an internal diagnostic reason; transports never serialize it.
            return PairingDispatchOutcome.Refuse($"pairing_dispatch_faulted:{ex.GetType().Name}");
        }
    }
}

/// <summary>
/// Type-enforced transport-neutral outcome of the shared pairing dispatch. Refusals expose no response payload;
/// each wire adapter can therefore emit only its one opaque refusal shape.
/// </summary>
internal sealed record PairingDispatchOutcome
{
    private PairingDispatchOutcome(
        PairingDispatchKind kind,
        EnrollmentResponse? response,
        string? internalReason)
    {
        Kind = kind;
        Response = response;
        InternalReason = internalReason;
    }

    internal PairingDispatchKind Kind { get; }
    internal EnrollmentResponse? Response { get; }

    /// <summary>Diagnostics only. Never serialize this value to either enrollment wire.</summary>
    internal string? InternalReason { get; }

    internal static PairingDispatchOutcome PlainPath() =>
        new(PairingDispatchKind.PlainPath, null, null);

    internal static PairingDispatchOutcome Accept(EnrollmentResponse response) =>
        new(PairingDispatchKind.Accepted, response, null);

    internal static PairingDispatchOutcome Refuse(string? internalReason) =>
        new(PairingDispatchKind.Refused, null, internalReason);

    internal static PairingDispatchOutcome RateLimited() =>
        new(PairingDispatchKind.RateLimited, null, "rate_limited");
}

internal enum PairingDispatchKind
{
    PlainPath,
    Accepted,
    Refused,
    RateLimited,
}

/// <summary>
/// MTW-2 #3167 (R5) — a coarse, in-memory PER-SOURCE + PER-TENANT rate limiter for the enrollment redeem route.
/// </summary>
/// <remarks>
/// <para>
/// <b>ACCEPTED TIMING RISK (R5 / verdict finding A / F4-b), documented here as the ruling requires.</b> Full
/// refusal-timing equalization is NOT implemented: a pre-crypto pairing refusal (bad PoP / malformed key / absent
/// binding) returns after materially less work than a signing-path refusal, so the redeem route carries a PARTIAL
/// timing oracle the F4 body/status byte-equality does not cover. The ruling ACCEPTS this residual because the
/// enrollment route is human-paced and single-shot per token, and mandates these compensations instead: (a) THIS
/// rate limiter (per-source AND per-tenant), (b) the F3 pre-check ordering stays cheap-refusals-first (the
/// fail-closed direction), and (c) this recorded acceptance. Revisit ONLY if enrollment ever becomes
/// machine-paced. This throttle is a VOLUME cap, orthogonal to the F4 admission-decision oracle — exceeding it is
/// an honest 429, not an admission refusal.
/// </para>
/// <para>
/// <b>Shape.</b> A sliding-window counter over the last <see cref="_window"/> keyed by <c>src:&lt;ip&gt;</c> and
/// <c>ten:&lt;tenant&gt;</c>. A single process-wide lock serialises the prune-count-record (admission is
/// low-frequency + human-paced, so contention is irrelevant). Defaults are deliberately generous so a legitimate
/// operator / Harborline App flow never trips it; they exist to bound automated abuse, not to gate normal use.
/// </para>
/// </remarks>
public sealed class PairingRedeemRateLimiter
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _window;
    private readonly int _perSourceMax;
    private readonly int _perTenantMax;
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);

    public PairingRedeemRateLimiter(
        TimeProvider? clock = null,
        TimeSpan? window = null,
        int perSourceMax = 20,
        int perTenantMax = 120)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _window = window ?? TimeSpan.FromMinutes(1);
        if (perSourceMax <= 0) throw new ArgumentOutOfRangeException(nameof(perSourceMax));
        if (perTenantMax <= 0) throw new ArgumentOutOfRangeException(nameof(perTenantMax));
        _perSourceMax = perSourceMax;
        _perTenantMax = perTenantMax;
    }

    /// <summary>
    /// Try to acquire ONE redeem slot for (<paramref name="source"/>, <paramref name="tenant"/>). Returns false iff
    /// either the per-source OR the per-tenant window cap is already reached — in which case NOTHING is recorded (the
    /// caller is throttled, so its attempt does not count against the window). On success both counters are recorded.
    /// </summary>
    public bool TryAcquire(string source, string tenant) =>
        TryAcquireScopes($"src:{source ?? "unknown"}", $"ten:{tenant ?? "unknown"}");

    /// <summary>
    /// Applies the same two-window mechanism to already-namespaced scopes. Account-setup acceptance uses an
    /// invitation fingerprint as the first scope and the route as the second: distinct invitees behind one source
    /// address retain independent allowances, while cycling invitation and tenant input cannot evade the route cap.
    /// </summary>
    internal bool TryAcquireScopes(string firstScope, string secondScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondScope);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            // Prune + count both windows BEFORE recording either, so a per-tenant reject does not leave a stray
            // per-source hit (and vice versa) — the throttled attempt is not counted.
            if (PrunedCount(firstScope, now) >= _perSourceMax) return false;
            if (PrunedCount(secondScope, now) >= _perTenantMax) return false;
            Record(firstScope, now);
            Record(secondScope, now);
            return true;
        }
    }

    private int PrunedCount(string key, DateTimeOffset now)
    {
        if (!_hits.TryGetValue(key, out var q)) return 0;
        var cutoff = now - _window;
        while (q.Count > 0 && q.Peek() <= cutoff) q.Dequeue();
        if (q.Count == 0) _hits.Remove(key);
        return q.Count;
    }

    private void Record(string key, DateTimeOffset now)
    {
        if (!_hits.TryGetValue(key, out var q))
        {
            q = new Queue<DateTimeOffset>();
            _hits[key] = q;
        }
        q.Enqueue(now);
    }
}
