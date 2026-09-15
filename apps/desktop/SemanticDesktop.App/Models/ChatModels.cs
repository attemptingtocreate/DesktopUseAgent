namespace SemanticDesktop.App.Models;

public sealed class Conversation
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "New conversation";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? SelectedProviderId { get; set; }
    public string? SelectedModel { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
    public List<AssistantTurn> Turns { get; set; } = new();
}

public sealed class ChatMessage
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = ChatRoles.User;
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? TurnId { get; set; }
    public List<ToolResultReference>? ToolResults { get; set; }
}

public static class ChatRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
}

public sealed class AssistantTurn
{
    public string Id { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ProviderType { get; set; } = "";
    public string? Model { get; set; }
    public string Status { get; set; } = TurnStatus.Streaming;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<ToolInvocation> ToolInvocations { get; set; } = new();
    public string? Error { get; set; }
}

public static class TurnStatus
{
    public const string Streaming = "streaming";
    public const string Completed = "completed";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
    public const string PermissionDenied = "permission_denied";
}

public sealed class ToolInvocation
{
    public string Id { get; set; } = "";
    public string Tool { get; set; } = "";
    public string? Target { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Success { get; set; }
    public string? PermissionDecision { get; set; }
    public string? Error { get; set; }
    public string? Summary { get; set; }
}

public sealed class ToolResultReference
{
    public string ToolCallId { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Success { get; set; }
}

public sealed class ProviderRecord
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class ModelRecord
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ProviderId { get; set; }
}

public sealed class ConversationListItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public string? SelectedProviderId { get; set; }
}
