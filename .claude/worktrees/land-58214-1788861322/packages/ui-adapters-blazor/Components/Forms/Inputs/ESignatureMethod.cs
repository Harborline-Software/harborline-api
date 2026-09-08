namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Inputs;

/// <summary>Capture methods offered by <see cref="HarborlineESignatureField"/>.</summary>
public enum ESignatureMethod
{
    /// <summary>Draw the signature on a canvas pad.</summary>
    Draw,

    /// <summary>Type a full name as the signature.</summary>
    Type,

    /// <summary>Upload an existing signature image.</summary>
    Upload,
}
