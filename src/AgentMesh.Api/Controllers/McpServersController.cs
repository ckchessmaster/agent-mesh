using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/mcp-servers")]
[Authorize]
public class McpServersController(
    AgentMeshDbContext db,
    ICredentialEncryptionService? encryptionService = null,
    IHubContext<AgentMeshHub>? hubContext = null) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetMcpServers(
        [FromQuery] bool? enabled = null,
        [FromQuery] McpTransportType? transport = null)
    {
        var query = db.McpServers.AsNoTracking().AsQueryable();

        if (enabled.HasValue)
        {
            query = query.Where(m => m.Enabled == enabled.Value);
        }

        if (transport.HasValue)
        {
            query = query.Where(m => m.Transport == transport.Value);
        }

        var servers = await query
            .OrderBy(m => m.Name)
            .ToListAsync();

        var assignmentCounts = await db.PersonaMcpServers
            .AsNoTracking()
            .GroupBy(pm => pm.McpServerId)
            .Select(g => new { McpServerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.McpServerId, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var dtos = servers
            .Select(m => ToDto(m, assignmentCounts.GetValueOrDefault(m.Id, 0), encryptionService))
            .ToList();

        return Ok(dtos);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetMcpServer(string id)
    {
        var normalizedId = McpControllerHelpers.NormalizeId(id);
        var server = await db.McpServers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == normalizedId);

        if (server == null)
        {
            return NotFound(new { error = $"MCP server '{id}' not found." });
        }

        var assignedCount = await db.PersonaMcpServers.CountAsync(pm => pm.McpServerId == normalizedId);
        return Ok(ToDto(server, assignedCount, encryptionService));
    }

    [HttpPost]
    public async Task<IActionResult> CreateMcpServer([FromBody] CreateMcpServerRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest(new { error = "MCP server Name is required." });
        }

        var trimmedName = req.Name.Trim();
        if (trimmedName.Length > 128)
        {
            return BadRequest(new { error = "MCP server Name cannot exceed 128 characters." });
        }

        var trimmedDescription = req.Description?.Trim() ?? string.Empty;
        if (trimmedDescription.Length > 1024)
        {
            return BadRequest(new { error = "MCP server Description cannot exceed 1024 characters." });
        }

        var normalizedId = !string.IsNullOrWhiteSpace(req.Id)
            ? McpControllerHelpers.NormalizeSlug(req.Id)
            : McpControllerHelpers.NormalizeSlug(trimmedName);

        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return BadRequest(new { error = "A valid MCP server Id or Name is required." });
        }

        var existing = await db.McpServers.FirstOrDefaultAsync(m => m.Id == normalizedId);
        if (existing != null)
        {
            return Conflict(new { error = $"MCP server with Id '{normalizedId}' already exists." });
        }

        if (req.TimeoutSeconds < 1 || req.TimeoutSeconds > 3600)
        {
            return BadRequest(new { error = "TimeoutSeconds must be between 1 and 3600." });
        }

        var normalizedCommand = req.Transport == McpTransportType.Stdio
            ? McpControllerHelpers.NormalizeOptional(req.Command)
            : null;
        var normalizedUrl = req.Transport != McpTransportType.Stdio
            ? McpControllerHelpers.NormalizeOptional(req.Url)
            : null;

        if (!McpControllerHelpers.TryValidateTransport(req.Transport, normalizedCommand, normalizedUrl, out var transportError))
        {
            return BadRequest(new { error = transportError });
        }

        if (!McpControllerHelpers.TryEncryptSecretMap(encryptionService, req.Env, out var envEncrypted, out var envError))
        {
            return envError!;
        }

        if (!McpControllerHelpers.TryEncryptSecretMap(encryptionService, req.Headers, out var headersEncrypted, out var headersError))
        {
            return headersError!;
        }

        var args = req.Transport == McpTransportType.Stdio && req.Args != null
            ? McpControllerHelpers.NormalizeArgsList(req.Args)
            : new List<string>();

        var now = DateTimeOffset.UtcNow;
        var server = new McpServer
        {
            Id = normalizedId,
            Name = trimmedName,
            Description = trimmedDescription,
            Transport = req.Transport,
            Command = normalizedCommand,
            ArgsJson = JsonSerializer.Serialize(args),
            Url = normalizedUrl,
            EnvEncrypted = envEncrypted,
            HeadersEncrypted = headersEncrypted,
            Enabled = req.Enabled,
            TimeoutSeconds = req.TimeoutSeconds,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var dto = ToDto(server, 0, encryptionService);
        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("McpServerCreated", dto);
        }

        return CreatedAtAction(nameof(GetMcpServer), new { id = server.Id }, dto);
    }

    [HttpPut("{id}")]
    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdateMcpServer(string id, [FromBody] UpdateMcpServerRequest req)
    {
        var normalizedId = McpControllerHelpers.NormalizeId(id);
        var server = await db.McpServers.FirstOrDefaultAsync(m => m.Id == normalizedId);
        if (server == null)
        {
            return NotFound(new { error = $"MCP server '{id}' not found." });
        }

        if (req.Name != null)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return BadRequest(new { error = "MCP server Name cannot be empty." });
            }

            var trimmedName = req.Name.Trim();
            if (trimmedName.Length > 128)
            {
                return BadRequest(new { error = "MCP server Name cannot exceed 128 characters." });
            }

            server.Name = trimmedName;
        }

        if (req.Description != null)
        {
            var trimmedDescription = req.Description.Trim();
            if (trimmedDescription.Length > 1024)
            {
                return BadRequest(new { error = "MCP server Description cannot exceed 1024 characters." });
            }

            server.Description = trimmedDescription;
        }

        if (req.TimeoutSeconds.HasValue)
        {
            if (req.TimeoutSeconds.Value < 1 || req.TimeoutSeconds.Value > 3600)
            {
                return BadRequest(new { error = "TimeoutSeconds must be between 1 and 3600." });
            }

            server.TimeoutSeconds = req.TimeoutSeconds.Value;
        }

        var nextTransport = req.Transport ?? server.Transport;
        var transportChanged = req.Transport.HasValue && req.Transport.Value != server.Transport;

        var nextCommand = req.Command != null
            ? McpControllerHelpers.NormalizeOptional(req.Command)
            : (transportChanged && nextTransport != McpTransportType.Stdio ? null : server.Command);

        var nextUrl = req.Url != null
            ? McpControllerHelpers.NormalizeOptional(req.Url)
            : (transportChanged && nextTransport == McpTransportType.Stdio ? null : server.Url);

        if (nextTransport == McpTransportType.Stdio)
        {
            nextUrl = null;
        }
        else
        {
            nextCommand = null;
        }

        if (!McpControllerHelpers.TryValidateTransport(nextTransport, nextCommand, nextUrl, out var transportError))
        {
            return BadRequest(new { error = transportError });
        }

        if (!McpControllerHelpers.TryUpdateSecretMap(
                encryptionService,
                server.EnvEncrypted,
                req.Env,
                req.MergeEnv,
                req.RemoveEnvKeys,
                out var nextEnvEncrypted,
                out var envError))
        {
            return envError!;
        }

        if (!McpControllerHelpers.TryUpdateSecretMap(
                encryptionService,
                server.HeadersEncrypted,
                req.Headers,
                req.MergeHeaders,
                req.RemoveHeaderKeys,
                out var nextHeadersEncrypted,
                out var headersError))
        {
            return headersError!;
        }

        server.Transport = nextTransport;
        server.Command = nextCommand;
        server.Url = nextUrl;

        if (nextTransport != McpTransportType.Stdio)
        {
            server.ArgsJson = "[]";
        }
        else if (req.Args != null)
        {
            server.ArgsJson = JsonSerializer.Serialize(McpControllerHelpers.NormalizeArgsList(req.Args));
        }

        server.EnvEncrypted = nextEnvEncrypted;
        server.HeadersEncrypted = nextHeadersEncrypted;

        if (req.Enabled.HasValue)
        {
            server.Enabled = req.Enabled.Value;
        }

        server.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var assignedCount = await db.PersonaMcpServers.CountAsync(pm => pm.McpServerId == normalizedId);
        var dto = ToDto(server, assignedCount, encryptionService);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("McpServerUpdated", dto);
        }

        return Ok(dto);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteMcpServer(string id)
    {
        var normalizedId = McpControllerHelpers.NormalizeId(id);
        var server = await db.McpServers.FirstOrDefaultAsync(m => m.Id == normalizedId);
        if (server == null)
        {
            return NotFound(new { error = $"MCP server '{id}' not found." });
        }

        var assignments = await db.PersonaMcpServers
            .Where(pm => pm.McpServerId == normalizedId)
            .ToListAsync();
        if (assignments.Count > 0)
        {
            db.PersonaMcpServers.RemoveRange(assignments);
        }

        db.McpServers.Remove(server);
        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("McpServerDeleted", new
            {
                id = normalizedId,
                unassignedPersonaCount = assignments.Count
            });
        }

        return Ok(new
        {
            status = "deleted",
            id = normalizedId,
            unassignedPersonaCount = assignments.Count
        });
    }

    public static McpServerDto ToDto(
        McpServer server,
        int assignedPersonaCount,
        ICredentialEncryptionService? encryption = null)
    {
        return new McpServerDto(
            Id: server.Id,
            Name: server.Name,
            Description: server.Description,
            Transport: server.Transport,
            Command: server.Command,
            Args: McpControllerHelpers.ParseStringList(server.ArgsJson),
            Url: server.Url,
            EnvMasked: McpControllerHelpers.MaskOrEmpty(encryption, server.EnvEncrypted),
            HeadersMasked: McpControllerHelpers.MaskOrEmpty(encryption, server.HeadersEncrypted),
            HasEnv: !string.IsNullOrWhiteSpace(server.EnvEncrypted),
            HasHeaders: !string.IsNullOrWhiteSpace(server.HeadersEncrypted),
            Enabled: server.Enabled,
            TimeoutSeconds: server.TimeoutSeconds,
            AssignedPersonaCount: assignedPersonaCount,
            CreatedAt: server.CreatedAt,
            UpdatedAt: server.UpdatedAt
        );
    }
}

