using System.Text.Json.Serialization;

namespace AgentMesh.Core.Enums;

/// <summary>
/// Transport protocol used to communicate with a Model Context Protocol (MCP) server.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpTransportType
{
    Stdio = 0,
    Http = 1,
    Sse = 2
}
