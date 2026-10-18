using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class SystemController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, List<ModelCapabilityDto> Models)> ModelCache = new();

    [HttpGet("capabilities")]
    public async Task<IActionResult> GetCapabilities([FromQuery] string? agentId = null)
    {
        var currentDir = Environment.CurrentDirectory;
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projectsDir = Path.Combine(userHome, "projects");

        var bookmarks = new List<DirectoryBookmarkDto>
        {
            new("Default Sandbox", "", "Dedicated isolated workspace under ~/.agentmesh/workspaces/"),
            new("Current Project", currentDir, currentDir),
            new("User Home", userHome, userHome)
        };

        if (Directory.Exists(projectsDir))
        {
            bookmarks.Add(new("Projects Directory", projectsDir, projectsDir));
        }

        var agyModels = await GetAgyModelsAsync();

        var runtimes = new Dictionary<string, RuntimeCapabilitiesDto>(StringComparer.OrdinalIgnoreCase)
        {
            ["agy"] = new(
                Adapter: "agy",
                Name: "Antigravity CLI (agy)",
                Modes: new List<ModeOptionDto>
                {
                    new("auto", "Auto (Autonomous)", "Autonomous execution with step-by-step tool invocation"),
                    new("plan", "Plan Only", "Explores codebase and produces an architectural plan without making edits"),
                    new("accept-edits", "Accept Edits", "Automatically approves and applies code edits")
                },
                Models: agyModels
            ),
            ["copilot"] = new(
                Adapter: "copilot",
                Name: "GitHub Copilot CLI",
                Modes: new List<ModeOptionDto>
                {
                    new("autopilot", "Autopilot", "Executes prompt autonomously up to continuation limit"),
                    new("plan", "Plan Mode", "Analyzes task and creates an implementation plan"),
                    new("interactive", "Interactive", "Standard interactive guidance and chat")
                },
                Models: new List<ModelCapabilityDto>
                {
                    // GPT Models
                    new("gpt-6.1-sol", "GPT-6.1 Sol", new[] { "low", "medium", "high" }),
                    new("gpt-6-sol", "GPT-6 Sol", new[] { "low", "medium", "high" }),
                    new("gpt-6-luna", "GPT-6 Luna", new[] { "low", "medium", "high" }),
                    new("gpt-6-astra", "GPT-6 Astra", new[] { "low", "medium", "high" }),
                    new("gpt-5.6-sol", "GPT-5.6 Sol", new[] { "low", "medium", "high" }),
                    new("gpt-5.6-terra", "GPT-5.6 Terra", new[] { "low", "medium", "high" }),
                    new("gpt-5.6-luna", "GPT-5.6 Luna", new[] { "low", "medium", "high" }),
                    new("gpt-5.5", "GPT-5.5", new[] { "low", "medium", "high" }),
                    new("gpt-5.4", "GPT-5.4", new[] { "low", "medium", "high" }),
                    new("gpt-5.4-mini", "GPT-5.4 Mini", Array.Empty<string>()),
                    new("gpt-5.3-codex", "GPT-5.3 Codex", new[] { "low", "medium", "high" }),
                    new("gpt-5-mini", "GPT-5 Mini", Array.Empty<string>()),
                    // Claude Models
                    new("claude-sonnet-5.5", "Claude Sonnet 5.5", new[] { "low", "medium", "high" }),
                    new("claude-sonnet-5", "Claude Sonnet 5", new[] { "low", "medium", "high" }),
                    new("claude-fable-5.1", "Claude Fable 5.1", new[] { "low", "medium", "high" }),
                    new("claude-fable-5", "Claude Fable 5", new[] { "low", "medium", "high" }),
                    new("claude-opus-5.5", "Claude Opus 5.5", new[] { "low", "medium", "high" }),
                    new("claude-opus-5", "Claude Opus 5", new[] { "low", "medium", "high" }),
                    new("claude-opus-4.8", "Claude Opus 4.8", new[] { "low", "medium", "high" }),
                    new("claude-opus-4.8-fast", "Claude Opus 4.8 Fast", Array.Empty<string>()),
                    new("claude-sonnet-4.6", "Claude Sonnet 4.6", new[] { "low", "medium", "high" }),
                    new("claude-haiku-5.5", "Claude Haiku 5.5", Array.Empty<string>()),
                    new("claude-haiku-4.5", "Claude Haiku 4.5", Array.Empty<string>()),
                    // Specialized & Reasoning Models
                    new("mai-code-1.1-flash", "MAI Code 1.1 Flash", Array.Empty<string>()),
                    new("gemini-4-argon", "Gemini 4 Argon", new[] { "low", "medium", "high" }),
                    new("gemini-3.8-flash", "Gemini 3.8 Flash", new[] { "low", "medium", "high" }),
                    new("gemini-3.7-flash", "Gemini 3.7 Flash", new[] { "low", "medium", "high" }),
                    new("grok-4.7", "Grok 4.7", new[] { "low", "medium", "high" }),
                    new("grok-4.6", "Grok 4.6", new[] { "low", "medium", "high" }),
                    new("grok-4.5", "Grok 4.5", new[] { "low", "medium", "high" }),
                    new("kimi-k3", "Kimi k3", new[] { "low", "medium", "high" })
                },
                Tiers: new List<TierOptionDto>
                {
                    new("auto", "Auto", "Automatically select the best available model for each task"),
                    new("efficiency", "Efficiency", "Favors faster models while preserving quality needed for the task"),
                    new("balance", "Balance", "Balances model quality and speed while adapting to each task"),
                    new("intelligence", "Intelligence", "Favors stronger models for complex work while remaining adaptive")
                }
            ),
            ["claude"] = new(
                Adapter: "claude",
                Name: "Claude Code CLI",
                Modes: new List<ModeOptionDto>
                {
                    new("auto", "Auto", "Autonomous task execution"),
                    new("plan", "Plan", "Planning only")
                },
                Models: new List<ModelCapabilityDto>
                {
                    new("claude-3-7-sonnet", "Claude 3.7 Sonnet", new[] { "low", "medium", "high" }),
                    new("claude-3-5-sonnet", "Claude 3.5 Sonnet", Array.Empty<string>())
                }
            ),
            ["generic"] = new(
                Adapter: "generic",
                Name: "Custom Command Agent",
                Modes: new List<ModeOptionDto>(),
                Models: new List<ModelCapabilityDto>()
            )
        };

        return Ok(new SystemCapabilitiesResponse(
            Workspace: new WorkspaceDefaultsDto(
                DefaultSandboxPath: "~/.agentmesh/workspaces/<session-id>",
                CurrentDirectory: currentDir,
                UserHome: userHome,
                Bookmarks: bookmarks
            ),
            AgentRuntimes: runtimes
        ));
    }

    [HttpGet("filesystem")]
    public IActionResult BrowseFilesystem([FromQuery] string? path = null)
    {
        var targetPath = !string.IsNullOrWhiteSpace(path)
            ? Path.GetFullPath(path)
            : Environment.CurrentDirectory;

        if (!Directory.Exists(targetPath))
        {
            targetPath = Directory.Exists(Environment.CurrentDirectory)
                ? Environment.CurrentDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        string? parentPath = null;
        try
        {
            var parent = Directory.GetParent(targetPath);
            parentPath = parent?.FullName;
        }
        catch { }

        var subDirs = new List<DirectoryItemDto>();
        try
        {
            var dirInfo = new DirectoryInfo(targetPath);
            foreach (var sub in dirInfo.GetDirectories())
            {
                // Skip hidden directories and build/package noise
                if (sub.Name.StartsWith('.') ||
                    sub.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    sub.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    sub.Name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                subDirs.Add(new DirectoryItemDto(sub.Name, sub.FullName));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Restricted access
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to read directory '{targetPath}': {ex.Message}" });
        }

        subDirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return Ok(new FilesystemBrowseResponse(
            CurrentPath: targetPath,
            ParentPath: parentPath,
            Directories: subDirs
        ));
    }

    private static async Task<List<ModelCapabilityDto>> GetAgyModelsAsync()
    {
        if (ModelCache.TryGetValue("agy", out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(10))
        {
            return cached.Models;
        }

        var models = new List<ModelCapabilityDto>();
        var agyPath = ResolveExecutablePath("agy");

        if (agyPath != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var psi = new ProcessStartInfo
                {
                    FileName = agyPath,
                    Arguments = "models",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var lines = new List<string>();
                while (await proc.StandardOutput.ReadLineAsync(cts.Token) is { } line)
                {
                    lines.Add(line);
                }
                await proc.WaitForExitAsync(cts.Token);

                // Group by model base name
                var grouped = new Dictionary<string, (string DisplayName, HashSet<string> Efforts)>(StringComparer.OrdinalIgnoreCase);

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('⠋') || trimmed.StartsWith('⠙')) continue;

                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    var modelId = parts[0];
                    // Example: gemini-4-argon-medium -> base: gemini-4-argon, effort: medium
                    var effort = ExtractEffortFromModelId(modelId);
                    var baseId = effort != null ? modelId[..(modelId.Length - effort.Length - 1)] : modelId;
                    var displayName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : baseId;

                    // Clean (Medium) / (High) from display name
                    if (effort != null && displayName.EndsWith($"({effort})", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = displayName[..^(effort.Length + 2)].Trim();
                    }

                    if (!grouped.TryGetValue(baseId, out var entry))
                    {
                        entry = (displayName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        grouped[baseId] = entry;
                    }
                    if (effort != null)
                    {
                        entry.Efforts.Add(effort.ToLowerInvariant());
                    }
                }

                foreach (var (baseId, (displayName, efforts)) in grouped)
                {
                    models.Add(new ModelCapabilityDto(
                        Id: baseId,
                        Name: displayName,
                        Efforts: efforts.OrderBy(e => EffortOrder(e)).ToList()
                    ));
                }
            }
            catch
            {
                // Fallback to static catalog on timeout or error
            }
        }

        if (models.Count == 0)
        {
            models = GetDefaultAgyCatalog();
        }

        ModelCache["agy"] = (DateTimeOffset.UtcNow, models);
        return models;
    }

    private static string? ExtractEffortFromModelId(string id)
    {
        if (id.EndsWith("-low", StringComparison.OrdinalIgnoreCase)) return "low";
        if (id.EndsWith("-medium", StringComparison.OrdinalIgnoreCase)) return "medium";
        if (id.EndsWith("-high", StringComparison.OrdinalIgnoreCase)) return "high";
        if (id.EndsWith("-xhigh", StringComparison.OrdinalIgnoreCase)) return "xhigh";
        if (id.EndsWith("-max", StringComparison.OrdinalIgnoreCase)) return "max";
        return null;
    }

    private static int EffortOrder(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => 1,
        "medium" => 2,
        "high" => 3,
        "xhigh" => 4,
        "max" => 5,
        _ => 99
    };

    private static List<ModelCapabilityDto> GetDefaultAgyCatalog() => new()
    {
        new("gemini-4-argon", "Gemini 4 Argon", new[] { "low", "medium", "high" }),
        new("gemini-3.8-flash", "Gemini 3.8 Flash", new[] { "low", "medium", "high" }),
        new("claude-opus-5-5", "Claude Opus 5.5", new[] { "low", "medium", "high" }),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", new[] { "low", "medium", "high" }),
        new("gpt-oss-120b", "GPT-OSS 120B", new[] { "medium" })
    };

    private static string? ResolveExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (System.IO.File.Exists(command)) return Path.GetFullPath(command);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidateDirs = new List<string>();

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidateDirs.AddRange(pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        if (!string.IsNullOrWhiteSpace(home))
        {
            candidateDirs.Add(Path.Combine(home, ".local", "bin"));
            candidateDirs.Add(Path.Combine(home, ".gemini", "antigravity-cli", "bin"));
        }

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "", ".exe", ".cmd", ".bat", ".ps1" }
            : new[] { "" };

        foreach (var dir in candidateDirs)
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var fullPath = Path.Combine(dir, command + ext);
                    if (System.IO.File.Exists(fullPath)) return fullPath;
                }
                catch { }
            }
        }

        return null;
    }
}

public record DirectoryBookmarkDto(string Label, string Path, string Description);

public record WorkspaceDefaultsDto(
    string DefaultSandboxPath,
    string CurrentDirectory,
    string UserHome,
    List<DirectoryBookmarkDto> Bookmarks
);

public record ModeOptionDto(string Id, string Label, string Description);

public record TierOptionDto(string Id, string Label, string Description);

public record ModelCapabilityDto(string Id, string Name, IReadOnlyCollection<string> Efforts);

public record RuntimeCapabilitiesDto(
    string Adapter,
    string Name,
    List<ModeOptionDto> Modes,
    List<ModelCapabilityDto> Models,
    List<TierOptionDto>? Tiers = null
);

public record SystemCapabilitiesResponse(
    WorkspaceDefaultsDto Workspace,
    Dictionary<string, RuntimeCapabilitiesDto> AgentRuntimes
);

public record DirectoryItemDto(string Name, string Path);

public record FilesystemBrowseResponse(
    string CurrentPath,
    string? ParentPath,
    List<DirectoryItemDto> Directories
);
