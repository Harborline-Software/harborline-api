using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Data.Audit;

/// <summary>
/// An immutable snapshot of the acting caller's AUTHZ attribution facts for one node-local durable
/// mutation (MTW-2 2612-C). Projected from the request-bound
/// <see cref="SelectedSessionRequestPrincipal"/> — the member's People Party, their membership, the
/// session correlation, and the pinned authority versions the request was authorized under. It rides
/// INSIDE the node-signed audit envelope (<see cref="NodeAttributionEnvelope"/>), so a verifier reads
/// WHO the node attests acted, bound to the same signed bytes.
/// </summary>
/// <remarks>
/// <b>Vocabulary (board finding F8).</b> Everything here is the AUTHZ identity — People
/// <c>PartyId</c> strings, membership ids, authorization epochs. It is deliberately kept separate from
/// the CRYPTO <see cref="Harborline.Api.Foundation.Crypto.PrincipalId"/> (a 32-byte Ed25519 public key) that
/// signs the envelope: <see cref="MemberPartyId"/> is NOT a signing key and never a
/// <c>PrincipalId</c>. The node's attesting key is carried separately, as the envelope's
/// <c>attesting_public_key</c> binding row.
/// </remarks>
internal sealed record NodeCallerAttribution(
    string MemberPartyId,
    string MembershipId,
    long MembershipOwnerVersion,
    string SessionCorrelationId,
    string CoordinationCorrelationId,
    long AuthorizationEpoch)
{
    /// <summary>
    /// Project one live, request-bound <see cref="SelectedSessionRequestPrincipal"/> into the flat,
    /// immutable AUTHZ snapshot that rides inside the signed audit envelope. The principal ITSELF is
    /// never carried past the request feature (its own doctrine: "never stored in a singleton, ambient,
    /// or AsyncLocal context holder") — this projection is what the listener publishes to
    /// <see cref="NodeCallerAttributionScope"/>.
    /// </summary>
    internal static NodeCallerAttribution From(SelectedSessionRequestPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new NodeCallerAttribution(
            MemberPartyId: principal.CanonicalParty.Value,
            MembershipId: principal.MembershipId,
            MembershipOwnerVersion: principal.MembershipOwnerVersion,
            SessionCorrelationId: principal.SessionCorrelationId,
            CoordinationCorrelationId: principal.CoordinationCorrelationId,
            AuthorizationEpoch: principal.AuthorizationEpoch);
    }
}

/// <summary>
/// Resolves the acting caller's <see cref="NodeCallerAttribution"/> for the CURRENT node-local durable
/// mutation, or <c>null</c> when no selected-session principal is bound to the in-flight request — the
/// single-operator bootstrap / desktop / detached-workflow path (the ruled fallback option (a), Admiral
/// 2026-07-22). A <c>null</c> result is the signal for the operator-fallback attribution, NOT an error.
/// </summary>
internal interface INodeCallerAttributionSource
{
    /// <summary>The acting caller's attribution, or <c>null</c> on the unbound (operator) path.</summary>
    NodeCallerAttribution? TryResolveCurrent();
}

/// <summary>
/// The production <see cref="INodeCallerAttributionSource"/>: reads whatever the listener published
/// into the explicit <see cref="NodeCallerAttributionScope"/> for the in-flight request. No scope open
/// (a detached workflow effect, bootstrap, or the desktop single-operator path) ⇒ <c>null</c> ⇒ the
/// ruled operator fallback.
/// </summary>

/// <summary>
/// The explicit ambient (<see cref="AsyncLocal{T}"/>) context holder for the acting caller's attribution across
/// one request's <c>route → posting service → store → SaveChangesAsync → enlister</c> call chain. The
/// listener's selected-session accept branch (<c>SharedHostedWebApp</c>) opens exactly one scope in a
/// <c>using</c> around <c>next(context)</c>, immediately after it binds the request principal; the
/// <see cref="NodeAuditWriteEnlister"/> reads <see cref="Current"/> inside the mutation's transaction.
/// Same pattern as <c>HomeEpochWriteScope</c> / <c>RecurringInvoiceWriteScope</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an explicit scope and NOT <c>IHttpContextAccessor</c> (card #3192).</b> The accessor's
/// AsyncLocal is populated by ASP.NET Core's <c>DefaultHttpContextFactory</c> ONLY when the container of
/// the app SERVING the request resolves <c>IHttpContextAccessor</c>. The node registers the audit slice
/// on the OUTER generic host (<c>Program.cs</c>, <c>Host.CreateApplicationBuilder</c> — no HTTP pipeline
/// at all) while requests are served by the INNER <c>WebApplication</c> inside
/// <c>SharedHostedWebApp</c>. That cross-container dependency was invisible, unassertable from either
/// composition root, and silently dead — every production audit row stamped <c>operator-fallback</c>.
/// This context holder has NO container in its path: the write site and the read site are two types in this
/// assembly, so no registration in any other container can break it.
/// </para>
/// <para>
/// <b>What is carried.</b> The flat <see cref="NodeCallerAttribution"/> PROJECTION only — never the
/// <see cref="SelectedSessionRequestPrincipal"/> itself, which stays request-feature-only by its own
/// doctrine. This is not an ambient <c>IPartyContext</c>/<c>ICurrentUser</c> identity resolver (2611
/// territory): it is a per-request, per-mutation snapshot of the authority the request was ALREADY
/// revalidated under, with the same lifetime as the request.
/// </para>
/// <para>
/// <b>Leak safety.</b> The value lives behind a mutable holder, so <see cref="Dispose"/> clears it for
/// EVERY execution context that captured the scope — a task spawned during the request cannot keep
/// attributing later writes to that member. This is the same indirection ASP.NET Core's own
/// <c>HttpContextAccessor</c> uses for exactly this reason.
/// </para>
/// </remarks>
internal sealed class NodeCallerAttributionScope : IDisposable
{
    private sealed class Holder
    {
        internal NodeCallerAttribution? Attribution;
        internal DevicePrincipal? Device;
    }

