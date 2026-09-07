using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Blocks.Leases.Services;
using Harborline.Api.Foundation.Localization;

namespace Harborline.Api.Blocks.Leases.DependencyInjection;

/// <summary>
/// DI extension methods for registering Harborline lease-management services.
/// </summary>
public static class LeasesServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ILeaseService"/> as a singleton backed by
    /// <see cref="InMemoryLeaseService"/>.
    /// Suitable for development, testing, and demo scenarios.
    /// Replace with a persistence-backed implementation for production.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same <paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection AddInMemoryLeases(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ILeaseService, InMemoryLeaseService>();

        // Wave 2 Cluster C — Plan 2 Task 3.5: register the open-generic Harborline localizer
        // so consumers can resolve IStringLocalizer-equivalents against this block's
        // SharedResource bundle. Idempotent via TryAddSingleton.
        services.TryAddSingleton(typeof(IHarborlineLocalizer<>), typeof(HarborlineLocalizer<>));

        return services;
    }

    /// <summary>
    /// Registers the ADR 0120 PR-D sub-ledger mapping services for the PM pack.
    ///
    /// <para>
    /// Registers:
    /// <list type="bullet">
    ///   <item><see cref="ILeaseSubLedgerLinkRepository"/> →
    ///     <see cref="InMemoryLeaseSubLedgerLinkRepository"/> (in-memory v1).</item>
    ///   <item><see cref="LeaseSubLedgerService"/> — lease activation + offline read path.</item>
    ///   <item><see cref="ErpNextLeaseCaptureService"/> — ERPNext capture extension.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>D3 pack-boundary (ADR 0111):</b> these services reference ONLY the LOW
    /// identity assembly (<c>blocks-financial-subledger</c> +
    /// <c>blocks-financial-subledger</c>). No AR/AP/payments/projection types
    /// are registered here.
    /// </para>
    ///
    /// <para>
    /// The caller must separately register <c>ISubLedgerAccountRepository</c> and
    /// <c>ISubLedgerReadModel</c> from <c>blocks-financial-subledger</c> and
    /// <c>blocks-financial-subledger-projection</c> respectively. These are HIGH-tier
    /// financial registrations that live outside the PM pack's DI composition root.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same <paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection AddLeaseSubLedgerMapping(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ILeaseSubLedgerLinkRepository, InMemoryLeaseSubLedgerLinkRepository>();
        services.AddSingleton<LeaseSubLedgerService>();
        services.AddSingleton<ErpNextLeaseCaptureService>();

        return services;
    }
}
