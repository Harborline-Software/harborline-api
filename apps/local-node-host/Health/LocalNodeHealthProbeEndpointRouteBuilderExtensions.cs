using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Maps the local node's aggregate and consequence-specific health probes.</summary>
public static class LocalNodeHealthProbeEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>/health</c> as the aggregate, <c>/live</c> for restart decisions, and
    /// <c>/ready</c> for traffic-rotation decisions.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <returns>The supplied route builder.</returns>
    public static IEndpointRouteBuilder MapLocalNodeHealthProbes(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var operational = endpoints.MapPreAuthOperationalGroup();
        operational.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteAggregateResponseAsync,
        });
        operational.MapHealthChecks("/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
        });
        operational.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
        });
        return endpoints;
    }

    private static Task WriteAggregateResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        var details = report.Entries.Values
            .Select(entry => entry.Description)
            .Where(description => !string.IsNullOrWhiteSpace(description));
        return context.Response.WriteAsync(
            string.Join(Environment.NewLine, details.Prepend(report.Status.ToString())));
    }
}
