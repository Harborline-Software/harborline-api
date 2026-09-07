using Bunit;

using Harborline.Api.Foundation.Services;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.UIAdapters.Blazor.Components.DataDisplay;
using Harborline.Api.UIAdapters.Blazor.Components.Scheduling;
using Harborline.Api.UIAdapters.Blazor.Enums;
using Harborline.Api.UICore.Contracts;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ViewDefinitions;

public sealed class ShapeRoleAdapterTests
{
    [Fact]
    public void SchedulerConsumesTitleAndPlacedByMappingsBeforeLegacyFieldParameters()
    {
        using var context = CreateContext();
        var mappedStart = new DateTime(2026, 9, 2, 9, 30, 0, DateTimeKind.Local);
        var data = new[]
        {
            new ScheduleRow(
                Caption: "Mapped caption",
                LegacyTitle: "Legacy caption",
                OccursAt: mappedStart,
                LegacyStart: mappedStart.AddMonths(1),
                EndsAt: mappedStart.AddHours(1)),
        };

        var rendered = context.Render<HarborlineScheduler<ScheduleRow>>(parameters => parameters
            .Add(component => component.Data, data)
            .Add(component => component.ShapeRoles,
                new ShapeRoleMapping(Title: nameof(ScheduleRow.Caption), PlacedBy: nameof(ScheduleRow.OccursAt)))
            .Add(component => component.TitleField, nameof(ScheduleRow.LegacyTitle))
            .Add(component => component.StartField, nameof(ScheduleRow.LegacyStart))
            .Add(component => component.EndField, nameof(ScheduleRow.EndsAt))
            .Add(component => component.View, SchedulerView.Day)
            .Add(component => component.CurrentDate, mappedStart.Date));

        Assert.Contains("Mapped caption", rendered.Markup, StringComparison.Ordinal);
        Assert.Contains("09:30", rendered.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Legacy caption", rendered.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ListConsumesGroupedByMappingBeforeLegacyGroupingParameters()
    {
        using var context = CreateContext();
        var data = new[]
        {
            new ListRow("North", "Legacy A"),
            new ListRow("South", "Legacy A"),
        };

        var rendered = context.Render<HarborlineListView<ListRow>>(parameters => parameters
            .Add(component => component.Data, data)
            .Add(component => component.Groupable, true)
            .Add(component => component.ShapeRoles,
                new ShapeRoleMapping(GroupedBy: nameof(ListRow.Swimlane)))
            .Add(component => component.GroupField, nameof(ListRow.LegacyGroup)));

        Assert.Contains($"<strong>{nameof(ListRow.Swimlane)}:</strong> North", rendered.Markup, StringComparison.Ordinal);
        Assert.Contains($"<strong>{nameof(ListRow.Swimlane)}:</strong> South", rendered.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain($"<strong>{nameof(ListRow.LegacyGroup)}:</strong>", rendered.Markup, StringComparison.Ordinal);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddSingleton(Substitute.For<IHarborlineCssProvider>());
        context.Services.AddSingleton(Substitute.For<IHarborlineIconProvider>());
        context.Services.AddSingleton(Substitute.For<IHarborlineThemeService>());
        return context;
    }

    private sealed record ScheduleRow(
        string Caption,
        string LegacyTitle,
        DateTime OccursAt,
        DateTime LegacyStart,
        DateTime EndsAt);

    private sealed record ListRow(string Swimlane, string LegacyGroup);
}
