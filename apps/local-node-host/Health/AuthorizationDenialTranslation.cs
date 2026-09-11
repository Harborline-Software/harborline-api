using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Ticket 380 slice 1 — the node's LAST word on an authorization refusal that reached the server as an
/// exception instead of a guard verdict.
/// </summary>
/// <remarks>
/// <para>
/// A route family that authorizes at the route AND again inside the service it calls makes two decisions
/// about one command (ticket 379's review, finding 6: the pack install route decides on the preview's key
/// while <c>PackInstaller.AuthorizeOrAudit</c> decides on the coordinates claimed in the artifact). While
/// the two agree the second decision is redundant; when they DISAGREE the inner one raises
/// <see cref="AuthorizationDeniedException"/>, which used to escape to Kestrel as an empty HTTP 500. A
/// refusal answered as a server error is a security-boundary defect: the caller cannot tell a denial from
/// a crash, and nothing records the decision that refused.
/// </para>
/// <para>
/// Registered ONCE in <see cref="SharedHostedWebApp"/>, so it closes the CLASS rather than the instance:
/// no handler on the inner application can let the gate's denial reach the server, including routes added
/// later. It renders the decision the exception CARRIES through
/// <see cref="RequestAuthorization.RefusedAsync(HttpContext, AuthorizationDeniedException, CancellationToken)"/>
/// — the node's one read-filtered refusal renderer plus its refusal audit row — so nothing here re-decides
/// and the exception's own message never reaches the wire.
/// </para>
/// </remarks>
internal static class AuthorizationDenialTranslation
{
    /// <summary>Installs the translation on <paramref name="app"/>.</summary>
    internal static void Use(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            // A plain catch clause, not a `when` filter: the 214 fence discovers catchers from the IL
            // exception-handling table, where a filtered clause carries no catch type and would be invisible
            // to it (AuthorizationRefusalRenderingFenceTests). The response-started test is therefore made
            // inside the clause: a response already on the wire cannot become a 403, and that denial stays an
            // error — the honest answer for a handler that had already started writing.
            catch (AuthorizationDeniedException denial)
            {
                if (context.Response.HasStarted)
                {
                    throw;
                }

                var refusal = await RequestAuthorization
                    .RefusedAsync(context, denial, context.RequestAborted)
                    .ConfigureAwait(false);
                await refusal.ExecuteAsync(context).ConfigureAwait(false);
            }
        });
    }
}
