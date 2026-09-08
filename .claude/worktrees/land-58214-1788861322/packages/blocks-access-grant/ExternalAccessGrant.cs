using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Macaroons;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// The external / cross-Account access grant (ADR 0117 Phase-2 amendment A2 — the <i>external principal</i>
/// realization; the security-engineering deep-review out-of-scope A2-external follow-on). An external
/// principal (a cross-Account auditor, a customer-portal user) is NOT a member of the granting tenant and
/// MUST NOT receive a tenant role or DEK membership. Instead the granting tenant <b>mints a narrowly-caveated
/// macaroon</b> over the ADR 0117 D3 / ADR 0119 external-tier substrate; the grantee presents that macaroon,
/// and access is authorized for <b>exactly the caveated scope, relay-only, OUTSIDE the tenant DEK</b> — never
/// the role-membership path the internal grant uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the cross-<i>Account</i> analog of D3's cross-<i>app</i> bridge — the SAME mechanism.</b> It
/// composes the shipped macaroon substrate (<c>Harborline.Api.Foundation.Macaroons</c>: mint requires the issuer's
/// root key, attenuation narrows without it, verification checks the HMAC chain + every first-party caveat,
/// failing closed). The grant carries no inline crypto of its own — it is a thin policy layer over the
/// audited macaroon issuer/verifier. The internal-member grant (<see cref="AccessGrant"/>) feeds the ADR 0077
/// resolver; this external grant is a parallel, role-free, DEK-free capability bridge.
/// </para>
/// <para>
/// <b>Outside the tenant DEK (A2 — relay-only).</b> Minting a macaroon and verifying its caveats touches no
/// DEK and confers no tenant membership: the external grantee can present a valid macaroon for the caveated
/// scope and nothing else. The granting tenant stays sovereign — it can revoke by rotating the external-tier
/// root key (every outstanding macaroon at that location fails verification), the cross-Account revocation
/// A4 names.
/// </para>
/// <para>
/// <b>Time-bounded (expires at <see cref="GrantValidity.ValidTo"/>).</b> The mint encodes a
/// <c>time &lt;= "&lt;until&gt;"</c> first-party caveat, so verification rejects a presentation after the
/// validity window closes — enforced by the macaroon verifier's clock-bearing context, not by a separate
/// expiry check. An open-ended external grant is refused at mint: an external capability MUST be time-bounded.
/// </para>
/// </remarks>
/// <param name="GrantId">Stable identifier for this external grant (shares the internal grant id type).</param>
/// <param name="TenantId">The granting tenant whose data the macaroon authorizes a scoped read of (the sovereign / isolation boundary).</param>
/// <param name="GranteeSubjectUri">
/// The external principal's subject URI (e.g. <c>urn:harborline:account:acme:auditor:alice</c>) — bound into
/// the macaroon as a <c>subject == "&lt;uri&gt;"</c> caveat so the credential is not bearer-transferable to
/// another principal.
/// </param>
/// <param name="Scope">The record/schema filter the grant authorizes — encoded as a resource-schema caveat (A2 caveated scope).</param>
/// <param name="Actions">The actions the grant authorizes (e.g. <c>read</c>) — encoded as an <c>action in [...]</c> caveat.</param>
/// <param name="Validity">The validity window; <see cref="GrantValidity.ValidTo"/> MUST be present (external capabilities are time-bounded).</param>
/// <param name="GrantedBy">The principal that minted the external grant (the issuer authority per A3).</param>
/// <param name="GrantedAt">Wall-clock instant the external grant was minted.</param>
public sealed record ExternalAccessGrant(
    GrantId GrantId,
    TenantId TenantId,
    string GranteeSubjectUri,
    ScopeExpression Scope,
    IReadOnlyList<string> Actions,
    GrantValidity Validity,
    ActorId GrantedBy,
    DateTimeOffset GrantedAt);

