using System.Text.Json.Serialization;

namespace AgentMesh.Cli.Models;

public class CliConfig
{
    public string MeshUrl { get; set; } = "http://localhost:5000";
    public string MasterToken { get; set; } = "mesh-default-secret-key";
    public Dictionary<string, AgentPresetConfig> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class AgentPresetConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = "Autonomous Agent";
    public string AdapterType { get; set; } = "generic";
    public string? Command { get; set; }
    public string? Arguments { get; set; }
    public string? Token { get; set; }
    public List<string> Capabilities { get; set; } = new();
}

public class PromptReceivedEvent
{
    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; set; }

    [JsonPropertyName("messageId")]
    public Guid MessageId { get; set; }

    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("sender")]
    public string Sender { get; set; } = "operator";

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("effort")]
    public string? Effort { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }
}

public class TrajectoryEventPayload
{
    public string EventType { get; set; } = "response";
    public string Content { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public string? ToolArgsJson { get; set; }
    public string? ToolResultJson { get; set; }
    public string? StructuredPayloadJson { get; set; }
}

public enum RunnerConnectionState
{
    Offline,
    Connecting,
    Online,
    Busy,
    Error
}

public class AgentRuntimeState
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string AdapterType { get; set; } = string.Empty;
    public RunnerConnectionState State { get; set; } = RunnerConnectionState.Offline;
    public Guid? ActiveSessionId { get; set; }
    public int PromptsHandled { get; set; }
    public long TotalTokensUsed { get; set; }
    public double TotalDurationSeconds { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public string? LastError { get; set; }
}

public record RemoteAgentDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("sessionCount")] int SessionCount,
    [property: JsonPropertyName("lastHeartbeatAt")] DateTimeOffset? LastHeartbeatAt
);
