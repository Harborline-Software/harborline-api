using System.Text.Json;

using Microsoft.AspNetCore.Http;

using Harborline.Kernel.WorkItems;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Maps a declared definition window to the platform admission boundary.</summary>
internal static class NodeDefinitionWrites
{
    internal static async ValueTask<IResult> ExecuteAsync(
        string definitionId,
        JsonElement definition,
        DateTimeOffset observedAt,
        Func<ValueTask<IResult>> write)
    {
        if (!definition.TryGetProperty("contractWindow", out var declared))
            return await write().ConfigureAwait(false);

        DefinitionContractWindow window;
        try
        {
            window = new DefinitionContractWindow(
                definitionId,
                declared.GetProperty("opensAt").GetDateTimeOffset(),
                declared.GetProperty("closesAt").GetDateTimeOffset());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Results.BadRequest(new { code = "definition.invalid-contract-window" });
        }

        var result = await DefinitionWriteBoundary.ExecuteAsync(window, observedAt, write).ConfigureAwait(false);
        return result.Refusal is { } refusal
            ? Results.Json(refusal, statusCode: refusal.StatusCode)
            : result.Value!;
    }
}
