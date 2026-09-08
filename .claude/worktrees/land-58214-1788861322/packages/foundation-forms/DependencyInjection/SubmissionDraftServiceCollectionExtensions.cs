using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Forms.Drafts;

namespace Harborline.Api.Foundation.Forms.DependencyInjection;

/// <summary>
/// DI registration for the D2 save-and-resume submission-draft substrate (ADR 0135
/// amendment 2026-07-01): the fail-closed <see cref="ISubmissionDraftService"/> +
/// <see cref="ISubmissionDraftStore"/> and the pre-auth capture buffer.
/// </summary>
public static class SubmissionDraftServiceCollectionExtensions
{
    /// <summary>
    /// Registers the submission-draft services with the composable in-memory store +
    /// buffer as the <c>TryAdd</c> defaults. A durable host registers its own
    /// <see cref="ISubmissionDraftStore"/> (e.g. a restart-surviving node-EF store)
    /// BEFORE calling this so its store wins the <c>TryAdd</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires the ADR-0102 party seam (<c>AddHarborlinePartyContext</c>) and a scoped
    /// <c>ITenantContext</c> (<c>AddHarborlineTenantContext</c>) to be registered — the
    /// draft service resolves the tenant + party fail-closed off those.
    /// </para>
    /// <para>
    /// The store + buffer are singletons; the two services are SCOPED because they read
    /// the scoped ambient principal (<c>IPartyContext</c> / <c>ITenantContext</c>).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddHarborlineSubmissionDrafts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISubmissionDraftStore, InMemorySubmissionDraftStore>();
        services.TryAddSingleton<IPreAuthCaptureBuffer, InMemoryPreAuthCaptureBuffer>();
        services.TryAddScoped<ISubmissionDraftService, SubmissionDraftService>();
        services.TryAddScoped<IPreAuthCaptureService, PreAuthCaptureService>();

        return services;
    }
}
