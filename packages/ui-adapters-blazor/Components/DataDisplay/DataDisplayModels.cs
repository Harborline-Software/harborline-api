namespace Harborline.Api.UIAdapters.Blazor.Components.DataDisplay;

/// <summary>A term/value row for <see cref="HarborlineDescriptionList"/>.</summary>
public sealed record DescriptionListItem(string Term, string Description);

/// <summary>Supported categorical status-pill families.</summary>
public enum StatusPillKind
{
    GlAccountType,
    OccupancyStatus,
    AgingBucket,
    BalanceState,
    WorkOrderStatus,
}
