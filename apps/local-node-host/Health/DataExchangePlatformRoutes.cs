using Harborline.Foundation.DataExchange;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>API-owned discovery surface for the released Data Exchange protocol.</summary>
public static class DataExchangePlatformRoutes
{
    public const string Route = "/api/local-node/data-exchange/runtime-contract";

    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(Route, () => Results.Ok(new DataExchangeRuntimeContractDto(
            TabularMappingProfile.Family,
            TabularMappingProfile.SchemaUri,
            "1.0.0",
            [
                DataExchangePermissions.Author,
                DataExchangePermissions.DryRun,
                DataExchangePermissions.Commit,
                DataExchangePermissions.ReadRunResults,
            ],
            Enum.GetNames<ExchangeEffectStatus>(),
            InboundOnly: true)));
    }
}

public sealed record DataExchangeRuntimeContractDto(
    string Profile,
    string Schema,
    string DocumentVersion,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> EffectOutcomes,
    bool InboundOnly);