    private sealed record DevicePrincipal(string DeviceId, string TenantId, string PrincipalId);

    private static readonly AsyncLocal<Holder?> _current = new();

    private readonly Holder _holder;
    private readonly Holder? _previous;
    private bool _disposed;

    private NodeCallerAttributionScope(NodeCallerAttribution attribution)
    {
        _previous = _current.Value;
        _holder = new Holder { Attribution = attribution };
        _current.Value = _holder;
    }

    /// <summary>
    /// The acting caller's attribution for the in-flight request, or <see langword="null"/> when no scope
    /// is open — the unbound bootstrap / desktop / detached-workflow path (the ruled operator fallback).
    /// </summary>
    internal static NodeCallerAttribution? Current => _current.Value?.Attribution;

    /// <summary>
    /// Whether a hosted-WEB request principal is bound to the in-flight call (card #3356). This is the
    /// same fact <see cref="Current"/> carries, asked as a plane question: the listener opens exactly one
    /// scope per selected-session request, so a scope being open IS "a web-plane member is acting".
    /// </summary>
    /// <remarks>
    /// Deliberately the SAME context holder the audit envelope reads, rather than a second parallel one. ADR 0160
    /// R3-D makes attribution and authorization two readings of one fact — WHO is acting — so giving them
    /// separate context holders would let them disagree, and would double the number of context holders that can go
    /// silently inert (the failure card #3192 already caught once). Both readings are kept honest by tests
    /// that drive the real listener: <c>NodeServingPipelineAttributionTests</c> for the audit side and
    /// <c>WebPlaneAuthorizationFenceTests</c> for this one.
    /// </remarks>
    internal static bool HasBoundWebPrincipal => Current is not null || _current.Value?.Device is not null;

    /// <summary>
    /// Opens the device-plane principal scope. A device principal is intentionally not converted into an
    /// operator attribution; the authorization fence only needs the stronger fact that a non-desktop
    /// principal is acting, so operator grants are refused rather than borrowed.
    /// </summary>
    internal static IDisposable EnterDevice(string deviceId, string tenantId, string principalId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(principalId))
        {
            throw new ArgumentException("A device principal must be fully bound before entering scope.");
        }
        var previous = _current.Value;
        var holder = new Holder
        {
            Device = new DevicePrincipal(deviceId, tenantId, principalId),
        };
        _current.Value = holder;
        return new DeviceScope(holder, previous);
    }

    private sealed class DeviceScope(Holder holder, Holder? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            holder.Device = null;
            _current.Value = previous;
        }
    }

    /// <summary>
    /// Opens the ambient scope for <paramref name="attribution"/>. Dispose clears it (for every captured
    /// flow) and restores any enclosing scope, so nesting is well-behaved.
    /// </summary>
    /// <remarks>
    /// <b>Two consumers, and the second one is a security control.</b> Besides the audit enlister, this
    /// scope is read by <c>NodeCallerParty.Resolve</c> as the signal that a hosted-web member
    /// is acting (card #3356) — so WHEN it is opened is an authorization decision, not only an audit one.
    /// It must be opened for EVERY selected-session request, reads included. Narrowing it to mutations
    /// would be defensible audit hygiene and would silently unfence every gated read route;
    /// <c>WebPlaneAuthorizationFenceTests</c> drives a gated GET so that narrowing fails a test.
    /// </remarks>
    internal static NodeCallerAttributionScope Enter(NodeCallerAttribution attribution)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        return new NodeCallerAttributionScope(attribution);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _holder.Attribution = null;
        _current.Value = _previous;
    }
}