/// <summary>
/// Mints + authorizes external / cross-Account access grants (ADR 0117 amendment A2). Minting produces a
/// caveated macaroon over the external-tier root key; authorizing verifies a presented macaroon against the
/// request context, granting ONLY the caveated scope and rejecting after the validity window. The location
/// (the macaroon's root-key address) is the external-tier address for the granting tenant.
/// </summary>
/// <remarks>
/// <b>Search-first — reuse, don't reinvent.</b> All cryptography lives in the audited
/// <c>Harborline.Api.Foundation.Macaroons</c> substrate (the issuer's <see cref="IMacaroonIssuer.MintAsync"/> +
/// the verifier's <see cref="IMacaroonVerifier.VerifyAsync"/> + the first-party caveat grammar). This minter
/// only assembles caveats from the grant fields and interprets the verification result — it never computes a
/// signature or evaluates a caveat itself.
/// </remarks>
public sealed class ExternalGrantMinter
{
    private readonly IMacaroonIssuer _issuer;
    private readonly IMacaroonVerifier _verifier;
    private readonly Func<TenantId, string> _locationFor;

    /// <summary>
    /// Constructs the minter over the macaroon issuer + verifier and a function that maps a granting tenant to
    /// its external-tier macaroon location (the root-key address; a root key MUST be registered there for the
    /// granting tenant before minting).
    /// </summary>
    /// <param name="issuer">The macaroon issuer (mints over the external-tier root key).</param>
    /// <param name="verifier">The macaroon verifier (checks the HMAC chain + caveats, fail-closed).</param>
    /// <param name="locationFor">Maps a granting tenant to its external-tier macaroon location.</param>
    public ExternalGrantMinter(
        IMacaroonIssuer issuer,
        IMacaroonVerifier verifier,
        Func<TenantId, string> locationFor)
    {
        _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _locationFor = locationFor ?? throw new ArgumentNullException(nameof(locationFor));
    }