/// <summary>
/// Shared normalization, validation, and secret-map update helpers used across
/// <see cref="McpServersController"/> and <see cref="PersonasController"/>.
/// </summary>
internal static class McpControllerHelpers
{
    public static string NormalizeId(string input) => input.Trim().ToLowerInvariant();

    public static string NormalizeSlug(string input)
    {
        var lowered = input.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lowered, @"[^a-z0-9\-_]+", "-").Trim('-');
        if (slug.Length > 64)
        {
            slug = slug[..64].Trim('-');
        }
        return slug;
    }

    public static string? NormalizeOptional(string? val) =>
        string.IsNullOrWhiteSpace(val) ? null : val.Trim();

    public static List<string> NormalizeArgsList(IEnumerable<string> args)
    {
        var normalized = new List<string>();
        foreach (var arg in args)
        {
            if (arg != null)
            {
                normalized.Add(arg);
            }
        }
        return normalized;
    }

    public static List<string> NormalizeToolList(IEnumerable<string> tools)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>();
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool))
            {
                continue;
            }

            var trimmed = tool.Trim();
            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }
        return normalized;
    }

    public static List<string> ParseStringList(string? json) =>
        ParseNullableStringList(json) ?? new List<string>();

    public static List<string>? ParseNullableStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool TryValidateTransport(
        McpTransportType transport,
        string? command,
        string? url,
        out string? error)
    {
        if (transport == McpTransportType.Stdio)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                error = "Command is required for Stdio MCP servers.";
                return false;
            }

            if (command.Length > 512)
            {
                error = "Command cannot exceed 512 characters.";
                return false;
            }

            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            error = $"Url is required for {transport} MCP servers.";
            return false;
        }

        if (url.Length > 1024)
        {
            error = "Url cannot exceed 1024 characters.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Url must be an absolute http:// or https:// URL.";
            return false;
        }

        error = null;
        return true;
    }

    public static Dictionary<string, string> MaskOrEmpty(
        ICredentialEncryptionService? encryption,
        string? cipherText)
    {
        if (encryption == null || string.IsNullOrWhiteSpace(cipherText))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return McpSecretProtector.MaskDictionary(encryption, cipherText);
    }

    public static bool TryEncryptSecretMap(
        ICredentialEncryptionService? encryption,
        IReadOnlyDictionary<string, string>? values,
        out string? encrypted,
        out IActionResult? errorResult)
    {
        if (values is null || values.Count == 0)
        {
            encrypted = null;
            errorResult = null;
            return true;
        }

        if (encryption == null)
        {
            encrypted = null;
            errorResult = new ObjectResult(new { error = "Credential encryption service is unavailable." })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
            return false;
        }

        try
        {
            encrypted = McpSecretProtector.EncryptDictionary(encryption, values);
            errorResult = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            encrypted = null;
            errorResult = new BadRequestObjectResult(new { error = ex.Message });
            return false;
        }
    }

    public static bool TryUpdateSecretMap(
        ICredentialEncryptionService? encryption,
        string? currentEncrypted,
        IReadOnlyDictionary<string, string>? incoming,
        bool merge,
        IReadOnlyCollection<string>? removeKeys,
        out string? updatedEncrypted,
        out IActionResult? errorResult)
    {
        var hasRemovals = removeKeys is { Count: > 0 };
        if (incoming is null && !hasRemovals)
        {
            updatedEncrypted = currentEncrypted;
            errorResult = null;
            return true;
        }

        if (!merge && !hasRemovals)
        {
            return TryEncryptSecretMap(encryption, incoming, out updatedEncrypted, out errorResult);
        }

        var requiresEncryption = !string.IsNullOrWhiteSpace(currentEncrypted) || incoming is { Count: > 0 };
        if (requiresEncryption && encryption == null)
        {
            updatedEncrypted = null;
            errorResult = new ObjectResult(new { error = "Credential encryption service is unavailable." })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
            return false;
        }

        var map = encryption != null && !string.IsNullOrWhiteSpace(currentEncrypted)
            ? McpSecretProtector.DecryptDictionary(encryption, currentEncrypted)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        if (hasRemovals)
        {
            foreach (var rawKey in removeKeys!)
            {
                if (!string.IsNullOrWhiteSpace(rawKey))
                {
                    map.Remove(rawKey.Trim());
                }
            }
        }

        if (incoming is { Count: > 0 })
        {
            foreach (var (rawKey, rawValue) in incoming)
            {
                if (string.IsNullOrWhiteSpace(rawKey))
                {
                    updatedEncrypted = null;
                    errorResult = new BadRequestObjectResult(new { error = "MCP secret map keys cannot be null or whitespace." });
                    return false;
                }

                map[rawKey.Trim()] = rawValue ?? string.Empty;
            }
        }

        if (map.Count == 0)
        {
            updatedEncrypted = null;
            errorResult = null;
            return true;
        }

        return TryEncryptSecretMap(encryption, map, out updatedEncrypted, out errorResult);
    }
}

