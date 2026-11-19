using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

public class AgentSession
{
    private string? _driverId;
    private string? _creatorAgentId;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public string MetadataJson { get; set; } = "{}";

    public string? PersonaId { get; set; }

    [JsonIgnore]
    public Persona? Persona { get; set; }

    public string? DriverId
    {
        get => _driverId ?? _creatorAgentId;
        set
        {
            _driverId = value;
            if (_creatorAgentId is null)
            {
                _creatorAgentId = value;
            }
        }
    }

    [JsonIgnore]
    public Agent? Driver { get; set; }

    public string? CreatorAgentId
    {
        get => _creatorAgentId ?? _driverId;
        set
        {
            _creatorAgentId = value;
            if (_driverId is null)
            {
                _driverId = value;
            }
        }
    }

    [JsonIgnore]
    public Agent? CreatorAgent { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public ICollection<SessionMessage> Messages { get; set; } = new List<SessionMessage>();

    [JsonIgnore]
    public ICollection<AgentJob> Jobs { get; set; } = new List<AgentJob>();
}


