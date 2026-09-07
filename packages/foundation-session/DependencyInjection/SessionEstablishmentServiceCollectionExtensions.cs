using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.Foundation.Session.DependencyInjection;

/// <summary>
/// DI helper for the ADR 0099 session-establishment substrate. Registers ONLY the
/// session-specific services — <see cref="ISessionEstablisher"/>, <see cref="ISessionStore"/>
/// (in-memory v1), <see cref="SessionOptions"/>, and <see cref="TimeProvider"/>. It does NOT
/// register the <c>Foundation.Authorization</c> facade (ADR 0099 A7): the production composition
/// root registers <c>SessionBackedTenantContext</c> separately via the existing
/// <c>AddHarborlineTenantContext&lt;TConcrete&gt;()</c> helper so the ADR-0091 same-instance
/// assertion stays in force. The two helpers compose; neither owns the other's surface.
/// </summary>
public static class SessionEstablishmentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the session-establishment substrate (ADR 0099 PR-2):
    /// <list type="bullet">
    ///   <item><see cref="ISessionStore"/> → <see cref="InMemorySessionStore"/> as a SINGLETON
    ///   (the in-memory dictionary IS the shared store; single-instance-only per O-5).</item>
    ///   <item><see cref="ISessionEstablisher"/> → <see cref="SessionEstablisher"/> as SCOPED
    ///   (it consumes the per-request <c>HttpContext</c> via <c>SignInAsync</c>).</item>
    ///   <item><see cref="TimeProvider.System"/> if no <see cref="TimeProvider"/> is registered
    ///   (tests may register a fake first; this uses <c>TryAddSingleton</c>).</item>
    ///   <item><see cref="SessionOptions"/> configured + validated (idle ≤ absolute, entropy
    ///   floor; ADR 0099 S4/S6) at registration so a misconfiguration fails fast.</item>
    /// </list>
    /// Does NOT wire <c>AddAuthentication().AddCookie()</c>, the middleware ordinal, the
    /// production-guard, or the CSRF endpoint group — those are the Bridge composition root's
    /// job (deferred to the Bridge PR per ADR 0099 PR-2 / H6).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Optional <see cref="SessionOptions"/> configuration (TTL + entropy). Defaults: 8h
    /// absolute / 30min idle / 32-byte (256-bit) ids.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown at registration if the configured options violate the substrate floor
    /// (<see cref="SessionOptions.Validate"/>).
    /// </exception>
    public static IServiceCollection AddHarborlineSessionEstablishment(
        this IServiceCollection services,
        Action<SessionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SessionOptions();
        configure?.Invoke(options);
        // Fail fast at registration rather than silently weakening the session floor.
        options.Validate();

        services.AddOptions<SessionOptions>().Configure(opts =>
        {
            opts.AbsoluteLifetime = options.AbsoluteLifetime;
            opts.IdleTimeout = options.IdleTimeout;
            opts.SessionIdByteLength = options.SessionIdByteLength;
        });

        services.TryAddSingleton<ISessionStore, InMemorySessionStore>();
        services.TryAddScoped<ISessionEstablisher, SessionEstablisher>();

        return services;
    }
}
