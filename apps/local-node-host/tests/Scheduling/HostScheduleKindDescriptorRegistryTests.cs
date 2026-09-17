using System.Text.Json;

using Harborline.Api.Foundation.ScheduleDefinitions;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.Scheduling;

namespace Harborline.Api.LocalNodeHost.Tests.Scheduling;

public sealed class HostScheduleKindDescriptorRegistryTests
{
    [Fact]
    public async Task Pack_admission_checks_every_module_and_refuses_an_unknown_later_entry()
    {
        var registry = new HostScheduleKindDescriptorRegistry(
            new SchedulingDraftValidator(["admitted.capacity"]));
        var definition = new ScheduleDefinition
        {
            Key = "module-check",
            Version = "1.0.0",
            Tenant = "tenant",
            SchemaVersion = 1,
            ScheduleKind = SchedulingDraftValidator.DraftSchema,
            Title = "Module check",
            Body = JsonSerializer.SerializeToElement(new
            {
                schema = SchedulingDraftValidator.DraftSchema,
                title = "Module check",
                timezone = "UTC",
                activities = new[] { new { id = "work", durationMinutes = 30 } },
                resourceRequirements = Array.Empty<string>(),
                modules = new[] { "admitted.capacity", "unknown.routing" },
            }),
        };

        var exception = await Assert.ThrowsAsync<ScheduleDefinitionGovernanceException>(async () =>
            await registry.AdmitAsync(definition));

        Assert.Equal("schedule_definition.body_invalid", exception.ErrorCode);
    }
}
