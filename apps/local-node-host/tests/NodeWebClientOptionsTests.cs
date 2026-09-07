using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests;

/// <summary>
/// Deep-review #1842 (major finding) — the FAIL-CLOSED coupling between the web-client profile and the
/// listener caller-auth gate. The listener middleware (<c>SharedHostedWebApp.UseListenerCallerAuth</c>)
/// FAST-PATHS — permits every route — when no bootstrap session token is configured
/// (<see cref="NodeCallerSessionToken.IsEnforced"/> == <c>false</c>). So a web node enabled WITHOUT
/// <c>LocalNode__SessionToken</c> set would leave every route open and make the browser login screen
/// decorative (server-side there is no gate). <see cref="NodeWebClientOptions.EnsureCallerAuthEnforcedIfEnabled"/>
/// is the startup guard Program.cs calls to refuse that exact misconfiguration; these facts pin it.
/// </summary>
/// <remarks>
/// The guard is driven through the SAME <see cref="NodeCallerSessionToken.IsEnforced"/> predicate the
/// composition root passes (a real token object built from the configured value), so the test exercises
/// the true enabled-web-client × unset-session-token config the reviewer named — not a synthetic bool.
/// </remarks>
public sealed class NodeWebClientOptionsTests
{
    // Mirrors Program.cs: the guard is fed `new NodeCallerSessionToken(sessionToken).IsEnforced`.
    private static bool CallerAuthEnforcedFor(string? sessionToken) =>
        new NodeCallerSessionToken(sessionToken).IsEnforced;

    [Theory(DisplayName = "web-client ENABLED without a session token ⇒ host refuses to start (fail-closed)")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Enabled_Without_SessionToken_Throws(string? unsetSessionToken)
    {
        var options = new NodeWebClientOptions
        {
            Enabled = true,
            // A credential CAN be provisioned yet the node must STILL refuse — the decorative-login
            // risk is about the LISTENER gate being un-enforced, independent of the founder credential.
            FounderUsername = "founder",
            FounderPasswordHash = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHQ$aGFzaGhhc2g",
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => options.EnsureCallerAuthEnforcedIfEnabled(CallerAuthEnforcedFor(unsetSessionToken)));

        // The message must name the cause + the fix honestly (an opaque throw would be a worse gap).
        Assert.Contains("LocalNode__SessionToken", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fail-closed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "web-client ENABLED with a session token configured ⇒ starts (gate is enforced)")]
    public void Enabled_With_SessionToken_DoesNotThrow()
    {
        var options = new NodeWebClientOptions { Enabled = true };

        // A configured (non-blank) token ⇒ IsEnforced true ⇒ the listener gates every non-allowlisted
        // route ⇒ the browser login is a real server-side gate ⇒ no throw.
        options.EnsureCallerAuthEnforcedIfEnabled(
            CallerAuthEnforcedFor("carrier-per-boot-session-token-0123456789abcdef"));
    }

    [Fact(DisplayName = "web-client DISABLED ⇒ never throws, even with no session token (guard is a no-op)")]
    public void Disabled_NeverThrows()
    {
        var options = new NodeWebClientOptions { Enabled = false };

        // The default desktop/dev posture: the profile is off, so an un-enforced gate is fine (there is
        // no browser front door to make decorative). The guard must not gratuitously block that boot.
        options.EnsureCallerAuthEnforcedIfEnabled(CallerAuthEnforcedFor(null));
    }
}