public record CreateMcpServerRequest(
    string Name,
    string? Id = null,
    string? Description = null,
    McpTransportType Transport = McpTransportType.Stdio,
    string? Command = null,
    List<string>? Args = null,
    string? Url = null,
    Dictionary<string, string>? Env = null,
    Dictionary<string, string>? Headers = null,
    bool Enabled = true,
    int TimeoutSeconds = 30
);

public record UpdateMcpServerRequest(
    string? Name = null,
    string? Description = null,
    McpTransportType? Transport = null,
    string? Command = null,
    List<string>? Args = null,
    string? Url = null,
    Dictionary<string, string>? Env = null,
    Dictionary<string, string>? Headers = null,
    bool MergeEnv = false,
    bool MergeHeaders = false,
    List<string>? RemoveEnvKeys = null,
    List<string>? RemoveHeaderKeys = null,
    bool? Enabled = null,
    int? TimeoutSeconds = null
);

public record McpServerDto(
    string Id,
    string Name,
    string Description,
    McpTransportType Transport,
    string? Command,
    List<string> Args,
    string? Url,
    Dictionary<string, string> EnvMasked,
    Dictionary<string, string> HeadersMasked,
    bool HasEnv,
    bool HasHeaders,
    bool Enabled,
    int TimeoutSeconds,
    int AssignedPersonaCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
