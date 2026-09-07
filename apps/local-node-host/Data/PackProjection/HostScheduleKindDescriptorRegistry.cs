using Harborline.Api.Foundation.ScheduleDefinitions;
using Harborline.Api.LocalNodeHost.Data.Scheduling;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// Adapts the host's existing scheduling authoring contract to schedule-definition descriptor
/// admission. Unlike the reports descriptor, which injects the host's runtime cartridge registry
/// because registered cartridges are runtime state, scheduling's contract authority is the stateless
/// <see cref="SchedulingDraftValidator"/>. It is constructed directly so descriptor admission never
/// depends on the <c>LocalNode:SchedulingDogfood:Enabled</c> feature flag that gates authoring DI.
/// </summary>
public sealed class HostScheduleKindDescriptorRegistry : IScheduleDefinitionDescriptorRegistry
{
    private const string KindUnknown = "schedule_definition.kind_unknown";
    private const string BodyInvalid = "schedule_definition.body_invalid";

    private readonly SchedulingDraftValidator _validator = new();

    /// <inheritdoc />
    public ValueTask AdmitAsync(
        ScheduleDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        if (!StringComparer.Ordinal.Equals(
                definition.ScheduleKind,
                SchedulingDraftValidator.DraftSchema))
        {
            throw new ScheduleDefinitionGovernanceException(KindUnknown);
        }

        if (_validator.Validate(definition.Body).Count != 0)
        {
            throw new ScheduleDefinitionGovernanceException(BodyInvalid);
        }

        return ValueTask.CompletedTask;
    }
}
