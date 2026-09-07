namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Configuration for the WEB-CLIENT profile — a browser UI (the Harborline App built
/// <c>--mode webclient</c>) served BY this node over its own origin, authenticated per-user
/// against the node's roster. Bound from <c>LocalNode:WebClient</c>. Disabled by default: the
/// shipped Tauri Harborline App does NOT use any of this (it talks to a sidecar it spawned itself over
/// the per-boot host token). The customer-zero dogfood node turns it ON to give the live node a
/// browser front door (DOGFOOD.md gap #1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Secrets come from the ENVIRONMENT, never the committed config template.</b>
/// <see cref="FounderUsername"/> and <see cref="FounderPasswordHash"/> are provisioned via the
/// service environment (<c>LocalNode__WebClient__FounderUsername</c> /
/// <c>LocalNode__WebClient__FounderPasswordHash</c>) exactly like <c>LocalNode__SessionToken</c> —
/// they are a credential and MUST NOT be committed. The password is stored ONLY as an Argon2id PHC
/// hash (mint it with the node's <c>hash-web-password</c> subcommand); the plaintext never touches
/// config. When either is absent the web login is <b>fail-closed</b> (every login attempt is
/// rejected) and the node logs the un-provisioned state loudly at startup.
/// </para>
/// <para>
/// <b>The non-secret bits</b> (<see cref="Enabled"/>, <see cref="BundleRoot"/>,
/// <see cref="LlmUpstreamBase"/>) live in the committed <c>appsettings.Production.json</c> template.
/// </para>
/// </remarks>
public sealed class NodeWebClientOptions
{
    /// <summary>
    /// Master switch for the web-client profile. When <c>false</c> (default) nothing in this
    /// subsystem is wired — no static bundle hosting, no <c>/api/session/*</c> routes, no LLM proxy,
    /// and the listener caller-auth gate behaves exactly as it did before (bootstrap token only).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The single founder login username. FROM THE ENVIRONMENT (never committed). Null/blank ⇒ web
    /// login is fail-closed (no credential provisioned).
    /// </summary>
    public string? FounderUsername { get; set; }

    /// <summary>
    /// The founder password as an Argon2id PHC hash string
    /// (<c>$argon2id$v=19$m=...$&lt;salt&gt;$&lt;hash&gt;</c>). FROM THE ENVIRONMENT (never committed —
    /// it is a credential). Mint it with <c>Harborline.Api.LocalNodeHost hash-web-password</c>. Null/blank ⇒
    /// web login is fail-closed.
    /// </summary>
    public string? FounderPasswordHash { get; set; }

    /// <summary>
    /// Optional human-friendly label for the session bearer (shown in the client's whoami). Defaults
    /// to the username when unset.
    /// </summary>
    public string? FounderDisplayName { get; set; }

    /// <summary>
    /// Absolute path to the directory holding the built web-client bundle (the Harborline App
    /// <c>dist/</c> from a <c>--mode webclient</c> build). When set + present, the node serves it
    /// as static files at its root over the SAME origin as the API (no CORS surface). When null /
    /// missing, no static hosting is wired (the API is still reachable; there is just no UI in front).
    /// </summary>
    public string? BundleRoot { get; set; }

    /// <summary>
    /// Optional base URL of the OpenAI-compatible LLM upstream the node reverse-proxies for Pilot at
    /// <c>/api/llm/*</c> (e.g. <c>http://127.0.0.1:11434</c> for a co-located Ollama). Same-origin
    /// from the browser's perspective; localhost from the node's. Session-gated (a logged-in user
    /// only). Null ⇒ the proxy is not wired and Pilot degrades to its honest echo fallback.
    /// </summary>
    public string? LlmUpstreamBase { get; set; }

    /// <summary>
    /// The login rate-limit / lockout knobs (S11 — keyed off the <c>Auth.LoginFailed</c> signal).
    /// Non-secret; overridable from the committed template or the environment
    /// (<c>LocalNode:WebClient:Lockout:*</c>). Never null; defaults are safe.
    /// </summary>
    public NodeWebLoginLockoutOptions Lockout { get; set; } = new();

    /// <summary>
    /// Fail-closed startup guard (deep-review #1842, major finding): the web-client profile's per-user
    /// login is only a REAL server-side gate when the listener caller-auth is ENFORCED. The listener
    /// middleware (<c>SharedHostedWebApp.UseListenerCallerAuth</c>) FAST-PATHS — permits EVERY route —
    /// when no bootstrap session token is configured (<c>NodeCallerSessionToken.IsEnforced == false</c>).
    /// So enabling the browser front door WITHOUT <c>LocalNode__SessionToken</c> set would leave every
    /// node route open and make the login screen decorative (server-side there is no gate — client-side
    /// gating is not auth). When <see cref="Enabled"/> is true the caller MUST pass
    /// <paramref name="callerAuthEnforced"/> = <c>true</c>; otherwise this throws and the host refuses to
    /// start, fail-closed, rather than serve an unguarded node. No-op when the profile is disabled.
    /// </summary>
    /// <param name="callerAuthEnforced">
    /// Whether the listener caller-auth gate is enforced — i.e. <c>NodeCallerSessionToken.IsEnforced</c>
    /// (a non-blank <c>LocalNode__SessionToken</c> is configured).
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Enabled"/> is <c>true</c> but caller-auth is not enforced (no session token) — the web
    /// login would be decorative and every route open. The host must not start.
    /// </exception>
    public void EnsureCallerAuthEnforcedIfEnabled(bool callerAuthEnforced)
    {
        if (Enabled && !callerAuthEnforced)
        {
            throw new InvalidOperationException(
                "LocalNode:WebClient:Enabled is true but no caller-auth session token is configured "
                + "(LocalNode__SessionToken is unset). The web-client profile REQUIRES the listener "
                + "caller-auth gate to be ENFORCED — otherwise every node route is open and the browser "
                + "login is decorative (server-side there is no gate; client-side gating is not auth). "
                + "Set LocalNode__SessionToken in the service environment (see DOGFOOD.md) and redeploy. "
                + "Refusing to start fail-closed.");
        }
    }
}
