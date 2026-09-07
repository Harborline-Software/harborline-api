using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Harborline.Api.Foundation.Forms.Submission;

namespace Harborline.Api.Foundation.Forms.DependencyInjection;

/// <summary>
/// DI registration for the post-submit projection seam (ADR 0101 Rev 3.1 Wave 2 — the forms
/// field-kind extension point).
/// </summary>
public static class FormSubmitProjectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the post-submit projection seam with its at-least-once outbox (ADR 0101 Rev
    /// 3.1 Wave 2b / F-ATOM). The public <see cref="IFormSubmitProjectionRunner"/> is the
    /// <see cref="OutboxFormSubmitProjectionRunner"/> — it records an outbox row BEFORE running
    /// the projections, so a committed submission whose projection is interrupted leaves a recoverable
    /// trace; the core <see cref="FormSubmitProjectionRunner"/> (which iterates the registered
    /// <see cref="IFormSubmitProjection"/> hooks) and the <see cref="IFormSubmitProjectionReconciler"/>
    /// that drains the outbox are registered alongside. The reference in-memory outbox is permitted
    /// only in Development; startup fails closed elsewhere unless the host replaces it with a durable
    /// implementation. Idempotent (<c>TryAdd</c>). Individual
    /// projections (e.g. the asset condition-capture projector) are registered by their owning packages;
    /// the runner is a no-op when none are registered.
    /// </summary>
    public static IServiceCollection AddFormSubmitProjections(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The core runner (iterates the projection hooks) + the Development-only reference outbox.
        services.TryAddSingleton<FormSubmitProjectionRunner>();
        services.TryAddSingleton<IFormSubmitOutbox, InMemoryFormSubmitOutbox>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, InMemoryFormSubmitOutboxGuardAssertion>());

        // The PUBLIC runner is the outbox-backed one, so every submit path (the ProjectingFormEngine
        // decorator, or a node submit route) gets F-ATOM durability transparently.
        services.TryAddSingleton<IFormSubmitProjectionRunner>(sp => new OutboxFormSubmitProjectionRunner(
            sp.GetRequiredService<FormSubmitProjectionRunner>(),
            sp.GetRequiredService<IFormSubmitOutbox>()));

        // The recovery half — a host runs this on startup / on a sweep to re-drain interrupted rows.
        services.TryAddSingleton<IFormSubmitProjectionReconciler>(sp => new FormSubmitProjectionReconciler(
            sp.GetRequiredService<FormSubmitProjectionRunner>(),
            sp.GetRequiredService<IFormSubmitOutbox>()));

        return services;
    }
}
