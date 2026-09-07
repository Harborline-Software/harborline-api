using System.Collections.Immutable;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>Severs every caller-owned collection before a form enters definition persistence.</summary>
internal static class FormDefinitionFreezer
{
    internal static FormDefinition Freeze(FormDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition with
        {
            Envelope = definition.Envelope with
            {
                Requires = definition.Envelope.Requires.ToImmutableArray(),
            },
            Overlay = Freeze(definition.Overlay),
        };
    }

    private static HarborlineOverlay Freeze(HarborlineOverlay overlay) => overlay with
    {
        Fields = overlay.Fields.ToImmutableDictionary(
            pair => pair.Key,
            pair => Freeze(pair.Value),
            StringComparer.Ordinal),
        Sections = overlay.Sections.Select(Freeze).ToImmutableArray(),
        Rules = overlay.Rules.Select(Freeze).ToImmutableArray(),
        Title = Freeze(overlay.Title),
        Description = Freeze(overlay.Description),
        Aspects = Freeze(overlay.Aspects),
        Pages = overlay.Pages?.Select(Freeze).ToImmutableArray(),
        Wizard = Freeze(overlay.Wizard),
        AsyncChecks = overlay.AsyncChecks?.Select(check => check with
        {
            Inputs = check.Inputs?.ToImmutableArray(),
        }).ToImmutableArray(),
    };

    private static FieldOverlay Freeze(FieldOverlay field) => field with
    {
        Label = Freeze(field.Label)!,
        HelpText = Freeze(field.HelpText),
        FieldReadRoles = field.FieldReadRoles?.ToImmutableArray(),
        FieldWriteRoles = field.FieldWriteRoles?.ToImmutableArray(),
        FieldReadStandings = field.FieldReadStandings?.ToImmutableArray(),
        FieldWriteStandings = field.FieldWriteStandings?.ToImmutableArray(),
        Aspects = Freeze(field.Aspects),
    };

    private static FormSection Freeze(FormSection section) => section with
    {
        Title = Freeze(section.Title)!,
        Fields = section.Fields.ToImmutableArray(),
        Access = Freeze(section.Access),
        FieldPlacement = section.FieldPlacement?.ToImmutableDictionary(StringComparer.Ordinal),
        Aspects = Freeze(section.Aspects),
        Items = section.Items?.Select(Freeze).ToImmutableArray(),
    };

    private static SectionAccess Freeze(SectionAccess access) => access with
    {
        ReadRoles = access.ReadRoles.ToImmutableArray(),
        WriteRoles = access.WriteRoles.ToImmutableArray(),
        ReadStandings = access.ReadStandings?.ToImmutableArray(),
        WriteStandings = access.WriteStandings?.ToImmutableArray(),
    };

    private static RuleDefinition Freeze(RuleDefinition rule) => rule with
    {
        Envelope = rule.Envelope with { Requires = rule.Envelope.Requires.ToImmutableArray() },
        ErrorMessage = Freeze(rule.ErrorMessage),
        Presentation = rule.Presentation is null
            ? null
            : rule.Presentation with { Badge = Freeze(rule.Presentation.Badge) },
    };

    private static FormPage Freeze(FormPage page) => page with
    {
        Title = Freeze(page.Title)!,
        Sections = page.Sections.ToImmutableArray(),
        Checks = page.Checks?.ToImmutableArray(),
    };

    private static WizardSettings? Freeze(WizardSettings? wizard) => wizard is null
        ? null
        : wizard with { ConfirmationMessage = Freeze(wizard.ConfirmationMessage) };

    private static FormItem Freeze(FormItem item) => item with
    {
        Items = item.Items?.Select(Freeze).ToImmutableArray(),
        Title = Freeze(item.Title),
        Content = item.Content?.Select(content => content with
        {
            Text = Freeze(content.Text)!,
        }).ToImmutableArray(),
        Action = item.Action is null
            ? null
            : item.Action with { Label = Freeze(item.Action.Label)! },
        Placement = item.Placement?.ToImmutableDictionary(StringComparer.Ordinal),
        Table = item.Table is null
            ? null
            : item.Table with
            {
                Columns = item.Table.Columns?.ToImmutableDictionary(StringComparer.Ordinal),
                Totals = item.Table.Totals?.ToImmutableArray(),
            },
        Aspects = Freeze(item.Aspects),
    };

    private static AspectOverlay? Freeze(AspectOverlay? aspects) => aspects is null
        ? null
        : aspects with
        {
            Classification = aspects.Classification is null
                ? null
                : aspects.Classification with { Tags = aspects.Classification.Tags.ToImmutableArray() },
            Access = aspects.Access is null
                ? null
                : aspects.Access with
                {
                    ReadRoles = aspects.Access.ReadRoles?.ToImmutableArray(),
                    WriteRoles = aspects.Access.WriteRoles?.ToImmutableArray(),
                    ReadStandings = aspects.Access.ReadStandings?.ToImmutableArray(),
                    WriteStandings = aspects.Access.WriteStandings?.ToImmutableArray(),
                },
            Lifecycle = aspects.Lifecycle?.Residency is null
                ? aspects.Lifecycle
                : aspects.Lifecycle with
                {
                    Residency = aspects.Lifecycle.Residency with
                    {
                        AllowedJurisdictions = aspects.Lifecycle.Residency.AllowedJurisdictions.ToImmutableArray(),
                        ProhibitedJurisdictions = aspects.Lifecycle.Residency.ProhibitedJurisdictions?.ToImmutableArray(),
                    },
                },
        };

    private static InternationalizedText? Freeze(InternationalizedText? text) => text is null
        ? null
        : text with { Values = text.Values.ToImmutableDictionary(StringComparer.Ordinal) };
}
