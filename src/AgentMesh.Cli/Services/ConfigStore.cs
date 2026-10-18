using System.Text.Json;
using AgentMesh.Cli.Models;

namespace AgentMesh.Cli.Services;

public class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configFilePath;

    public ConfigStore(string? customConfigPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customConfigPath))
        {
            _configFilePath = customConfigPath;
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(home, ".agentmesh");
            _configFilePath = Path.Combine(dir, "config.json");
        }
    }

    public string ConfigFilePath => _configFilePath;

    public CliConfig Load()
    {
        CliConfig config;
        if (File.Exists(_configFilePath))
        {
            try
            {
                var json = File.ReadAllText(_configFilePath);
                config = JsonSerializer.Deserialize<CliConfig>(json, JsonOptions) ?? new CliConfig();
            }
            catch
            {
                config = new CliConfig();
            }
        }
        else
        {
            config = new CliConfig();
        }

        // Apply environment overrides if set
        var envUrl = Environment.GetEnvironmentVariable("AGENTMESH_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            config.MeshUrl = envUrl.TrimEnd('/');
        }

        var envToken = Environment.GetEnvironmentVariable("AGENTMESH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            config.MasterToken = envToken.Trim();
        }

        EnsureBuiltInPresets(config);
        return config;
    }

    public void Save(CliConfig config)
    {
        var dir = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configFilePath, json);
    }

    public AgentPresetConfig ResolveOrCreatePreset(CliConfig config, string agentId, string? customCommand = null, string? customRole = null)
    {
        var normalizedId = agentId.Trim().ToLowerInvariant();
        if (config.Agents.TryGetValue(normalizedId, out var existing))
        {
            if (!string.IsNullOrWhiteSpace(customCommand))
            {
                existing.Command = customCommand;
            }
            if (!string.IsNullOrWhiteSpace(customRole))
            {
                existing.Role = customRole;
            }
            return existing;
        }

        var adapterType = InferAdapterType(normalizedId);
        var preset = new AgentPresetConfig
        {
            Id = normalizedId,
            Name = FormatDisplayName(normalizedId, adapterType),
            Role = customRole ?? GetDefaultRole(adapterType),
            AdapterType = adapterType,
            Command = customCommand ?? GetDefaultCommand(adapterType),
            Capabilities = GetDefaultCapabilities(adapterType)
        };

        config.Agents[normalizedId] = preset;
        Save(config);
        return preset;
    }

    private static void EnsureBuiltInPresets(CliConfig config)
    {
        if (!config.Agents.ContainsKey("agy"))
        {
            config.Agents["agy"] = new AgentPresetConfig
            {
                Id = "agy",
                Name = "Antigravity CLI",
                Role = "Autonomous Codebase Engineer",
                AdapterType = "agy",
                Command = "agy",
                Capabilities = new List<string> { "run_command", "view_file", "replace_file_content", "search_code" }
            };
        }

        if (!config.Agents.ContainsKey("claude"))
        {
            config.Agents["claude"] = new AgentPresetConfig
            {
                Id = "claude",
                Name = "Claude Code CLI",
                Role = "Anthropic Terminal Architect",
                AdapterType = "claude",
                Command = "claude",
                Arguments = "-p",
                Capabilities = new List<string> { "bash", "edit_file", "read_file", "grep" }
            };
        }

        if (!config.Agents.ContainsKey("copilot"))
        {
            config.Agents["copilot"] = new AgentPresetConfig
            {
                Id = "copilot",
                Name = "GitHub Copilot CLI",
                Role = "Shell & Git Specialist",
                AdapterType = "copilot",
                Command = "gh",
                Arguments = "copilot",
                Capabilities = new List<string> { "explain", "suggest", "git_ops" }
            };
        }
    }

    public static string InferAdapterType(string id)
    {
        if (id.StartsWith("agy", StringComparison.OrdinalIgnoreCase)) return "agy";
        if (id.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return "claude";
        if (id.StartsWith("copilot", StringComparison.OrdinalIgnoreCase)) return "copilot";
        return "generic";
    }

    private static string FormatDisplayName(string id, string adapterType) => adapterType switch
    {
        "agy" => $"Antigravity ({id})",
        "claude" => $"Claude Code ({id})",
        "copilot" => $"GitHub Copilot ({id})",
        _ => $"Agent {id}"
    };

    private static string GetDefaultRole(string adapterType) => adapterType switch
    {
        "agy" => "Autonomous Codebase Engineer",
        "claude" => "Anthropic Terminal Architect",
        "copilot" => "Shell & Git Specialist",
        _ => "Codebase Engineer"
    };

    private static string? GetDefaultCommand(string adapterType) => adapterType switch
    {
        "agy" => "agy",
        "claude" => "claude",
        "copilot" => "gh",
        _ => null
    };

    private static List<string> GetDefaultCapabilities(string adapterType) => adapterType switch
    {
        "agy" => new List<string> { "run_command", "view_file", "replace_file_content" },
        "claude" => new List<string> { "bash", "edit_file", "read_file" },
        "copilot" => new List<string> { "explain", "suggest" },
        _ => new List<string> { "shell", "inspect_workspace" }
    };
}
