using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.Entities;

/// <summary>
/// Stage two of a record write (ticket 151): the authority's validator decides the body, and a refusal
/// leaves a reviewable trace entry before it reaches the caller.
/// </summary>
/// <remarks>
/// One helper rather than a try/catch at each coordinator call site: every records coordinator routes its
/// validate through here, so "a refused write is traceable" cannot be true on one path and false on
/// another. The audit sink is optional only for the embedders that compose no audit module; the shipping
/// host always supplies it.
/// </remarks>
internal static class RecordWriteValidation
{
    internal static async ValueTask ValidateAsync(
        IEntityValidator validator,
        AuthorizationRefusalAudit? audit,
        SchemaId schema,
        JsonDocument body,
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken ct)
    {
        try
        {
            await validator.ValidateAsync(schema, body, ct).ConfigureAwait(false);
        }
        catch (EntityValidationException refusal)
        {
            if (audit is not null)
            {
                await audit.RecordValidationRefusalAsync(
                    refusal, TeamRolePermissions.RecordsWrite, principal, tenant, at, ct).ConfigureAwait(false);
            }

            throw;
        }
    }
}
