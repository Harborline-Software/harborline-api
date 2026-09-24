namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>An idempotent submission cannot be reused by a different actor, payload or correlation context.</summary>
public sealed class FormSubmissionReplayConflictException()
    : InvalidOperationException("The prior submission does not match this request context.");
