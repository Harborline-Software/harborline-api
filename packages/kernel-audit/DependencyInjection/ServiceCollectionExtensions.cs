using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.Kernel.Audit.DependencyInjection;

/// <summary>
/// DI extensions for the Harborline kernel audit-trail subsystem (ADR 0049 +
/// ADR 0094).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the audit subsystem — <see cref="IAuditTrail"/> and
    /// <see cref="IAuditEventStream"/> — as singletons. Direct parallel to
    /// <c>AddHarborlineKernelLedger</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Depends on <c>IEventLog</c> (from <c>Harborline.Api.Kernel.EventBus</c>) and
    /// <c>IOperationVerifier</c> (from <c>Harborline.Api.Foundation.Crypto</c>) being
    /// registered by the caller. Typical composition:
    /// </para>
    /// <code>
    /// services
    ///     .AddHarborlineEventLog(o => o.RootDirectory = "./data/events")
    ///     .AddHarborlineKernelAudit();
    /// </code>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddHarborlineKernelAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The trail publishes directly to the concrete in-memory stream to
        // keep replay deterministic; register the concrete first, then expose
        // it via the IAuditEventStream interface for consumers.
        services.TryAddSingleton<InMemoryAuditEventStream>();
        services.TryAddSingleton<IAuditEventStream>(sp =>
            sp.GetRequiredService<InMemoryAuditEventStream>());

        services.TryAddSingleton<EventLogBackedAuditTrail>();
        services.TryAddSingleton<AuthorityCapturingAuditTrail>(sp =>
            new AuthorityCapturingAuditTrail(sp.GetRequiredService<EventLogBackedAuditTrail>()));
        services.TryAddSingleton<IAuditTrail>(sp =>
            sp.GetRequiredService<AuthorityCapturingAuditTrail>());
        services.TryAddSingleton<IAuthorizedAuditTrail>(sp =>
            sp.GetRequiredService<AuthorityCapturingAuditTrail>());

        // Ticket 212 slice 3 -- the authorized production read of a recorded decision's four-step trace.
        // Scoped, because the in-memory composition registers the trail scoped and a singleton could not
        // depend on it. It resolves the gate, so a container with no gate cannot resolve a read either.
        services.TryAddScoped<AuthorizationTraceReader>();

        return services;
    }

    /// <summary>
    /// Register <see cref="InMemoryAuditEventReader"/> as the
    /// <see cref="IAuditEventReader"/> implementation for test fixtures and
    /// development hosts. Scoped lifetime per ADR 0092 §C5 (substrate DI
    /// lifetime mandate) and ADR 0094 Amendment 2.5 (reader must share the
    /// writer's DI-scope instance so the in-memory store is consistent).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per ADR 0094 (IAuditEventReader) + ADR 0092 §C5 (Scoped lifetime) +
    /// ADR 0091 (ITenantContext) + ADR 0049 (audit-trail substrate write side).
    /// </para>
    ///
    /// <para>
    /// <b>DI lifetime constraint (ADR 0094 Amendment 2.5).</b>
    /// Registers <see cref="InMemoryAuditTrail"/> as Scoped so the reader and
    /// writer share the same instance within a DI scope. Hosts that override
    /// the lifetime MUST keep it Scoped or Singleton; a Transient registration
    /// would silently empty the reader's store on each resolution. The
    /// <see cref="AddHarborlineKernelAuditReaderInMemory"/> method registers a
    /// lifetime assertion (FW2 from ADR 0094's forward-watch items) via
    /// <see cref="ValidateInMemoryAuditTrailLifetime"/>.
    /// </para>
    ///
    /// <para>
    /// Typical test-fixture composition:
    /// <code>
    /// services
    ///     .AddSingleton&lt;IOperationSigner&gt;(signer)
    ///     .AddHarborlineKernelAuditReaderInMemory();
    /// </code>
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddHarborlineKernelAuditReaderInMemory(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register InMemoryAuditTrail as Scoped (both concrete + interface).
        // TryAdd so callers that already registered the trail are not overridden.
        services.TryAddScoped<InMemoryAuditTrail>();
        services.TryAddScoped<IAuditTrail>(sp =>
            sp.GetRequiredService<InMemoryAuditTrail>());
        services.TryAddScoped<IAuthorizedAuditTrail>(sp =>
            sp.GetRequiredService<InMemoryAuditTrail>());

        // Register the reader as Scoped per ADR 0092 §C5.
        services.TryAddScoped<IAuditEventReader>(sp =>
            new InMemoryAuditEventReader(
                sp.GetRequiredService<InMemoryAuditTrail>(),
                sp.GetRequiredService<IAuditTrail>(),
                sp.GetRequiredService<Harborline.Api.Foundation.Crypto.IOperationSigner>()));

        return services;
    }

    /// <summary>
    /// Validates that <see cref="InMemoryAuditTrail"/> is NOT registered as
    /// Transient in <paramref name="services"/>. Throws
    /// <see cref="InvalidOperationException"/> if a Transient registration is
    /// found. Per ADR 0094 Amendment 2.5 forward-watch item FW2: the DI
    /// lifetime assertion ensures the shared in-memory store is never silently
    /// emptied by a Transient resolution.
    /// </summary>
    /// <param name="services">The service collection to inspect.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="InMemoryAuditTrail"/> is registered as Transient.
    /// </exception>
    public static void ValidateInMemoryAuditTrailLifetime(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(InMemoryAuditTrail) &&
                descriptor.Lifetime == ServiceLifetime.Transient)
            {
                throw new InvalidOperationException(
                    "InMemoryAuditTrail is registered as Transient. " +
                    "It MUST be Scoped or Singleton so InMemoryAuditEventReader shares " +
                    "the same in-memory store as the writer. " +
                    "Per ADR 0094 Amendment 2.5.");
            }
        }
    }
}
