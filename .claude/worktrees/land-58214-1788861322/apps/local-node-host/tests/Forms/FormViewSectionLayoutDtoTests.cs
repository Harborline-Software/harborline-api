using System.Collections.Generic;
using System.Text.Json;

using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// ADR 0055 Rev 6 — the wire-DTO closure FED flagged: the prototype projected
/// <see cref="SectionLayout"/> client-side only, so the server wire never carried
/// it. These tests pin that <see cref="FormViewSectionDto"/> now serialises
/// <c>layout</c> / <c>fieldPlacement</c> as the EXACT camelCase JSON the
/// <c>@harborline-software/api-contracts/forms</c> mirror declares — lowercase enum string
/// unions (<c>kind</c>/<c>direction</c>/<c>wrap</c>) and numeric placement — AND
/// that a section with no layout serialises byte-identically to pre-Rev-6
/// (the keys are simply absent, the back-compat guarantee).
/// </summary>
public sealed class FormViewSectionLayoutDtoTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static FormViewField Field(string name) => new(
        name,
        InternationalizedText.FromInvariant(name),
        HelpText: null,
        ControlHint: "text",
        IsSensitive: false,
        IsReadable: true,
        Value: null);

    [Fact]
    public void GridLayout_SerialisesLowercaseKindAndNumericPlacement()
    {
        var section = new FormViewSection(
            "location",
            InternationalizedText.FromInvariant("Location"),
            new[] { Field("addressLine1"), Field("city") },
            Layout: new SectionLayout(SectionLayoutKind.Grid, Columns: 3, Gap: 6),
            FieldPlacement: new Dictionary<string, FieldPlacement>
            {
                ["addressLine1"] = new(ColSpan: 2),
            });

        var dto = FormViewSectionDto.From(section);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, Web));
        var root = doc.RootElement;

        var layout = root.GetProperty("layout");
        Assert.Equal("grid", layout.GetProperty("kind").GetString());
        Assert.Equal(3, layout.GetProperty("columns").GetInt32());
        Assert.Equal(6, layout.GetProperty("gap").GetInt32());

        var placement = root.GetProperty("fieldPlacement");
        Assert.Equal(2, placement.GetProperty("addressLine1").GetProperty("colSpan").GetInt32());
    }

    [Fact]
    public void FlexLayout_SerialisesLowercaseDirectionAndWrap()
    {
        var section = new FormViewSection(
            "location",
            InternationalizedText.FromInvariant("Location"),
            new[] { Field("addressLine1") },
            Layout: new SectionLayout(
                SectionLayoutKind.Flex, Direction: FlexDirection.Column, Wrap: FlexWrap.NoWrap, Gap: 4),
            FieldPlacement: new Dictionary<string, FieldPlacement> { ["addressLine1"] = new(Grow: 1) });

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(FormViewSectionDto.From(section), Web));
        var layout = doc.RootElement.GetProperty("layout");

        // The TS contract's unions are lowercase — the DTO must NOT emit PascalCase enum names.
        Assert.Equal("flex", layout.GetProperty("kind").GetString());
        Assert.Equal("column", layout.GetProperty("direction").GetString());
        Assert.Equal("nowrap", layout.GetProperty("wrap").GetString());
        Assert.Equal(
            1,
            doc.RootElement.GetProperty("fieldPlacement").GetProperty("addressLine1").GetProperty("grow").GetInt32());
    }

    [Fact]
    public void NoLayout_OmitsLayoutKeys_BackCompatByteIdentical()
    {
        var section = new FormViewSection(
            "location",
            InternationalizedText.FromInvariant("Location"),
            new[] { Field("addressLine1") });

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(FormViewSectionDto.From(section), Web));
        var root = doc.RootElement;

        // Both keys are absent (WhenWritingNull) — a pre-Rev-6 section's JSON is unchanged.
        Assert.False(root.TryGetProperty("layout", out _));
        Assert.False(root.TryGetProperty("fieldPlacement", out _));
        // The legacy keys are untouched.
        Assert.Equal("location", root.GetProperty("id").GetString());
        Assert.True(root.TryGetProperty("fields", out _));
    }
}
