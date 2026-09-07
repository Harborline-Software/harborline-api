namespace Harborline.Api.UIAdapters.Blazor.Components.AI;

/// <summary>A quick action shown by <see cref="HarborlineAIPrompt"/>.</summary>
public sealed record AIPromptSuggestion(string Text, string? Id = null);

/// <summary>Lifecycle state for an inline AI output supplied by the host.</summary>
public enum InlineAIPromptOutputStatus
{
    Pending,
    Streaming,
    Complete,
    Error,
}

/// <summary>An output card rendered by <see cref="HarborlineInlineAIPrompt"/>.</summary>
public sealed record InlineAIPromptOutput(
    string Id,
    string Prompt,
    string? Response,
    InlineAIPromptOutputStatus Status = InlineAIPromptOutputStatus.Complete);

/// <summary>A named command rendered in the inline prompt toolbar.</summary>
public sealed record InlineAIPromptCommand(string Id, string Text);

/// <summary>A display-only conversation summary.</summary>
public sealed record ConversationSummary(
    string Id,
    string Title,
    string Timestamp,
    string? Preview = null);

/// <summary>Payload for a conversation rename request.</summary>
public sealed record ConversationRenameEventArgs(string Id, string CurrentTitle);