    /// <summary>
    /// Mints the caveated macaroon for <paramref name="grant"/>. The macaroon carries first-party caveats
    /// binding it to the grantee subject, the scope's resource schema, the authorized actions, and the
    /// validity deadline — so verification authorizes ONLY the caveated scope and rejects after
    /// <see cref="GrantValidity.ValidTo"/>. Refuses an open-ended grant (external capabilities are time-bounded).
    /// </summary>
    /// <param name="grant">The external grant to mint a credential for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The minted, caveated macaroon the external grantee presents.</returns>
    /// <exception cref="ArgumentException">When the grant has no <see cref="GrantValidity.ValidTo"/> (not time-bounded).</exception>
    public async ValueTask<Macaroon> MintAsync(ExternalAccessGrant grant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (grant.Validity.ValidTo is not { } until)
        {
            throw new ArgumentException(
                "An external / cross-Account grant MUST be time-bounded (Validity.Until is required); an "
                + "open-ended external capability is refused (ADR 0117 amendment A2).",
                nameof(grant));
        }

        var location = _locationFor(grant.TenantId);
        // The macaroon identifier ties the credential back to the grant id (for the issuer to trace / revoke).
        var identifier = grant.GrantId.ToString();
        var caveats = BuildCaveats(grant, until);

        return await _issuer.MintAsync(location, identifier, caveats, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Authorizes a presented <paramref name="macaroon"/> for the external grantee at the time + request in
    /// <paramref name="presentation"/>. Returns an <see cref="ExternalGrantDecision"/> — <c>Authorized</c> only
    /// when the macaroon's chain is authentic AND every caveat holds (subject match, in-scope schema,
    /// permitted action, within the validity deadline). Any other outcome is a fail-closed denial, INCLUDING a
    /// presentation after <see cref="GrantValidity.ValidTo"/> (the time caveat fails).
    /// </summary>
    /// <param name="macaroon">The macaroon the external grantee presented.</param>
    /// <param name="presentation">The request context the caveats are evaluated against.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<ExternalGrantDecision> AuthorizeAsync(
        Macaroon macaroon,
        ExternalGrantPresentation presentation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(macaroon);
        ArgumentNullException.ThrowIfNull(presentation);

        var context = new MacaroonContext(
            Now: presentation.Now,
            SubjectUri: presentation.SubjectUri,
            ResourceSchema: presentation.ResourceSchema,
            RequestedAction: presentation.RequestedAction,
            DeviceIp: presentation.DeviceIp);

        var result = await _verifier.VerifyAsync(macaroon, context, ct).ConfigureAwait(false);
        return result.IsValid
            ? ExternalGrantDecision.Authorized
            : ExternalGrantDecision.Denied(result.Reason ?? "Macaroon verification failed.");
    }

    /// <summary>
    /// Builds the first-party caveats that narrow the macaroon to the grant's caveated scope. Each uses the
    /// shipped <c>FirstPartyCaveatParser</c> grammar; together they make the credential authorize exactly the
    /// grant — and no wider.
    /// </summary>
    private static IReadOnlyList<Caveat> BuildCaveats(ExternalAccessGrant grant, DateTimeOffset until)
    {
        var caveats = new List<Caveat>
        {
            // Bind to the external subject — the credential is not transferable to another principal.
            new($"subject == \"{grant.GranteeSubjectUri}\""),
            // Time-bound — verification rejects a presentation after `until` (the expiry the grant promises).
            new($"time <= \"{until.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}\""),
        };

        // Caveated scope: a record-scoped grant narrows the resource schema; a whole-tenant external grant
        // omits the schema caveat (it is already root-key + subject + time + action bounded). The scope's
        // record ids are expressed as a schema-glob the relying party matches the requested resource against.
        if (grant.Scope.Value != "/")
        {
            // Record-scoped: pin each reachable record as an explicit schema. v1 uses the first record id as
            // the schema-glob anchor; a multi-record external grant mints one macaroon per record (the issuer
            // attenuates), keeping each credential single-scope and independently revocable.
            var schema = grant.Scope.Value.StartsWith("/records/", StringComparison.Ordinal)
                ? grant.Scope.Value[9..]
                : grant.Scope.Value;
            if (!string.IsNullOrEmpty(schema))
            {
                caveats.Add(new($"resource.schema matches \"{schema}\""));
            }
        }

        // Caveated actions — the credential authorizes only the named actions (e.g. read).
        if (grant.Actions.Count > 0)
        {
            var quoted = string.Join(", ", grant.Actions.Select(a => $"\"{a}\""));
            caveats.Add(new($"action in [{quoted}]"));
        }

        return caveats;
    }
}

/// <summary>
/// The request context an external grantee presents alongside its macaroon. Mirrors the
/// <see cref="MacaroonContext"/> fields the caveats are evaluated against; unset fields cause the
/// caveats that require them to fail closed.
/// </summary>
/// <param name="Now">The instant to evaluate the time caveat against (the relying party's clock).</param>
/// <param name="SubjectUri">The authenticated external subject making the request (checked against the subject caveat).</param>
/// <param name="ResourceSchema">The resource schema being accessed (checked against the scope caveat).</param>
/// <param name="RequestedAction">The action being attempted (checked against the action caveat).</param>
/// <param name="DeviceIp">The requesting device IP, if any (for an optional CIDR caveat — unused in v1).</param>
public sealed record ExternalGrantPresentation(
    DateTimeOffset Now,
    string? SubjectUri,
    string? ResourceSchema,
    string? RequestedAction,
    string? DeviceIp = null);

/// <summary>The outcome of an <see cref="ExternalGrantMinter.AuthorizeAsync"/> check.</summary>
public sealed record ExternalGrantDecision
{
    private ExternalGrantDecision(bool isAuthorized, string? reason)
    {
        IsAuthorized = isAuthorized;
        Reason = reason;
    }

    /// <summary>True when the presented macaroon authorizes the request (chain authentic + every caveat holds).</summary>
    public bool IsAuthorized { get; }

    /// <summary>Diagnostic denial reason when not authorized; null when authorized. Not for verbatim leak to the caller.</summary>
    public string? Reason { get; }

    /// <summary>The authorized singleton.</summary>
    public static ExternalGrantDecision Authorized { get; } = new(isAuthorized: true, null);

    /// <summary>A fail-closed denial with a diagnostic reason.</summary>
    public static ExternalGrantDecision Denied(string reason) => new(isAuthorized: false, reason);
}
