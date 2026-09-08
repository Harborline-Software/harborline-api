using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// Hosted service that maps the web-client session routes (<c>/api/session/*</c>) onto the shared
/// Kestrel listener. A thin mapper (mirrors <c>HostedContactApiEndpoint</c>): the authority is
/// resolved from the composition root and passed closed-over to <see cref="WebSessionRoutes.Map"/>.
/// Mapped only when <c>LocalNode:WebClient:Enabled</c>. The effective-permissions
/// read is registered separately in every profile by <see cref="HostedEffectivePermissionsApiEndpoint"/>.
/// </summary>
internal sealed class HostedWebSessionApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly INodeWebSessionAuthority _authority;
    // S11 — the login rate limiter / lockout the login route enforces (keyed off Auth.LoginFailed).
    private readonly WebLoginRateLimiter _loginRateLimiter;
    private readonly IWebAccountAccessChallengeIssuer _challengeIssuer;
    private readonly IWebTenantSelectionAuthority _tenantSelection;
    private readonly IWebTenantSwitchAuthority _tenantSwitch;
    private readonly IWebAntiforgeryPolicy _antiforgery;
    private readonly IWebSelectedSessionLogoutAuthority _logout;
    private readonly IWebFounderBindAuthority _founderBind;
    private readonly IAccountSetupAcceptanceAuthority _acceptance;
    private readonly Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemRateLimiter _redeemRateLimiter;
    // #3338 / #3366 — the node-side password->credential mint the acceptance AND recovery routes need.
    // The artifact carries installation-local cost parameters and a padded-Base64 canonical encoding
    // standard PHC does not emit, so a browser cannot produce one; see IWebChosenCredentialFactory for
    // the whole reasoning.
    private readonly IWebChosenCredentialFactory _chosenCredentials;
    private readonly IAccountRecoveryAuthority _recovery;
    private readonly IAdminTeamAccessAuthority _adminTeamAccess;
    private readonly IWebSelectedSessionIdentityAuthority _selectedIdentity;
    // #3167 — the "connect your device" pairing-token mint + the home node's roster (for the team anchor).
    private readonly WebAdmittedMemberPairingTokenMint _pairingMint;
    private readonly Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster _roster;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedWebSessionApiEndpoint> _logger;

    /// <summary>Constructs the hosted web-client session API endpoint.</summary>
    public HostedWebSessionApiEndpoint(
        SharedHostedWebApp sharedApp,
        INodeWebSessionAuthority authority,
        WebLoginRateLimiter loginRateLimiter,
        IWebAccountAccessChallengeIssuer challengeIssuer,
        IWebTenantSelectionAuthority tenantSelection,
        IWebTenantSwitchAuthority tenantSwitch,
        IWebAntiforgeryPolicy antiforgery,
        IWebSelectedSessionLogoutAuthority logout,
        IWebFounderBindAuthority founderBind,
        IAccountSetupAcceptanceAuthority acceptance,
        Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemRateLimiter redeemRateLimiter,
        IWebChosenCredentialFactory chosenCredentials,
        IAccountRecoveryAuthority recovery,
        IAdminTeamAccessAuthority adminTeamAccess,
        IWebSelectedSessionIdentityAuthority selectedIdentity,
        WebAdmittedMemberPairingTokenMint pairingMint,
        Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster roster,
        TimeProvider timeProvider,
        ILogger<HostedWebSessionApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(loginRateLimiter);
        ArgumentNullException.ThrowIfNull(challengeIssuer);
        ArgumentNullException.ThrowIfNull(tenantSelection);
        ArgumentNullException.ThrowIfNull(tenantSwitch);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(logout);
        ArgumentNullException.ThrowIfNull(founderBind);
        ArgumentNullException.ThrowIfNull(acceptance);
        ArgumentNullException.ThrowIfNull(redeemRateLimiter);
        ArgumentNullException.ThrowIfNull(chosenCredentials);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(adminTeamAccess);
        ArgumentNullException.ThrowIfNull(selectedIdentity);
        ArgumentNullException.ThrowIfNull(pairingMint);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _authority = authority;
        _loginRateLimiter = loginRateLimiter;
        _challengeIssuer = challengeIssuer;
        _tenantSelection = tenantSelection;
        _tenantSwitch = tenantSwitch;
        _antiforgery = antiforgery;
        _logout = logout;
        _founderBind = founderBind;
        _acceptance = acceptance;
        _redeemRateLimiter = redeemRateLimiter;
        _chosenCredentials = chosenCredentials;
        _recovery = recovery;
        _adminTeamAccess = adminTeamAccess;
        _selectedIdentity = selectedIdentity;
        _pairingMint = pairingMint;
        _roster = roster;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(MapRoutes);
        var listenerPostureRoutes = new (string Method, string Path)[]
        {
            ("GET", AntiforgeryRoutes.IssuePath),
            ("POST", WebSessionRoutes.LoginPath),
            ("POST", AccountChallengeRoutes.IssuePath),
            ("POST", AccountSetupAcceptRoutes.AcceptPath),
            ("POST", TenantSelectionRoutes.SelectPath),
            ("POST", TenantSwitchRoutes.SwitchPath),
            ("POST", SessionLogoutRoutes.LogoutPath),
        };
        var allowlistedRoutes = FormatRoutes(listenerPostureRoutes.Where(
            route => NodeListenerCallerAuthPolicy.IsAllowlisted(route.Path)));
        var callerAuthenticatedRoutes = FormatRoutes(listenerPostureRoutes.Where(
            route => !NodeListenerCallerAuthPolicy.IsAllowlisted(route.Path)));
        _logger.LogInformation(
            "Web-client session API registered ({AllowlistedRoutes} " +
            "[listener allowlisted; handler authenticated]; {CallerAuthenticatedRoutes} " +
            "[listener caller authenticated; handler authenticated]; GET {Me}; " +
            // The fenced bracket qualifies FounderBind ALONE. Whoami used to sit inside it, which
            // made the node's own description of its auth surface wrong: FounderBindRoutes.Map
            // installs the desktop-plane fence, SelectedSessionIdentityRoutes.Map installs none,
            // and whoami is emphatically reachable by the selected web session — as the comment
            // above this method's route block already says.
            "GET {Whoami}; POST {FounderBind} " +
            "[DESKTOP-PLANE FENCED — card #3490; reachable by no caller today]).",
            allowlistedRoutes,
            callerAuthenticatedRoutes,
            WebSessionRoutes.MePath,
            SelectedSessionIdentityRoutes.WhoamiPath,
            FounderBindRoutes.BindPath);
        return Task.CompletedTask;
    }

    internal void MapRoutes(IEndpointRouteBuilder app)
    {
        var preAuth = app.MapPreAuthOperationalGroup();
        var selectedSession = app.MapSelectedSessionProductGroup();

        WebSessionRoutes.Map(preAuth, selectedSession, _authority, _loginRateLimiter);
        AntiforgeryRoutes.Map(preAuth, _antiforgery);
        AccountChallengeRoutes.Map(preAuth, _challengeIssuer, _antiforgery, _loginRateLimiter);
        AccountSetupAcceptRoutes.Map(
            preAuth,
            _acceptance,
            _chosenCredentials,
            _antiforgery,
            _redeemRateLimiter);
        RecoveryAcceptRoutes.Map(
            preAuth,
            _recovery,
            _chosenCredentials,
            _antiforgery,
            _redeemRateLimiter);
        TenantSelectionRoutes.Map(preAuth, _tenantSelection, _antiforgery);
        TenantSwitchRoutes.Map(selectedSession, _tenantSwitch, _antiforgery);
        // Audience-dispatching sign-out (#3343): the selected-session authority AND the legacy
        // web-session authority, so a v1 founder holding only __Host-web_session can sign out.
        SessionLogoutRoutes.Map(preAuth, _logout, _authority, _antiforgery);
        // Founder-bind is DESKTOP-PLANE ONLY (card #3490). The fence is installed inside
        // FounderBindRoutes.Map, not here, so it cannot be lifted by editing this call site.
        // Only that route moves — select, switch, logout, admin and whoami stay on the web plane.
        FounderBindRoutes.Map(app, _founderBind, _antiforgery);
        AdminTeamAccessRoutes.Map(selectedSession, _adminTeamAccess, _antiforgery, _timeProvider);
        // #3329 step 1 — the selected audience's own whoami. Distinct from WebSessionRoutes'
        // v1 `me`: that one describes the audience R3-H rejects at the cutover and carries no
        // account id.
        SelectedSessionIdentityRoutes.Map(selectedSession, _selectedIdentity);
        // #3167 — the authenticated "connect your device" pairing-token mint (selected-session gated, like
        // founder-bind). Mints a single-use device-pairing token bound to the member's session-derived pins.
        ConnectDeviceRoutes.Map(selectedSession, _pairingMint, _roster, _antiforgery);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string FormatRoutes(IEnumerable<(string Method, string Path)> routes) =>
        string.Join(", ", routes.Select(route => $"{route.Method} {route.Path}"));
}
