using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

internal static class AuthorizationWriterTestExtensions
{
    internal static ValueTask<AuthorizationConfigurationWriteResult> WriteAsync(
        this AuthorizationDefinitionWriter writer,
        AuthorizationConfigurationCommand command,
        CancellationToken ct = default) => writer.WriteAsync(
        command,
        new AuthorizationWriteContext(
            new ActorId("test:authorization-writer"),
            command is NarrowCapabilityRoleBinding narrow ? narrow.TenantId : new TenantId("test"),
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero)),
        ct);
}
