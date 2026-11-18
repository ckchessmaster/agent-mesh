using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;

namespace AgentMesh.Cli.Services;

/// <summary>
/// Represents ephemeral per-turn MCP server provisioning for a subprocess CLI driver
/// (<c>agy</c>, <c>claude</c>, <c>copilot</c>). Disposing cleans up temporary config files or
/// restores pre-existing config state so decrypted credentials never persist across turns.
/// </summary>
public sealed class SubprocessMcpProvisionResult : IDisposable
{
    private Action? _onDispose;

    public static SubprocessMcpProvisionResult Empty { get; } =
        new([], [], 0, null, null);

    public SubprocessMcpProvisionResult(
        IReadOnlyList<string> extraArguments,
        IReadOnlyList<string> warnings,
        int provisionedServerCount,
        string? configFilePath,
        Action? onDispose)
    {
        ExtraArguments = extraArguments;
        Warnings = warnings;
        ProvisionedServerCount = provisionedServerCount;
        ConfigFilePath = configFilePath;
        _onDispose = onDispose;
    }

    public IReadOnlyList<string> ExtraArguments { get; }
    public IReadOnlyList<string> Warnings { get; }
    public int ProvisionedServerCount { get; }
    public string? ConfigFilePath { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}

/// <summary>
/// Generates per-turn MCP configurations for subprocess drivers (<c>agy</c>, <c>claude</c>,
/// <c>copilot</c>) using <c>0600</c> credential files and deterministic cleanup.
/// </summary>
public static class SubprocessMcpProvisioner
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    private sealed record NormalizedMcpServer(
        string Key,
        string DisplayName,
        ResolvedMcpServerConfig Source,
        List<string>? EffectiveAllowedTools,
        List<string> EffectiveBlockedTools);

