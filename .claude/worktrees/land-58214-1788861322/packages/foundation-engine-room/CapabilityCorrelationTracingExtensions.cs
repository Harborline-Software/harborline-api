using Harborline.Api.Protocol;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.Foundation.EngineRoom;

/// <summary>
/// Bridges the capability invoke correlation id into the ambient .NET trace context.
/// </summary>
public static class CapabilityCorrelationTracingExtensions
{
    /// <summary>Header written by the TypeScript capability membrane.</summary>
    public const string CorrelationHeaderName = "x-capability-correlation-id";

    /// <summary>
    /// Adds correlation tracing to an endpoint whose request argument is a
    /// <see cref="CapabilityInvokeRequest"/>.
    /// </summary>
    /// <param name="builder">Capability invoke endpoint builder.</param>
    /// <returns>The same endpoint builder.</returns>
    public static RouteHandlerBuilder WithCapabilityCorrelationTracing(this RouteHandlerBuilder builder)
        => builder.WithCapabilityCorrelationTracing<CapabilityInvokeRequest>(request => request.CorrelationId);

    /// <summary>Adds capability correlation tracing to an endpoint with a runtime-native request type.</summary>
    /// <typeparam name="TRequest">Endpoint request type.</typeparam>
    /// <param name="builder">Capability invoke endpoint builder.</param>
    /// <param name="correlationIdSelector">Reads the body correlation id from the request.</param>
    /// <returns>The same endpoint builder.</returns>
    public static RouteHandlerBuilder WithCapabilityCorrelationTracing<TRequest>(
        this RouteHandlerBuilder builder,
        Func<TRequest, string> correlationIdSelector)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(correlationIdSelector);

        return builder.AddEndpointFilter(async (context, next) =>
        {
            var request = context.Arguments.OfType<TRequest>().SingleOrDefault()
                ?? throw new InvalidOperationException(
                    $"{nameof(WithCapabilityCorrelationTracing)} requires a {typeof(TRequest).Name} endpoint argument.");
            var correlationId = correlationIdSelector(request);
            var header = context.HttpContext.Request.Headers[CorrelationHeaderName].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(header))
            {
                correlationId = header;
            }

            var telemetry = context.HttpContext.RequestServices.GetRequiredService<EngineRoomTelemetry>();
            using var activity = telemetry.StartCapabilityInvoke(correlationId);
            return await next(context).ConfigureAwait(false);
        });
    }
}
