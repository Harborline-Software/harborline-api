using Harborline.Api.UIAdapters.Blazor.Enums;

namespace Harborline.Api.UIAdapters.Blazor.Components.AI;

/// <summary>Lifecycle state rendered beside a chat message.</summary>
public enum ChatMessageStatus
{
    Pending,
    Streaming,
    Complete,
    Error,
    Sent,
    Delivered,
    Read,
    Failed,
}

/// <summary>A card-style attachment owned by the chat host.</summary>
public sealed record ChatAttachment(
    string Id,
    string Title,
    string? Subtitle = null,
    string? Url = null,
    string? ImageUrl = null);

/// <summary>An action shown in a message toolbar.</summary>
public sealed record ChatMessageAction(string Id, string Label);

/// <summary>Payload for a message action callback.</summary>
public sealed class ChatMessageActionEventArgs
{
    /// <summary>The message that owns the action.</summary>
    public ChatMessage Message { get; }

    /// <summary>The selected action.</summary>
    public ChatMessageAction Action { get; }

    /// <summary>Creates an action payload.</summary>
    public ChatMessageActionEventArgs(ChatMessage message, ChatMessageAction action)
    {
        Message = message;
        Action = action;
    }
}

/// <summary>
/// A role-tagged message rendered by <see cref="HarborlineChat"/>. The additional
/// properties are host-owned display metadata; the adapter never calls an LLM.
/// </summary>
public sealed record ChatMessage(ChatRole Role, string Content, DateTimeOffset Timestamp)
{
    /// <summary>Stable id used by pinned/reply interactions.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Optional display name for the message author.</summary>
    public string? AuthorName { get; init; }

    /// <summary>Optional attachments rendered below the message body.</summary>
    public IReadOnlyList<ChatAttachment> Attachments { get; init; } = Array.Empty<ChatAttachment>();

    /// <summary>Message lifecycle state.</summary>
    public ChatMessageStatus Status { get; init; } = ChatMessageStatus.Complete;

    /// <summary>Whether the message appears in the pinned strip.</summary>
    public bool IsPinned { get; init; }

    /// <summary>Id of the message this message replies to.</summary>
    public string? ReplyToId { get; init; }

    /// <summary>Create a message stamped with the caller's admitted instant.</summary>
    public static ChatMessage Create(ChatRole role, string content, DateTimeOffset at)
        => new(role, content, at);
}