    public static bool ShouldProvision(IReadOnlyList<ResolvedMcpServerConfig>? servers, string? mode)
    {
        if (string.Equals(mode?.Trim(), "direct", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return servers is { Count: > 0 };
    }

    public static SubprocessMcpProvisionResult ProvisionForClaude(
        IReadOnlyList<ResolvedMcpServerConfig>? servers,
        string? mode = null,
        string? tempDirectory = null)
    {
        if (!ShouldProvision(servers, mode))
        {
            return SubprocessMcpProvisionResult.Empty;
        }

        var warnings = new List<string>();
        var normalized = NormalizeServers(servers!, warnings);
        var mcpServersObj = new JsonObject();
        var disallowedTools = new List<string>();
        var provisionedCount = 0;

        foreach (var entry in normalized)
        {
            var src = entry.Source;
            var serverObj = new JsonObject();

            if (src.Transport == McpTransportType.Stdio)
            {
                serverObj["type"] = "stdio";
                serverObj["command"] = src.Command!.Trim();
                if (src.Args is { Count: > 0 })
                {
                    serverObj["args"] = ToJsonArray(src.Args);
                }
                if (src.Env is { Count: > 0 })
                {
                    serverObj["env"] = ToJsonObject(src.Env);
                }
            }
            else
            {
                serverObj["type"] = src.Transport == McpTransportType.Sse ? "sse" : "http";
                serverObj["url"] = src.Url!.Trim();
                if (src.Headers is { Count: > 0 })
                {
                    serverObj["headers"] = ToJsonObject(src.Headers);
                }
            }

            if (entry.EffectiveAllowedTools != null)
            {
                warnings.Add(
                    $"MCP server '{entry.DisplayName}' ({entry.Key}) defines an allowedTools allowlist, which Claude CLI does not enforce per-server; blockedTools are enforced via --disallowedTools.");
            }

            foreach (var blocked in entry.EffectiveBlockedTools)
            {
                disallowedTools.Add($"mcp__{entry.Key}__{blocked}");
            }

            mcpServersObj[entry.Key] = serverObj;
            provisionedCount++;
        }

        if (provisionedCount == 0)
        {
            return new SubprocessMcpProvisionResult([], warnings, 0, null, null);
        }

        var root = new JsonObject { ["mcpServers"] = mcpServersObj };
        var configPath = CreateTempConfigPath("claude", tempDirectory);
        WriteSecureFile(configPath, root.ToJsonString(JsonWriteOptions));

        var args = new List<string>
        {
            "--mcp-config",
            configPath,
            "--strict-mcp-config"
        };

        if (disallowedTools.Count > 0)
        {
            args.Add("--disallowedTools");
            args.Add(string.Join(",", disallowedTools.Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        return new SubprocessMcpProvisionResult(
            args,
            warnings,
            provisionedCount,
            configPath,
            () => TryDeleteFile(configPath));
    }

    public static SubprocessMcpProvisionResult ProvisionForCopilot(
        IReadOnlyList<ResolvedMcpServerConfig>? servers,
        string? mode = null,
        string? tempDirectory = null)
    {
        if (!ShouldProvision(servers, mode))
        {
            return SubprocessMcpProvisionResult.Empty;
        }

        var warnings = new List<string>();
        var normalized = NormalizeServers(servers!, warnings);
        var mcpServersObj = new JsonObject();
        var denyRules = new List<string>();
        var provisionedCount = 0;

        foreach (var entry in normalized)
        {
            var src = entry.Source;
            var serverObj = new JsonObject();

            if (src.Transport == McpTransportType.Stdio)
            {
                serverObj["type"] = "local";
                serverObj["command"] = src.Command!.Trim();
                serverObj["args"] = ToJsonArray(src.Args ?? []);
                if (src.Env is { Count: > 0 })
                {
                    serverObj["env"] = ToJsonObject(src.Env);
                }
            }
            else
            {
                serverObj["type"] = src.Transport == McpTransportType.Sse ? "sse" : "http";
                serverObj["url"] = src.Url!.Trim();
                if (src.Headers is { Count: > 0 })
                {
                    serverObj["headers"] = ToJsonObject(src.Headers);
                }
            }

            serverObj["tools"] = entry.EffectiveAllowedTools != null
                ? ToJsonArray(entry.EffectiveAllowedTools)
                : new JsonArray("*");

            foreach (var blocked in entry.EffectiveBlockedTools)
            {
                denyRules.Add($"{entry.Key}({blocked})");
            }

            mcpServersObj[entry.Key] = serverObj;
            provisionedCount++;
        }

        if (provisionedCount == 0)
        {
            return new SubprocessMcpProvisionResult([], warnings, 0, null, null);
        }

        var root = new JsonObject { ["mcpServers"] = mcpServersObj };
        var configPath = CreateTempConfigPath("copilot", tempDirectory);
        WriteSecureFile(configPath, root.ToJsonString(JsonWriteOptions));

        var args = new List<string>
        {
            "--additional-mcp-config",
            $"@{configPath}"
        };

        foreach (var rule in denyRules.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--deny-tool");
            args.Add(rule);
        }

        return new SubprocessMcpProvisionResult(
            args,
            warnings,
            provisionedCount,
            configPath,
            () => TryDeleteFile(configPath));
    }

    public static SubprocessMcpProvisionResult ProvisionForAgy(
        IReadOnlyList<ResolvedMcpServerConfig>? servers,
        string? mode = null,
        string? homeDirectory = null)
    {
        if (!ShouldProvision(servers, mode))
        {
            return SubprocessMcpProvisionResult.Empty;
        }

        var effectiveHome = string.IsNullOrWhiteSpace(homeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : homeDirectory;

        if (string.IsNullOrWhiteSpace(effectiveHome))
        {
            return new SubprocessMcpProvisionResult(
                [],
                ["Unable to resolve user home directory for agy mcp_config.json provisioning."],
                0,
                null,
                null);
        }

        var warnings = new List<string>();
        var normalized = NormalizeServers(servers!, warnings);
        var provisionedServers = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var entry in normalized)
        {
            var src = entry.Source;
            if (src.Transport == McpTransportType.Sse)
            {
                warnings.Add(
                    $"MCP server '{entry.DisplayName}' ({entry.Key}) uses SSE transport, which agy does not support natively (use Streamable HTTP or a stdio bridge).");
                continue;
            }

            var serverObj = new JsonObject();
            if (src.Transport == McpTransportType.Stdio)
            {
                serverObj["command"] = src.Command!.Trim();
                if (src.Args is { Count: > 0 })
                {
                    serverObj["args"] = ToJsonArray(src.Args);
                }
                if (src.Env is { Count: > 0 })
                {
                    serverObj["env"] = ToJsonObject(src.Env);
                }
            }
            else
            {
                serverObj["serverUrl"] = src.Url!.Trim();
                if (src.Headers is { Count: > 0 })
                {
                    serverObj["headers"] = ToJsonObject(src.Headers);
                }
            }

            serverObj["disabled"] = false;
            if (src.TimeoutSeconds > 0)
            {
                serverObj["timeoutSeconds"] = src.TimeoutSeconds;
            }
            if (entry.EffectiveAllowedTools != null)
            {
                serverObj["enabledTools"] = ToJsonArray(entry.EffectiveAllowedTools);
            }
            if (entry.EffectiveBlockedTools.Count > 0)
            {
                serverObj["disabledTools"] = ToJsonArray(entry.EffectiveBlockedTools);
            }

            provisionedServers[entry.Key] = serverObj;
        }

        if (provisionedServers.Count == 0)
        {
            return new SubprocessMcpProvisionResult([], warnings, 0, null, null);
        }

        var mcpConfigPath = Path.Combine(effectiveHome, ".gemini", "config", "mcp_config.json");
        var lease = AgyMcpConfigCoordinator.Acquire(mcpConfigPath, provisionedServers);

        return new SubprocessMcpProvisionResult(
            [],
            warnings,
            provisionedServers.Count,
            mcpConfigPath,
            lease.Dispose);
    }

    private static List<NormalizedMcpServer> NormalizeServers(
        IReadOnlyList<ResolvedMcpServerConfig> servers,
        List<string> warnings)
    {
        var result = new List<NormalizedMcpServer>(servers.Count);
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers)
        {
            if (server == null) continue;

            var displayName = !string.IsNullOrWhiteSpace(server.Name)
                ? server.Name.Trim()
                : (!string.IsNullOrWhiteSpace(server.Id) ? server.Id.Trim() : "mcp-server");
            var key = NormalizeServerKey(server, usedKeys);

            if (server.Transport == McpTransportType.Stdio)
            {
                if (string.IsNullOrWhiteSpace(server.Command))
                {
                    warnings.Add($"MCP server '{displayName}' ({key}) has no command configured; skipping.");
                    continue;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(server.Url) ||
                    !Uri.TryCreate(server.Url.Trim(), UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    warnings.Add($"MCP server '{displayName}' ({key}) has an invalid URL; skipping.");
                    continue;
                }
            }

            var blocked = (server.BlockedTools ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var blockedSet = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);
            List<string>? allowed = null;
            if (server.AllowedTools != null)
            {
                allowed = server.AllowedTools
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .Where(t => !blockedSet.Contains(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (allowed.Count == 0)
                {
                    warnings.Add($"MCP server '{displayName}' ({key}) has no allowed tools after filtering; skipping.");
                    continue;
                }
            }

            result.Add(new NormalizedMcpServer(key, displayName, server, allowed, blocked));
        }

        return result;
    }

    private static string NormalizeServerKey(ResolvedMcpServerConfig server, ISet<string> usedKeys)
    {
        var raw = !string.IsNullOrWhiteSpace(server.Id) ? server.Id : server.Name;
        var clean = string.IsNullOrWhiteSpace(raw)
            ? "mcp-server"
            : Regex.Replace(raw.Trim(), @"[^a-zA-Z0-9_-]", "_");
        clean = Regex.Replace(clean, @"_{2,}", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(clean))
        {
            clean = "mcp-server";
        }

        if (usedKeys.Add(clean))
        {
            return clean;
        }

        var counter = 2;
        while (true)
        {
            var candidate = $"{clean}_{counter++}";
            if (usedKeys.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static JsonArray ToJsonArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
        {
            arr.Add(item);
        }
        return arr;
    }

    private static JsonObject ToJsonObject(IDictionary<string, string> map)
    {
        var obj = new JsonObject();
        foreach (var kvp in map)
        {
            obj[kvp.Key] = kvp.Value;
        }
        return obj;
    }

    private static string CreateTempConfigPath(string adapter, string? tempDirectory)
    {
        var baseDir = string.IsNullOrWhiteSpace(tempDirectory) ? Path.GetTempPath() : tempDirectory;
        Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, $"agentmesh-{adapter}-mcp-{Guid.NewGuid():N}.json");
    }

    private static void WriteSecureFile(string filePath, string contents)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(filePath, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(contents);
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Best effort on filesystems without POSIX mode support
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors on already-removed temp files
        }
    }

    /// <summary>
    /// Coordinates per-turn writes to <c>~/.gemini/config/mcp_config.json</c> so pre-existing
    /// user servers are preserved and the exact original file state (or absence) is restored once
    /// all active leases finish.
    /// </summary>
    private static class AgyMcpConfigCoordinator
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<string, PathState> States = new(StringComparer.Ordinal);

        private sealed class PathState
        {
            public bool OriginalExisted { get; set; }
            public byte[]? OriginalBytes { get; set; }
            public Dictionary<Guid, Dictionary<string, JsonObject>> ActiveLeases { get; } = new();
        }

        public static IDisposable Acquire(string path, Dictionary<string, JsonObject> servers)
        {
            var fullPath = Path.GetFullPath(path);
            var leaseId = Guid.NewGuid();

            lock (Sync)
            {
                if (!States.TryGetValue(fullPath, out var state))
                {
                    state = new PathState();
                    if (File.Exists(fullPath))
                    {
                        state.OriginalExisted = true;
                        state.OriginalBytes = File.ReadAllBytes(fullPath);
                    }
                    States[fullPath] = state;
                }

                state.ActiveLeases[leaseId] = servers;
                WriteMergedConfig(fullPath, state);
            }

            return new LeaseDisposable(() => Release(fullPath, leaseId));
        }

        private static void Release(string fullPath, Guid leaseId)
        {
            lock (Sync)
            {
                if (!States.TryGetValue(fullPath, out var state))
                {
                    return;
                }

                state.ActiveLeases.Remove(leaseId);

                if (state.ActiveLeases.Count == 0)
                {
                    States.Remove(fullPath);
                    RestoreOriginal(fullPath, state);
                }
                else
                {
                    WriteMergedConfig(fullPath, state);
                }
            }
        }

        private static void WriteMergedConfig(string fullPath, PathState state)
        {
            var rootObj = ParseOriginalRootOrNew(state.OriginalBytes);
            var mcpServers = rootObj["mcpServers"] as JsonObject ?? new JsonObject();
            rootObj["mcpServers"] = mcpServers;

            foreach (var leaseServers in state.ActiveLeases.Values)
            {
                foreach (var kvp in leaseServers)
                {
                    mcpServers[kvp.Key] = JsonNode.Parse(kvp.Value.ToJsonString());
                }
            }

            WriteSecureFile(fullPath, rootObj.ToJsonString(JsonWriteOptions));
        }

        private static JsonObject ParseOriginalRootOrNew(byte[]? originalBytes)
        {
            if (originalBytes == null || originalBytes.Length == 0)
            {
                return new JsonObject();
            }

            try
            {
                var text = Encoding.UTF8.GetString(originalBytes);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new JsonObject();
                }

                if (JsonNode.Parse(text) is JsonObject obj)
                {
                    return obj;
                }
            }
            catch
            {
                // Treat corrupt/empty original file as empty JSON object while still preserving OriginalBytes for restore
            }

            return new JsonObject();
        }

        private static void RestoreOriginal(string fullPath, PathState state)
        {
            try
            {
                if (state.OriginalExisted)
                {
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllBytes(fullPath, state.OriginalBytes ?? Array.Empty<byte>());
                    if (!OperatingSystem.IsWindows())
                    {
                        try
                        {
                            File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        }
                        catch
                        {
                        }
                    }
                }
                else
                {
                    TryDeleteFile(fullPath);
                }
            }
            catch
            {
                // Best-effort restore on disposal
            }
        }

        private sealed class LeaseDisposable(Action onDispose) : IDisposable
        {
            private Action? _onDispose = onDispose;
            public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }
}
