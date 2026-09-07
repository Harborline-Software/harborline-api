using Microsoft.AspNetCore.Components;
using Harborline.Api.UICore.Contracts;

namespace Harborline.Api.UIAdapters.Blazor.Renderers;

/// <summary>
/// Default <see cref="IHarborlineRenderer"/> for Blazor Server / WASM / MAUI Hybrid
/// hosts. Produces a <see cref="RenderFragment"/> from a
/// <see cref="HarborlineWidgetDescriptor"/> by mapping <c>WidgetKind</c> to an HTML
/// element name and <c>Parameters</c> to attributes.
/// </summary>
/// <remarks>
/// This is a deliberately simple v0 implementation — per-widget custom rendering
/// is a follow-up when a second renderer lands (see gap G7 Option A follow-up).
/// Consumers cast the <see cref="Render"/> result back to
/// <see cref="RenderFragment"/>.
/// </remarks>
public sealed class BlazorDomRenderer : IHarborlineRenderer
{
    /// <inheritdoc />
    public string Platform => "blazor-dom";

    /// <inheritdoc />
    public object Render(HarborlineWidgetDescriptor descriptor)
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenElement(0, descriptor.WidgetKind);
            foreach (var (key, value) in descriptor.Parameters)
            {
                builder.AddAttribute(1, key, value);
            }
            foreach (var child in descriptor.Children)
            {
                var childFragment = (RenderFragment)Render(child);
                builder.AddContent(2, childFragment);
            }
            builder.CloseElement();
        };
        return fragment;
    }
}
