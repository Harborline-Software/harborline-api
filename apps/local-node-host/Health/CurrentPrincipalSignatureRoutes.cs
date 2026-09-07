using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0134 P1b-2 / P2 — the canonical-node-key principal SIGNING route on the
/// shared Kestrel listener. Resolves the host's current OS-user principal
/// host-side, signs it with the node's canonical Ed25519 identity (the
/// <c>RootSeedHex</c>→keypair) via the cross-language
/// <see cref="CanonicalJson.SerializeSignable"/> form, and returns the
/// signed-principal envelope a TS/remote verifier
/// (<c>apps/capability-host/src/membrane/signed-principal.ts</c> <c>verifySignedPrincipal</c>)
/// accepts with the node public key pinned as the trusted issuer.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this RESOLVES.</b> The P2 "node-key reachability" flag asked whether the
/// canonical .NET node signing key (which is NOT on the Harborline App invoke path —
/// <c>renderer → capability.rs → capability-invoker.mjs → @harborline-software/capability-host PEP</c>) can reach a
/// principal signer at all. The ratified answer: a <b>bounded loopback signing route
/// on the local-node-host</b>. The private key STAYS in the .NET host — it is NEVER
/// copied to the Harborline App, the renderer, or the TS layer. The Harborline App CALLS this route
/// (inc-4 wiring); the dev <c>NodeSigningKey.fromSeedHex</c> path in
/// <c>host-principal-signer.ts</c> becomes the dev/test-only fallback.
/// </para>
/// <para>
/// <b>Bounded — it is NOT a signing oracle.</b> The route signs <i>ONLY</i> the
/// host-resolved current OS-user principal (<see cref="ResolveCurrentPrincipal"/>:
/// <c>Environment.UserName</c> → <c>os:&lt;user&gt;</c>, <c>kind: local-os-user</c>).
/// There is NO request body and no query parameter that selects WHAT gets signed — a
/// caller cannot ask the node to sign an arbitrary principal of its choosing. This is
/// the load-bearing scope-fence: the node attests "the local OS user is X", it does not
/// offer "sign anything for me".
/// </para>
/// <para>
/// <b>Security posture.</b>
/// <list type="bullet">
///   <item><b>Loopback only.</b> The shared Kestrel listener binds
///     <c>127.0.0.1</c> (see <c>SharedHostedWebApp.ConfigureUrls</c>); the route is not
///     reachable off-box.</item>
///   <item><b>Bounded principal.</b> Host-resolved only (above) — no signing oracle.</item>
///   <item><b>No inline audit side effect.</b> Per the financial-cluster audit-envelope
///     durable-layer convention, this read-only signing route emits NO inline
///     signed-event / audit row — it derives a signature over volatile identity, it does
///     not mutate any durable store.</item>
///   <item><b>INC-4 REQUIREMENT — cross-process caller auth.</b> Loopback-bind alone does
///     NOT authenticate the CALLER: any local process can currently reach a loopback
///     port. inc-4 (the Harborline App↔node connection) MUST gate this route behind a
///     session token the Harborline App holds (a per-boot shared secret the Tauri shell and the
///     spawned node agree on, presented as a header / bearer that the route validates).
///     Until inc-4 wires that token check, treat this route as
///     <b>dev/single-host-trusted only</b> — it is NOT yet an authenticated cross-process
///     signing surface. See the <c>INC-4</c> TODO in <see cref="Map"/>.</item>
/// </list>
/// </para>
/// </remarks>
public static class CurrentPrincipalSignatureRoutes
{
    /// <summary>The route path. Loopback-bound on the shared listener.</summary>
    public const string Route = "/api/local-node/current-principal-signature";

