using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The record a guarded route act addresses. It exists so that "this act addresses no record" is a word a
/// caller has to write (<see cref="TheInstall"/>) rather than a value it can drift into: the shared guard
/// takes one of these and has no ambient fallback to resolve from (ticket 205, ledger L592/L600/L671).
/// </summary>
internal readonly record struct RouteRecord
{
    private RouteRecord(string? id, bool addressesTheInstall)
    {
        Id = id;
        AddressesTheInstall = addressesTheInstall;
    }

    /// <summary>The addressed record's id.</summary>
    internal string? Id { get; }

    /// <summary>Whether the act deliberately addresses the install rather than a record.</summary>
    internal bool AddressesTheInstall { get; }

    /// <summary>The act addresses the record <paramref name="id"/> names. A blank id is not "no record" —
    /// it is an unresolvable one, and the guard refuses it fail-closed.</summary>
    internal static RouteRecord Of(string? id) => new(id, addressesTheInstall: false);

    /// <summary>
    /// The act addresses the INSTALL rather than any one record — a list, or a create whose record does not
    /// exist yet. The gate admits this shape only for an operation the definition side declares install-wide
    /// (<c>PermissionVocabulary.InstallWideOperations</c>), never because a call site said so.
    /// </summary>
    internal static RouteRecord TheInstall { get; } = new(null, addressesTheInstall: true);
}

/// <summary>
/// The one point-of-use current-authorization resolution the node's record-scoped route families share
/// (ticket 205 slice 4). Every guard resolves ONE <see cref="AuthorizationGate"/> decision for the act the
/// route performs, against the record that route addresses.
/// </summary>
/// <remarks>
/// <para>
/// It used to be <c>HasPermission(HttpContext, string)</c> over the request-scoped
/// <c>IAuthorizationContext</c>: a bare string carrying neither the record the act touches nor the instant
/// it happens, resolved from whatever ambient context the container held. The signature below is the
/// conversion — a caller cannot reach the gate without naming the record, so every one of the ~36 call
/// sites in the record-scoped families had to state what its act addresses, and a new route cannot silently
/// inherit an ambient answer.
/// </para>
/// <para>
/// The record KIND is not a call-site parameter: it is derived from the operation through
/// <see cref="AuthorizationGate.RecordKindFor"/>, the same reading <c>AuthorizationGate.Validate</c>
/// enforces, so a route supplies only the id and the two cannot disagree.
/// </para>
/// </remarks>
internal static class RequestAuthorization
{
    /// <summary>The server-derived authority for THIS request: the acting PRINCIPAL (the grant subject the
    /// gate decides about - <see cref="NodeGatePrincipal"/>, shared with the production PEP), the tenant,
    /// and the instant the act happens. Never an ambient read.</summary>
    internal static AuthorizationWriteContext Authority(HttpContext http, TenantId tenant, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(time);
        return new AuthorizationWriteContext(NodeGatePrincipal.Resolve(http), tenant, time.GetUtcNow());
    }

    /// <summary>
    /// Resolves <paramref name="permission"/> for <paramref name="record"/> through the gate and returns the
    /// fail-closed refusal when the single decision denies, or <see langword="null"/> when the act may
    /// proceed. The clock and the gate are read from the request's own container.
    /// </summary>
    internal static ValueTask<IResult?> RefusalAsync(
        HttpContext http,
        TenantId tenant,
        string permission,
        RouteRecord record,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var time = http.RequestServices.GetService<TimeProvider>();
        // No kernel clock in this container means there is no instant to date the act with, and there is
        // deliberately no wall-clock fallback: inventing one here would date a decision off the record.
        return time is null
            ? ValueTask.FromResult<IResult?>(Denied(permission))
            : RefusalAsync(http, Authority(http, tenant, time), permission, record, ct);
    }

