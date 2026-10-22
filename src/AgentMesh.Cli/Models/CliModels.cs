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

    // Custom OpenAI compatible server settings
    public string? Endpoint { get; set; } // e.g. http://localhost:11434/v1
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; }
    public Dictionary<string, string>? CustomHeaders { get; set; }
}

public class PromptReceivedEvent
{
    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; set; }

    [JsonPropertyName("messageId")]
    public Guid MessageId { get; set; }

    [JsonPropertyName("agentId")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("driverId")]
    public string? DriverId { get; set; }

    [JsonPropertyName("personaId")]
    public string? PersonaId { get; set; }

    [JsonPropertyName("personaName")]
    public string? PersonaName { get; set; }

    [JsonPropertyName("personaRole")]
    public string? PersonaRole { get; set; }

    [JsonPropertyName("personaPrompt")]
    public string? PersonaPrompt { get; set; }

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

    [JsonPropertyName("tier")]
    public string? Tier { get; set; }

    [JsonPropertyName("contextSize")]
    public string? ContextSize { get; set; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("gitBranch")]
    public string? GitBranch { get; set; }

    [JsonPropertyName("workspaceType")]
    public string? WorkspaceType { get; set; }

    [JsonPropertyName("attachments")]
    public List<AttachmentItem>? Attachments { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AttachmentItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "application/octet-stream";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("s3Key")]
    public string? S3Key { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonIgnore]
    public string? LocalFilePath { get; set; }
}

public class TrajectoryEventPayload
{
    public string EventType { get; set; } = "response";
    public string Content { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public string? ToolArgsJson { get; set; }
    public string? ToolResultJson { get; set; }
    public string? StructuredPayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class StopExecutionEvent
{
    [JsonPropertyName("sessionId")]
    public Guid? SessionId { get; set; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public class StopCommandEvent
{
    [JsonPropertyName("sessionId")]
    public Guid? SessionId { get; set; }

    [JsonPropertyName("commandId")]
    public string? CommandId { get; set; }

    [JsonPropertyName("agentId")]
    public string? AgentId { get; set; }
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