    /// <summary>
    /// The signed PAYLOAD — the principal being attested. Mirrors the TS
    /// <c>MembranePrincipal</c> (<c>apps/capability-host/src/membrane/pep.ts</c>) EXACTLY:
    /// <c>{ id, displayName?, kind? }</c>, camelCase, optional fields omitted when null.
    /// The property casing + null-omission are what make the canonical signable bytes
    /// byte-identical to the TS <c>serializeSignablePrincipal</c> payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The JSON property names are LOAD-BEARING.</b> <see cref="CanonicalJson"/> serializes
    /// the payload via <c>JsonSerializer</c> with NO naming policy, so a CLR property
    /// <c>Id</c> would canonicalize to the key <c>"Id"</c> (PascalCase) — diverging from the
    /// TS payload key <c>"id"</c> and breaking the cross-language signature. The explicit
    /// <see cref="JsonPropertyNameAttribute"/>s pin the camelCase keys independent of any
    /// serializer-options policy. <see cref="JsonIgnoreCondition.WhenWritingNull"/> omits a
    /// null optional, matching how the TS side OMITS an undefined <c>displayName</c>/<c>kind</c>.
    /// </para>
    /// </remarks>
    /// <param name="Id">Stable identity, namespaced by source — <c>os:&lt;user&gt;</c>.</param>
    /// <param name="DisplayName">Human-readable name (the OS username today). Omitted when null.</param>
    /// <param name="Kind">The identity SOURCE — <c>local-os-user</c> today. Omitted when null.</param>
    public sealed record SignedPrincipalPayload(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("displayName")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
        [property: JsonPropertyName("kind")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind);

    /// <summary>
    /// Resolves the host's CURRENT OS-user principal — host-side, from the process
    /// environment, NOT from any caller-supplied input. Mirrors the Harborline App
    /// <c>principal.rs</c> <c>resolve_principal</c> + the SDK <c>currentOsPrincipal()</c>
    /// so all three sources build an identical principal: <c>os:&lt;user&gt;</c> /
    /// <c>local-os-user</c>, with a blank username falling back to <c>os:unknown</c> so the
    /// attested principal is ALWAYS non-anonymous.
    /// </summary>
    public static SignedPrincipalPayload ResolveCurrentPrincipal() =>
        ResolveCurrentPrincipal(Environment.UserName);

    /// <summary>
    /// The derivation itself, over an explicit raw OS-user name — the seam the tests drive
    /// (the process OS account name cannot be set in-process).
    /// </summary>
    internal static SignedPrincipalPayload ResolveCurrentPrincipal(string? raw)
    {
        var username = string.IsNullOrWhiteSpace(raw) ? "unknown" : raw.Trim();
        return new SignedPrincipalPayload(
            // MINT, not wrap: this is a derivation from a shell value (ticket 274/296), so the OS
            // account name is canonicalised here. Consumers wrap the result in ActorId, which refuses
            // a decomposed spelling — a non-form-C account name would 500 the KG search route.
            Id: ActorId.Mint($"os:{username}").Value,
            DisplayName: username,
            Kind: "local-os-user");
    }

    /// <summary>
    /// Maps <c>GET <see cref="Route"/></c> on <paramref name="routes"/>, closing over the
    /// node's canonical signing <paramref name="signer"/> + its <paramref name="nodePublicKey"/>.
    /// The same registration the production hosted endpoint and the route tests both call —
    /// one source of truth for the wire contract.
    /// </summary>
    /// <param name="routes">The endpoint route builder (the shared <c>WebApplication</c>).</param>
    /// <param name="signer">
    /// The node's canonical Ed25519 signer — built from the host's <c>RootSeedHex</c>→keypair.
    /// Its <see cref="IOperationSigner.IssuerId"/> IS the node public key the verifier pins.
    /// </param>
    /// <param name="nodePublicKey">
    /// The node public key (base64url of the raw 32 pubkey bytes — the verifier's trust
    /// anchor). Equals <c>signer.IssuerId.ToBase64Url()</c>; passed explicitly so the wire
    /// shape is unambiguous.
    /// </param>
    public static void Map(
        IEndpointRouteBuilder routes,
        IOperationSigner signer,
        string nodePublicKey,
        TimeProvider timeProvider)
        => Map(routes, signer, nodePublicKey, new NodeCallerSessionToken(null), timeProvider);

    /// <summary>
    /// Maps <c>GET <see cref="Route"/></c> with the inc-4 cross-process CALLER-AUTH guard
    /// (<paramref name="callerAuth"/>) enforced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>inc-4 — the <c>:138</c> TODO RESOLVED.</b> A loopback bind authenticates the
    /// host, not the calling process — so this signing route (a node-key attestation
    /// surface) is now gated behind a per-boot session token the Tauri shell injected
    /// into the spawned node + the Harborline App presents (<c>Authorization: Bearer</c>). A
    /// local stranger process WITHOUT the token is rejected <b>fail-closed (401)</b>
    /// BEFORE the principal is signed — the node never attests for an unauthenticated
    /// caller. When no token is configured (dev / single-host / Bridge-tenant) the guard
    /// permits the call (dev/single-host-trusted), preserving the existing 3-arg
    /// behaviour. See <see cref="NodeCallerSessionToken"/>.
    /// </para>
    /// </remarks>
    public static void Map(
        IEndpointRouteBuilder routes,
        IOperationSigner signer,
        string nodePublicKey,
        NodeCallerSessionToken callerAuth,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodePublicKey);
        ArgumentNullException.ThrowIfNull(callerAuth);

        // GET /api/local-node/current-principal-signature
        routes.MapGet(Route, async (HttpRequest httpRequest, CancellationToken ct) =>
        {
            // inc-4 CALLER AUTH (fail-closed): a loopback bind authenticates the host, not
            // the calling process. A stranger local process without the Harborline App's per-boot
            // session token is rejected 401 — BEFORE the node signs anything. (Un-enforced
            // in dev/single-host mode where no token is configured.)
            // inc-4 F1: the AUTHORITATIVE gate is now the LISTENER-LEVEL middleware in
            // SharedHostedWebApp (gate-all-by-default); this per-route check is retained as
            // defence-in-depth (and keeps this Map() self-contained for the route tests that
            // host it without the shared listener).
            if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
            {
                return NodeCallerSessionToken.RejectResult();
            }

            // BOUNDED: sign ONLY the host-resolved current principal. There is no request
            // body / query selecting the principal — this is not a signing oracle.
            var principal = ResolveCurrentPrincipal();

            // Sign over the cross-language canonical form (CanonicalJson.SerializeSignable,
            // #1254): {issuedAt, issuerId, nonce, payload} with the node identity. A fresh
            // nonce + now-stamped issuedAt give the verifier its replay window + seen-nonce
            // defence.
            var issuedAt = timeProvider.GetUtcNow();
            var nonce = Guid.NewGuid();
            var op = await signer.SignAsync(principal, issuedAt, nonce, ct).ConfigureAwait(false);

            // camelCase wire envelope matching signed-principal.ts SignedPrincipalEnvelope:
            //   { principal, issuerId, issuedAt(epoch-ms int), nonce(uuid), signature(b64url) }
            // plus nodePublicKey so the verifier can pin the trusted issuer. (issuerId and
            // nodePublicKey are the same value; nodePublicKey is named for the trust-anchor
            // role.)
            return Results.Ok(new SignedPrincipalEnvelopeDto(
                Principal: principal,
                IssuerId: op.IssuerId.ToBase64Url(),
                IssuedAt: op.IssuedAt.ToUnixTimeMilliseconds(),
                Nonce: op.Nonce.ToString("D"),
                Signature: op.Signature.ToBase64Url(),
                NodePublicKey: nodePublicKey));
        });
    }

    /// <summary>
    /// The wire DTO — the signed-principal envelope the route returns. camelCase
    /// (default ASP.NET Core <c>JsonSerializerOptions</c> applies <c>camelCase</c>) so it
    /// matches the TS <c>SignedPrincipalEnvelope</c> shape field-for-field, plus
    /// <c>nodePublicKey</c> for the verifier's trusted-issuer pin.
    /// </summary>
    public sealed record SignedPrincipalEnvelopeDto(
        SignedPrincipalPayload Principal,
        string IssuerId,
        long IssuedAt,
        string Nonce,
        string Signature,
        string NodePublicKey);
}