    /// <summary>
    /// The same resolution against an authority the route has ALREADY built. A write route that stamps its
    /// mutation with an actor and an instant passes that same authority here, so the act is decided on the
    /// very instant it is recorded with — one act, one clock read, one decision (ticket 216's kernel-clock
    /// discipline; <c>KernelClockIntegrationTests</c> counts the reads).
    /// </summary>
    internal static async ValueTask<IResult?> RefusalAsync(
        HttpContext http,
        AuthorizationWriteContext writeAuthority,
        string permission,
        RouteRecord record,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        // A write route hands us the authority it will STAMP its mutation with, whose actor is the acting
        // PARTY. The gate decides about the grant subject, so the principal is resolved here from the same
        // shared helper the production PEP uses; the instant and the tenant are the caller's, untouched, so
        // "one act, one clock read" still holds.
        var authority = writeAuthority with { Principal = NodeGatePrincipal.Resolve(http) };
        var gate = http.RequestServices.GetService<AuthorizationGate>();
        if (gate is null)
        {
            // A container with no gate is not "allowed": the act cannot be decided, so it does not happen.
            return await PreDecidedAsync(http, authority, permission, ct).ConfigureAwait(false);
        }

        var operation = AuthorizationOperation.Parse(permission);

        AuthorizationDecision decision;
        try
        {
            // Two shapes refuse HERE rather than throwing out of the gate as a 500, and both are fail-closed
            // by construction: a record id that cannot become one (it is caller-supplied on a route
            // parameter or a body field, and the gate's own ScopeExpression is what decides that), and a
            // record-LESS check of an operation the definition side never declared install-wide — which the
            // gate refuses by name (L600/L671), and which is a bug in the route rather than in the request.
            if (!record.AddressesTheInstall && string.IsNullOrWhiteSpace(record.Id))
                return await PreDecidedAsync(http, authority, permission, ct).ConfigureAwait(false);
            var request = record.AddressesTheInstall
                ? authority.InstallWide(operation)
                : authority.Request(operation, AuthorizationGate.RecordKindFor(operation), record.Id!);
            decision = await gate.DecideAsync(request, ct).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return await PreDecidedAsync(http, authority, permission, ct).ConfigureAwait(false);
        }

        return decision.Verdict == AuthorizationVerdict.Allowed
            ? null
            : await RefusedAsync(http, decision, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The refusal for an act that never reached a decision, for a caller with no instant to date an audit
    /// row with (no kernel clock in the container). Its words come from the same renderer the decided path
    /// uses (ticket 214), so there is ONE place a refusal becomes text.
    /// </summary>
    internal static IResult Denied(string permission) =>
        Write(AuthorizationRefusalRenderer.PreDecision(permission), permission);

    /// <summary>
    /// The 403 for a refusal a route met as <see cref="AuthorizationDeniedException"/> rather than as a
    /// guard verdict (ticket 214 slice 2). The exception's own message never reaches the wire: the SAME
    /// decision it carries is rendered through <see cref="AuthorizationRefusalRenderer"/> and audited, so a
    /// route family that authorizes inside its store shares the node's one filtered refusal.
    /// </summary>
    internal static ValueTask<IResult> RefusedAsync(
        HttpContext http,
        AuthorizationDeniedException denial,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(denial);
        return RefusedAsync(http, denial.Decision, ct);
    }

    /// <summary>Renders, audits and writes the refusal of <paramref name="decision"/> — the one decision
    /// the caller already made; nothing here re-decides.</summary>
    internal static async ValueTask<IResult> RefusedAsync(
        HttpContext http,
        AuthorizationDecision decision,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(decision);
        var refusal = await AuthorizationRefusalRenderer.RenderAsync(
            decision, [], http.RequestServices.GetService<AuthorizationGate>(), ct).ConfigureAwait(false);
        var request = decision.Request;
        return await RecordedAsync(
            http, refusal, request.Act.Operation.Value,
            request.Principal, request.Tenant, request.At, decision, ct).ConfigureAwait(false);
    }

    /// <summary>The pre-decision refusal, audited against the authority the act carried.</summary>
    private static ValueTask<IResult> PreDecidedAsync(
        HttpContext http,
        AuthorizationWriteContext authority,
        string permission,
        CancellationToken ct) =>
        RecordedAsync(
            http, AuthorizationRefusalRenderer.PreDecision(permission), permission,
            authority.Principal, authority.Tenant, authority.At, decision: null, ct);

    /// <summary>
    /// Writes the refusal's audit row — the classified diagnostic the response withheld — and then the
    /// response. A container with no sink still refuses; the sink cannot change the answer.
    /// </summary>
    private static async ValueTask<IResult> RecordedAsync(
        HttpContext http,
        AuthorizationRefusal refusal,
        string permission,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        AuthorizationDecision? decision,
        CancellationToken ct)
    {
        Guid? auditId = null;
        if (http.RequestServices.GetService<AuthorizationRefusalAudit>() is { } audit)
        {
            auditId = await audit.RecordAsync(refusal, permission, principal, tenant, at, decision, ct)
                .ConfigureAwait(false);
        }

        return Write(refusal, permission, auditId);
    }

    /// <summary>
    /// The single problem-details writer for an authorization refusal. It serializes ONLY what the renderer
    /// decided the acting principal may see; <see cref="AuthorizationRefusal.Diagnostic"/> is deliberately
    /// not written here — it belongs to the audit row.
    /// </summary>
    private static IResult Write(AuthorizationRefusal refusal, string permission, Guid? auditId = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["code"] = refusal.Code,
            ["permission"] = permission,
            ["title"] = refusal.Title,
            ["detail"] = refusal.Detail,
            ["remediation"] = refusal.Remediation,
        };
        // Only an appended receipt extends the refusal's original five-field shape.
        if (auditId is { } recordedId)
            body["auditId"] = recordedId;
        return Results.Json(body, statusCode: StatusCodes.Status403Forbidden);
    }
}
