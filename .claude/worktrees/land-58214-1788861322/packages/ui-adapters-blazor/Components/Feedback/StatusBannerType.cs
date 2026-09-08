namespace Harborline.Api.UIAdapters.Blazor.Components.Feedback;

/// <summary>
/// Banner intents for <see cref="HarborlineStatusBanner"/>, mirroring the
/// ui-react StatusBanner <c>type</c> union.
/// </summary>
public enum StatusBannerType
{
    /// <summary>Data shown is provisional / subject to change.</summary>
    Provisional,

    /// <summary>Feature is gated; access must be requested.</summary>
    Gated,

    /// <summary>Neutral informational notice.</summary>
    Informational,

    /// <summary>Warning that may require user action.</summary>
    Warning,
}
