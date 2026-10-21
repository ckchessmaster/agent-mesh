using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SessionsController(
    AgentMeshDbContext db,
    IHubContext<AgentMeshHub> hubContext,
    IStorageService? storageService = null) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public const long MaxAttachmentSizeBytes = 25 * 1024 * 1024; // 25 MB per file
    public const int MaxAttachmentsPerMessage = 10;
    public const string DefaultAttachmentPrompt = "Please review the attached file(s).";

    private static readonly HashSet<string> AllowedAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico",
        // PDFs
        ".pdf",
        // Markdown / Text / Docs
        ".md", ".markdown", ".txt", ".log", ".rst", ".adoc",
        // Data & Config
        ".json", ".jsonl", ".csv", ".tsv", ".yaml", ".yml", ".toml", ".xml", ".ini", ".env", ".conf", ".config", ".properties",
        // Source Code
        ".cs", ".csproj", ".sln", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py", ".go", ".rs", ".java", ".kt", ".swift",
        ".c", ".cpp", ".h", ".hpp", ".rb", ".php", ".sh", ".bash", ".zsh", ".ps1", ".sql", ".graphql", ".gql", ".html", ".htm",
        ".css", ".scss", ".less", ".vue", ".svelte", ".dockerfile", ".makefile", ".proto", ".tf", ".hcl", ".bicep", ".gradle",
        // Archives
        ".zip", ".tar", ".gz", ".tgz"
    };

    public static bool IsAllowedAttachment(string? fileName, string? contentType)
        => IsAllowedAttachment(fileName, contentType, out _, out _);

    public static bool IsAllowedAttachment(string? fileName, string? contentType, out string resolvedContentType, out string? errorReason)
    {
        resolvedContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();
        errorReason = null;

        var ext = Path.GetExtension(fileName ?? string.Empty)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ext))
        {
            if (resolvedContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                resolvedContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
                resolvedContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                resolvedContentType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            errorReason = $"File '{fileName}' is missing a supported file extension.";
            return false;
        }

        if (!AllowedAttachmentExtensions.Contains(ext))
        {
            errorReason = $"File extension '{ext}' is not allowed. Supported types: images, PDFs, markdown/text, source code, JSON/CSV/YAML/logs, and ZIP.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(contentType) || resolvedContentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            resolvedContentType = GuessContentTypeFromExtension(ext);
        }

        return true;
    }

    public static string GuessContentTypeFromExtension(string fileNameOrExt)
    {
        var trimmed = (fileNameOrExt ?? string.Empty).Trim();
        var ext = trimmed.StartsWith('.') ? trimmed : Path.GetExtension(trimmed);
        return ext.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            ".pdf" => "application/pdf",
            ".md" or ".markdown" => "text/markdown",
            ".txt" or ".log" or ".rst" or ".adoc" => "text/plain",
            ".json" or ".jsonl" => "application/json",
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".yaml" or ".yml" => "application/yaml",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".css" or ".scss" or ".less" => "text/css",
            ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" => "text/javascript",
            ".zip" => "application/zip",
            ".gz" or ".tgz" => "application/gzip",
            ".tar" => "application/x-tar",
            _ => "text/plain"
        };
    }
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetSessions(
        [FromQuery] string? status = null,
        [FromQuery] string? creatorAgentId = null,
        [FromQuery] string? driverId = null,
        [FromQuery] string? personaId = null)
    {
        var query = db.AgentSessions.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(s => s.Status.ToLower() == status.Trim().ToLower());
        }

        var effectiveDriverId = !string.IsNullOrWhiteSpace(driverId) ? driverId : creatorAgentId;
        if (!string.IsNullOrWhiteSpace(effectiveDriverId))
        {
            var normDriver = effectiveDriverId.Trim().ToLowerInvariant();
            query = query.Where(s => s.DriverId == normDriver || s.CreatorAgentId == normDriver);
        }

        if (!string.IsNullOrWhiteSpace(personaId))
        {
            var normPersona = personaId.Trim().ToLowerInvariant();
            query = query.Where(s => s.PersonaId == normPersona);
        }

        var drivers = await db.Agents
            .AsNoTracking()
            .ToDictionaryAsync(a => a.Id, a => a.Name, StringComparer.OrdinalIgnoreCase);

        var personas = await db.Personas
            .AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);

        var rawSessions = await query
            .OrderByDescending(s => s.UpdatedAt)
            .Take(100)
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.Status,
                s.MetadataJson,
                s.CreatorAgentId,
                s.DriverId,
                s.PersonaId,
                MessageCount = s.Messages.Count,
                s.CreatedAt,
                s.UpdatedAt
            })
            .ToListAsync();

        var sessions = rawSessions.Select(s =>
        {
            var dId = s.DriverId ?? s.CreatorAgentId;
            return new SessionSummaryDto(
                s.Id,
                s.Title,
                s.Status,
                s.MetadataJson,
                dId,
                s.MessageCount,
                s.CreatedAt,
                s.UpdatedAt,
                DriverId: dId,
                DriverName: dId != null ? drivers.GetValueOrDefault(dId, dId) : null,
                PersonaId: s.PersonaId,
                PersonaName: s.PersonaId != null ? personas.GetValueOrDefault(s.PersonaId, s.PersonaId) : null
            );
        }).ToList();

        return Ok(sessions);
    }

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSession(Guid id)
    {
        var session = await db.AgentSessions
            .AsNoTracking()
            .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(s => s.Id == id);

        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var effectiveDriverId = session.DriverId ?? session.CreatorAgentId;
        string? creatorAgentName = null;
        if (!string.IsNullOrWhiteSpace(effectiveDriverId))
        {
            creatorAgentName = await db.Agents
                .AsNoTracking()
                .Where(a => a.Id == effectiveDriverId)
                .Select(a => a.Name)
                .FirstOrDefaultAsync();
        }

        string? personaName = null;
        string? personaRole = null;
        if (!string.IsNullOrWhiteSpace(session.PersonaId))
        {
            var persona = await db.Personas
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == session.PersonaId);
            personaName = persona?.Name;
            personaRole = persona?.Role;
        }

        var dto = new SessionDetailDto(
            session.Id,
            session.Title,
            session.Status,
            session.MetadataJson,
            effectiveDriverId,
            creatorAgentName,
            session.CreatedAt,
            session.UpdatedAt,
            session.Messages.Select(ToMessageDto).ToList(),
            DriverId: effectiveDriverId,
            DriverName: creatorAgentName ?? effectiveDriverId,
            PersonaId: session.PersonaId,
            PersonaName: personaName,
            PersonaRole: personaRole
        );

        return Ok(dto);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteSession(Guid id)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var effectiveDriverId = session.DriverId ?? session.CreatorAgentId;
        var personaId = session.PersonaId;

        // 1. Halt any running execution
        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("StopExecution", new
            {
                sessionId = session.Id,
                agentId = effectiveDriverId
            });
            if (!string.IsNullOrWhiteSpace(effectiveDriverId))
            {
                await hubContext.Clients.Group($"agent-{effectiveDriverId}").SendAsync("StopExecution", new
                {
                    sessionId = session.Id,
                    agentId = effectiveDriverId
                });
            }
        }

        // 2. Remove local disk artifacts if present
        try
        {
            var basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentmesh", "artifacts", session.Id.ToString());
            if (Directory.Exists(basePath))
            {
                Directory.Delete(basePath, true);
            }
        }
        catch
        {
            // Ignore artifact folder deletion failures
        }

        // 3. Delete session from DB (cascades to messages and jobs)
        db.AgentSessions.Remove(session);
        await db.SaveChangesAsync();

        // 4. Broadcast SessionDeleted event via SignalR
        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("SessionDeleted", new
            {
                id = session.Id.ToString(),
                sessionId = session.Id,
                personaId,
                driverId = effectiveDriverId
            });
        }

        return Ok(new
        {
            status = "deleted",
            id = session.Id,
            personaId,
            driverId = effectiveDriverId
        });
    }

    public static string MergeSessionMetadata(
        string? existingJson,
        string? workingDirectory,
        string? model,
        string? effort,
        string? mode,
        string? tier = null,
        string? personaId = null,
        string? personaName = null,
        string? personaRole = null,
        string? personaPrompt = null,
        string? contextSize = null)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(existingJson);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed) dict[k] = v;
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory)) dict["workingDirectory"] = workingDirectory.Trim();
        if (!string.IsNullOrWhiteSpace(model)) dict["model"] = model.Trim();
        if (effort != null)
        {
            if (string.IsNullOrWhiteSpace(effort)) dict.Remove("effort");
            else dict["effort"] = effort.Trim();
        }
        if (!string.IsNullOrWhiteSpace(mode)) dict["mode"] = mode.Trim();
        if (!string.IsNullOrWhiteSpace(tier)) dict["tier"] = tier.Trim();
        if (contextSize != null)
        {
            if (string.IsNullOrWhiteSpace(contextSize)) dict.Remove("contextSize");
            else dict["contextSize"] = contextSize.Trim();
        }
        if (!string.IsNullOrWhiteSpace(personaId)) dict["personaId"] = personaId.Trim();
        if (!string.IsNullOrWhiteSpace(personaName)) dict["personaName"] = personaName.Trim();
        if (!string.IsNullOrWhiteSpace(personaRole)) dict["personaRole"] = personaRole.Trim();
        if (!string.IsNullOrWhiteSpace(personaPrompt)) dict["personaPrompt"] = personaPrompt.Trim();

        return System.Text.Json.JsonSerializer.Serialize(dict);
    }

    public static (string? personaId, string? personaName, string? personaRole, string? personaPrompt) ExtractSessionPersona(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return (null, null, null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            var pId = root.TryGetProperty("personaId", out var pid) ? pid.GetString() : null;
            var pName = root.TryGetProperty("personaName", out var pn) ? pn.GetString() : null;
            var pRole = root.TryGetProperty("personaRole", out var pr) ? pr.GetString() : null;
            var pPrompt = root.TryGetProperty("personaPrompt", out var pp) ? pp.GetString() : null;
            return (pId, pName, pRole, pPrompt);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    private async Task<(string? personaId, string? personaName, string? personaRole, string? personaPrompt)> ResolveSessionPersonaAsync(AgentSession session)
    {
        var (metaId, metaName, metaRole, metaPrompt) = ExtractSessionPersona(session.MetadataJson);
        var pid = session.PersonaId ?? metaId;
        if (!string.IsNullOrWhiteSpace(pid))
        {
            var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid);
            if (persona != null)
            {
                return (persona.Id, persona.Name, persona.Role, persona.PersonaPrompt);
            }
        }
        return (pid, metaName, metaRole, metaPrompt);
    }

    public static (string? workingDirectory, string? model, string? effort, string? mode, string? tier) ExtractSessionSettings(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return (null, null, null, null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            var workDir = root.TryGetProperty("workingDirectory", out var wd) ? wd.GetString() : null;
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            var effort = root.TryGetProperty("effort", out var e) ? e.GetString() : null;
            var mode = root.TryGetProperty("mode", out var md) ? md.GetString() : null;
            var tier = root.TryGetProperty("tier", out var tr) ? tr.GetString() : null;
            return (workDir, model, effort, mode, tier);
        }
        catch
        {
            return (null, null, null, null, null);
        }
    }

    public static string? ExtractSessionContextSize(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            return doc.RootElement.TryGetProperty("contextSize", out var cs) ? cs.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public static string AccumulateUsageMetadata(string? metadataJson, string? structuredPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(structuredPayloadJson)) return metadataJson ?? "{}";
        try
        {
            using var payloadDoc = System.Text.Json.JsonDocument.Parse(structuredPayloadJson);
            if (!payloadDoc.RootElement.TryGetProperty("usage", out var usageElem)) return metadataJson ?? "{}";

            var input = usageElem.TryGetProperty("inputTokens", out var ip) ? ip.GetInt64() : 0;
            var output = usageElem.TryGetProperty("outputTokens", out var op) ? op.GetInt64() : 0;
            var thinking = usageElem.TryGetProperty("thinkingTokens", out var tp) ? tp.GetInt64() : 0;
            var total = usageElem.TryGetProperty("totalTokens", out var totp) ? totp.GetInt64() : (input + output);
            var duration = usageElem.TryGetProperty("durationSeconds", out var dp) ? dp.GetDouble() : 0.0;

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed) dict[k] = v;
                }
            }

            long existingInput = 0, existingOutput = 0, existingThinking = 0, existingTotal = 0;
            double existingDuration = 0.0;

            if (dict.TryGetValue("cumulativeUsage", out var existingUsageObj) && existingUsageObj is System.Text.Json.JsonElement existingElem)
            {
                if (existingElem.TryGetProperty("inputTokens", out var ei)) existingInput = ei.GetInt64();
                if (existingElem.TryGetProperty("outputTokens", out var eo)) existingOutput = eo.GetInt64();
                if (existingElem.TryGetProperty("thinkingTokens", out var et)) existingThinking = et.GetInt64();
                if (existingElem.TryGetProperty("totalTokens", out var etot)) existingTotal = etot.GetInt64();
                if (existingElem.TryGetProperty("totalDurationSeconds", out var ed)) existingDuration = ed.GetDouble();
            }

            dict["cumulativeUsage"] = new
            {
                inputTokens = existingInput + input,
                outputTokens = existingOutput + output,
                thinkingTokens = existingThinking + thinking,
                totalTokens = existingTotal + total,
                totalDurationSeconds = existingDuration + duration
            };

            return System.Text.Json.JsonSerializer.Serialize(dict);
        }
        catch
        {
            return metadataJson ?? "{}";
        }
    }

    public static List<QueuedPromptDto> ExtractPromptQueue(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return new();
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if ((doc.RootElement.TryGetProperty("promptQueue", out var qElem) || doc.RootElement.TryGetProperty("PromptQueue", out qElem)) &&
                qElem.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<QueuedPromptDto>>(qElem.GetRawText(), JsonOptions) ?? new();
            }
        }
        catch { }
        return new();
    }

    public static string UpdatePromptQueueMetadata(string? metadataJson, List<QueuedPromptDto> queue)
    {
        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson, JsonOptions);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed) dict[k] = v;
                }
            }
            dict["promptQueue"] = queue;
            return JsonSerializer.Serialize(dict, JsonOptions);
        }
        catch
        {
            return metadataJson ?? "{}";
        }
    }

    public static List<SessionArtifactDto> ExtractArtifacts(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return new();
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if ((doc.RootElement.TryGetProperty("artifacts", out var aElem) || doc.RootElement.TryGetProperty("Artifacts", out aElem)) &&
                aElem.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<SessionArtifactDto>>(aElem.GetRawText(), JsonOptions) ?? new();
            }
        }
        catch { }
        return new();
    }

    public static string UpdateArtifactsMetadata(string? metadataJson, List<SessionArtifactDto> artifacts)
    {
        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson, JsonOptions);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed) dict[k] = v;
                }
            }
            dict["artifacts"] = artifacts;
            return JsonSerializer.Serialize(dict, JsonOptions);
        }
        catch
        {
            return metadataJson ?? "{}";
        }
    }

    public static string GetArtifactDirectory(Guid sessionId)
    {
        var basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentmesh", "artifacts", sessionId.ToString());
        Directory.CreateDirectory(basePath);
        return basePath;
    }

    public static string GetAttachmentDirectory(Guid sessionId)
    {
        var basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentmesh", "artifacts", sessionId.ToString(), "attachments");
        Directory.CreateDirectory(basePath);
        return basePath;
    }

    public static List<SessionAttachmentDto> ExtractAttachments(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return new();
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if ((doc.RootElement.TryGetProperty("attachments", out var aElem) || doc.RootElement.TryGetProperty("Attachments", out aElem)) &&
                aElem.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<SessionAttachmentDto>>(aElem.GetRawText(), JsonOptions) ?? new();
            }
        }
        catch { }
        return new();
    }

    public static string UpdateAttachmentsMetadata(string? metadataJson, List<SessionAttachmentDto> attachments)
    {
        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson, JsonOptions);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed) dict[k] = v;
                }
            }
            dict["attachments"] = attachments;
            return JsonSerializer.Serialize(dict, JsonOptions);
        }
        catch
        {
            return metadataJson ?? "{}";
        }
    }

    public static string? MergeAttachmentsIntoStructuredPayload(string? existingStructuredJson, List<SessionAttachmentDto>? attachments)
    {
        if (attachments == null || attachments.Count == 0)
        {
            return existingStructuredJson;
        }

        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(existingStructuredJson))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(existingStructuredJson, JsonOptions);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed) dict[k] = v;
                }
            }
            dict["attachments"] = attachments;
            return JsonSerializer.Serialize(dict, JsonOptions);
        }
        catch
        {
            return JsonSerializer.Serialize(new { attachments }, JsonOptions);
        }
    }

    public static List<SessionAttachmentDto> ExtractAttachmentsFromPayload(string? structuredPayloadJson, List<SessionAttachmentDto>? directAttachments = null)
    {
        if (directAttachments is { Count: > 0 })
        {
            return directAttachments;
        }
        if (string.IsNullOrWhiteSpace(structuredPayloadJson))
        {
            return new();
        }
        try
        {
            using var doc = JsonDocument.Parse(structuredPayloadJson);
            if ((doc.RootElement.TryGetProperty("attachments", out var aElem) || doc.RootElement.TryGetProperty("Attachments", out aElem)) &&
                aElem.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<SessionAttachmentDto>>(aElem.GetRawText(), JsonOptions) ?? new();
            }
        }
        catch { }
        return new();
    }

    public static string SanitizeFileName(string? fileName)
    {
        var raw = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "attachment.txt" : fileName.Trim());
        if (string.IsNullOrWhiteSpace(raw)) return "attachment.txt";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }
        return sb.ToString();
    }

    public static DateTimeOffset? ExtractStoppedAt(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("stoppedAt", out var sElem) && sElem.TryGetDateTimeOffset(out var dto))
            {
                return dto;
            }
        }
        catch { }
        return null;
    }

    public static DateTimeOffset? ExtractLastStoppedAt(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("lastStoppedAt", out var sElem) && sElem.TryGetDateTimeOffset(out var dto))
            {
                return dto;
            }
            if (doc.RootElement.TryGetProperty("stoppedAt", out var sElem2) && sElem2.TryGetDateTimeOffset(out var dto2))
            {
                return dto2;
            }
        }
        catch { }
        return null;
    }

    public static DateTimeOffset? ExtractTurnStartedAt(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("currentTurnStartedAt", out var sElem) && sElem.TryGetDateTimeOffset(out var dto))
            {
                return dto;
            }
        }
        catch { }
        return null;
    }

    public static string RecordStopMetadata(string? metadataJson, DateTimeOffset stoppedAt)
    {
        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed) dict[k] = v;
                }
            }
            dict["stoppedAt"] = stoppedAt;
            dict["lastStoppedAt"] = stoppedAt;
            return System.Text.Json.JsonSerializer.Serialize(dict);
        }
        catch
        {
            return metadataJson ?? "{}";
        }
    }

    public static string ClearStopMetadata(string? metadataJson)
    {
        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson);
                if (parsed != null)
                {
                    foreach (var (k, v) in parsed)
                    {
                        if (!string.Equals(k, "stoppedAt", StringComparison.OrdinalIgnoreCase))
                        {
                            dict[k] = v;
                        }
                    }
                }
            }
            dict["currentTurnStartedAt"] = DateTimeOffset.UtcNow;
            return System.Text.Json.JsonSerializer.Serialize(dict);
        }
        catch
        {
            return metadataJson ?? "{}";
        }
    }

    private async Task TriggerStopInternalAsync(AgentSession session)
    {
        session.MetadataJson = RecordStopMetadata(session.MetadataJson, DateTimeOffset.UtcNow);
        var auditMsg = new SessionMessage
        {
            SessionId = session.Id,
            SenderAgentId = "system",
            TargetAgentId = session.CreatorAgentId,
            EventType = "system",
            Content = "🛑 Interrupted by operator for new prompt.",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(auditMsg);
        var auditDto = ToMessageDto(auditMsg);
        await hubContext.Clients.Group($"session-{session.Id}").SendAsync("ReceiveMessage", auditDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", auditDto);

        await hubContext.Clients.Group($"session-{session.Id}").SendAsync("StopExecution", new
        {
            sessionId = session.Id,
            agentId = session.CreatorAgentId,
            reason = "Interrupted by incoming prompt"
        });
        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
            await hubContext.Clients.Group(targetGroup).SendAsync("StopExecution", new
            {
                sessionId = session.Id,
                agentId = session.CreatorAgentId,
                reason = "Interrupted by incoming prompt"
            });
        }
        await hubContext.Clients.All.SendAsync("StopExecution", new
        {
            sessionId = session.Id,
            agentId = session.CreatorAgentId,
            reason = "Interrupted by incoming prompt"
        });
    }

    [HttpPost]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
        {
            return BadRequest(new { error = "Session Title is required." });
        }

        Persona? persona = null;
        if (!string.IsNullOrWhiteSpace(req.PersonaId))
        {
            var normPersonaId = req.PersonaId.Trim().ToLowerInvariant();
            persona = await db.Personas.FirstOrDefaultAsync(p => p.Id == normPersonaId);
        }

        var callerAgentId = !string.IsNullOrWhiteSpace(req.DriverId)
            ? req.DriverId.Trim().ToLowerInvariant()
            : !string.IsNullOrWhiteSpace(req.CreatorAgentId)
                ? req.CreatorAgentId.Trim().ToLowerInvariant()
                : persona?.DriverId
                  ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? User?.FindFirst("AgentId")?.Value
                  ?? "agy";

        var effectiveWorkDir = !string.IsNullOrWhiteSpace(req.WorkingDirectory) ? req.WorkingDirectory : persona?.DefaultWorkingDirectory;
        var effectiveModel = !string.IsNullOrWhiteSpace(req.Model) ? req.Model : persona?.DefaultModel;
        var effectiveEffort = req.Effort != null ? req.Effort : persona?.DefaultEffort;
        var effectiveMode = !string.IsNullOrWhiteSpace(req.Mode) ? req.Mode : persona?.DefaultMode;
        var effectiveTier = !string.IsNullOrWhiteSpace(req.Tier) ? req.Tier : persona?.DefaultTier;
        var effectiveContextSize = req.ContextSize != null ? req.ContextSize : persona?.DefaultContextSize;

        var initialMetadata = MergeSessionMetadata(
            req.MetadataJson,
            effectiveWorkDir,
            effectiveModel,
            effectiveEffort,
            effectiveMode,
            effectiveTier,
            persona?.Id ?? req.PersonaId,
            persona?.Name,
            persona?.Role,
            persona?.PersonaPrompt,
            effectiveContextSize);

        var hasAttachments = req.Attachments is { Count: > 0 };
        var hasInitialPrompt = !string.IsNullOrWhiteSpace(req.InitialMessage) || hasAttachments;

        if (hasAttachments)
        {
            initialMetadata = UpdateAttachmentsMetadata(initialMetadata, req.Attachments!);
        }

        var session = new AgentSession
        {
            Title = req.Title.Trim(),
            Status = hasInitialPrompt ? "thinking" : "active",
            MetadataJson = initialMetadata,
            PersonaId = persona?.Id ?? req.PersonaId?.Trim().ToLowerInvariant(),
            DriverId = callerAgentId,
            CreatorAgentId = callerAgentId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.AgentSessions.Add(session);

        SessionMessage? initialMsg = null;
        if (hasInitialPrompt)
        {
            var promptText = !string.IsNullOrWhiteSpace(req.InitialMessage)
                ? req.InitialMessage.Trim()
                : DefaultAttachmentPrompt;

            initialMsg = new SessionMessage
            {
                SessionId = session.Id,
                SenderAgentId = "operator",
                TargetAgentId = callerAgentId,
                EventType = "prompt",
                Content = promptText,
                StructuredPayloadJson = MergeAttachmentsIntoStructuredPayload(null, req.Attachments),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(initialMsg);
        }

        await db.SaveChangesAsync();

        await hubContext.Clients.All.SendAsync("SessionCreated", session);

        if (initialMsg != null && !string.IsNullOrWhiteSpace(callerAgentId))
        {
            var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
            var contextSize = ExtractSessionContextSize(session.MetadataJson);
            var (personaId, personaName, personaRole, personaPrompt) = await ResolveSessionPersonaAsync(session);
            var targetGroup = $"agent-{callerAgentId}";
            var promptPayload = new
            {
                sessionId = session.Id,
                messageId = initialMsg.Id,
                agentId = callerAgentId,
                driverId = callerAgentId,
                personaId = personaId,
                personaName = personaName,
                personaRole = personaRole,
                personaPrompt = personaPrompt,
                prompt = initialMsg.Content,
                sender = initialMsg.SenderAgentId,
                workingDirectory = workDir,
                model = model,
                effort = effort,
                mode = mode,
                tier = tier,
                contextSize = contextSize,
                attachments = req.Attachments,
                createdAt = initialMsg.CreatedAt
            };
            await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", promptPayload);
            await hubContext.Clients.All.SendAsync("PromptReceived", promptPayload);
        }

        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, session);
    }

    [HttpGet("{id:guid}/messages")]
    [HttpGet("{id:guid}/events")]
    [AllowAnonymous]
    public async Task<IActionResult> GetMessages(Guid id)
    {
        var sessionExists = await db.AgentSessions.AnyAsync(s => s.Id == id);
        if (!sessionExists)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var messages = await db.SessionMessages
            .AsNoTracking()
            .Where(m => m.SessionId == id)
            .OrderBy(m => m.CreatedAt)
            .Take(500)
            .ToListAsync();

        return Ok(messages.Select(ToMessageDto).ToList());
    }

    private async Task<IActionResult> HandleMeshCommandInternalAsync(AgentSession session, string commandText)
    {
        var trimmed = commandText.Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var subCmd = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;
        var arg = parts.Length > 2 ? string.Join(' ', parts.Skip(2)).Trim() : null;

        string systemResponse;

        if (string.IsNullOrWhiteSpace(subCmd) || subCmd == "help")
        {
            systemResponse = "💡 **AgentMesh Command Reference**\n\n" +
                "• **`/mesh stop`**: Halt active model execution immediately (<500ms)\n" +
                "• **`/mesh mode <plan|auto|accept-edits>`**: Switch session execution mode\n" +
                "• **`/mesh model <model-id>`**: Set active model (e.g. gemini-3.8-flash)\n" +
                "• **`/mesh effort <low|medium|high|max>`**: Set reasoning effort\n" +
                "• **`/mesh context <size>`**: Set context window size (e.g. 128k, 200k, 500k, 1m, 2m, auto)\n" +
                "• **`/mesh artifacts`**: Toggle side-by-side Artifacts panel\n" +
                "• **`/mesh queue <prompt>`**: Enqueue prompt sequentially\n" +
                "• **`/mesh clear`**: Clear event timeline filters\n\n" +
                "*Agent Workflows*: `/plan`, `/grill-me`, `/goal`, `/compact`, `/browser`, `/schedule`, `/skills`, `/help`";
        }
        else if (subCmd == "stop")
        {
            await TriggerStopInternalAsync(session);
            systemResponse = "🛑 Stop signal dispatched for active model execution.";
        }
        else if (subCmd == "effort")
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                var eff = arg.ToLowerInvariant();
                if (eff is "low" or "medium" or "high" or "max")
                {
                    session.MetadataJson = MergeSessionMetadata(session.MetadataJson, null, null, eff, null, null);
                    systemResponse = $"✓ Reasoning effort set to \"{eff}\" for session '{session.Title}'.";
                }
                else
                {
                    systemResponse = $"⚠️ Invalid effort level \"{arg}\". Supported levels: low, medium, high, max.";
                }
            }
            else
            {
                var (_, _, currentEffort, _, _) = ExtractSessionSettings(session.MetadataJson);
                systemResponse = $"⚙️ **Reasoning Effort Configuration**\n\n" +
                    $"• Current Level: **`{currentEffort ?? "medium"}`**\n" +
                    $"• Supported Levels: `low` | `medium` | `high` | `max`\n" +
                    $"• Usage: `/mesh effort <level>` (e.g. `/mesh effort high`)";
            }
        }
        else if (subCmd == "model")
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                session.MetadataJson = MergeSessionMetadata(session.MetadataJson, null, arg, null, null, null);
                systemResponse = $"✓ Model updated to \"{arg}\" for session '{session.Title}'.";
            }
            else
            {
                var (_, currentModel, _, _, _) = ExtractSessionSettings(session.MetadataJson);
                systemResponse = $"⚙️ **Model Configuration**\n\n" +
                    $"• Current Model: **`{currentModel ?? "default"}`**\n" +
                    $"• Usage: `/mesh model <model-id>` (e.g. `/mesh model gemini-3.8-flash`)";
            }
        }
        else if (subCmd == "mode")
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                var mode = arg.ToLowerInvariant();
                if (mode is "plan" or "auto" or "accept-edits")
                {
                    session.MetadataJson = MergeSessionMetadata(session.MetadataJson, null, null, null, mode, null);
                    systemResponse = $"✓ Execution mode set to \"{mode}\" for session '{session.Title}'.";
                }
                else
                {
                    systemResponse = $"⚠️ Invalid mode \"{arg}\". Supported modes: plan, auto, accept-edits.";
                }
            }
            else
            {
                var (_, _, _, currentMode, _) = ExtractSessionSettings(session.MetadataJson);
                systemResponse = $"⚙️ **Execution Mode Configuration**\n\n" +
                    $"• Current Mode: **`{currentMode ?? "auto"}`**\n" +
                    $"• Supported Modes: `plan` | `auto` | `accept-edits`\n" +
                    $"• Usage: `/mesh mode <mode>` (e.g. `/mesh mode plan`)";
            }
        }
        else if (subCmd == "context")
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                var ctxVal = arg.ToLowerInvariant();
                session.MetadataJson = MergeSessionMetadata(session.MetadataJson, null, null, null, null, null, contextSize: ctxVal);
                systemResponse = $"✓ Context size set to \"{ctxVal}\" for session '{session.Title}'.";
            }
            else
            {
                var currentCtx = ExtractSessionContextSize(session.MetadataJson);
                systemResponse = $"⚙️ **Context Size Configuration**\n\n" +
                    $"• Current Context Size: **`{currentCtx ?? "default"}`**\n" +
                    $"• Usage: `/mesh context <size>` (e.g. `/mesh context 1m` or `/mesh context long_context`)";
            }
        }
        else if (subCmd == "queue")
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                var queue = ExtractPromptQueue(session.MetadataJson);
                queue.Add(new QueuedPromptDto(
                    Id: Guid.NewGuid().ToString(),
                    Prompt: arg,
                    Sender: "operator",
                    CreatedAt: DateTimeOffset.UtcNow
                ));
                session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, queue);
                systemResponse = $"✓ Prompt enqueued sequentially ({queue.Count} prompt(s) in queue).";
            }
            else
            {
                var queue = ExtractPromptQueue(session.MetadataJson);
                systemResponse = $"📋 **Prompt Queue Status**\n\n" +
                    $"• Pending in Queue: **{queue.Count}** prompt(s)\n" +
                    $"• Usage: `/mesh queue <prompt>` to enqueue a prompt sequentially.";
            }
        }
        else
        {
            systemResponse = $"⚠️ Unknown mesh subcommand: \"{subCmd}\". Type `/mesh help` for available commands.";
        }

        var sysMsg = new SessionMessage
        {
            SessionId = session.Id,
            SenderAgentId = "system",
            TargetAgentId = session.CreatorAgentId,
            EventType = "system",
            Content = systemResponse,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(sysMsg);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var msgDto = ToMessageDto(sysMsg);
        await hubContext.Clients.Group($"session-{session.Id}").SendAsync("ReceiveMessage", msgDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", msgDto);
        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(new { command = trimmed, systemMessage = msgDto, session });
    }

    [HttpPost("{id:guid}/messages")]
    [HttpPost("{id:guid}/events")]
    public async Task<IActionResult> PostMessage(Guid id, [FromBody] PostMessageRequest req)
    {
        var messageAttachments = ExtractAttachmentsFromPayload(req.StructuredPayloadJson, req.Attachments);
        var hasAttachments = messageAttachments.Count > 0;

        if (string.IsNullOrWhiteSpace(req.Content) && !hasAttachments)
        {
            return BadRequest(new { error = "Message content is required." });
        }

        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var senderId = !string.IsNullOrWhiteSpace(req.SenderAgentId)
            ? req.SenderAgentId.Trim()
            : User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? User?.FindFirst("AgentId")?.Value
              ?? "agy";

        var eventType = !string.IsNullOrWhiteSpace(req.EventType)
            ? req.EventType.Trim().ToLowerInvariant()
            : "response";

        var effectiveContent = !string.IsNullOrWhiteSpace(req.Content)
            ? req.Content.Trim()
            : DefaultAttachmentPrompt;

        if (eventType == "prompt")
        {
            if (effectiveContent.StartsWith("/mesh", StringComparison.OrdinalIgnoreCase) ||
                effectiveContent.StartsWith("\\mesh", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleMeshCommandInternalAsync(session, effectiveContent);
            }

            session.MetadataJson = ClearStopMetadata(session.MetadataJson);
        }
        else
        {
            if (session.Status == "stopped")
            {
                return Ok(new { status = "dropped_due_to_stop" });
            }

            var stoppedAt = ExtractStoppedAt(session.MetadataJson);
            if (stoppedAt.HasValue && session.Status != "thinking" && session.Status != "waiting_for_input")
            {
                // Discard stale events emitted by stopped agent turns
                return Ok(new { status = "dropped_due_to_stop" });
            }

            var lastStoppedAt = ExtractLastStoppedAt(session.MetadataJson);
            if (lastStoppedAt.HasValue)
            {
                var turnStartedAt = ExtractTurnStartedAt(session.MetadataJson);
                if (!turnStartedAt.HasValue || turnStartedAt.Value <= lastStoppedAt.Value)
                {
                    return Ok(new { status = "dropped_due_to_stop" });
                }
                if (req.CreatedAt.HasValue && req.CreatedAt.Value <= lastStoppedAt.Value)
                {
                    return Ok(new { status = "dropped_due_to_stop" });
                }
            }
        }

        var mergedStructuredPayload = MergeAttachmentsIntoStructuredPayload(req.StructuredPayloadJson, messageAttachments);

        var message = new SessionMessage
        {
            SessionId = id,
            SenderAgentId = senderId,
            TargetAgentId = req.TargetAgentId ?? session.CreatorAgentId,
            EventType = eventType,
            Content = effectiveContent,
            ToolName = req.ToolName,
            ToolArgsJson = req.ToolArgsJson,
            ToolResultJson = req.ToolResultJson,
            StructuredPayloadJson = mergedStructuredPayload,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var wasThinking = session.Status == "thinking";

        if (!string.IsNullOrWhiteSpace(mergedStructuredPayload))
        {
            session.MetadataJson = AccumulateUsageMetadata(session.MetadataJson, mergedStructuredPayload);
        }

        db.SessionMessages.Add(message);
        session.UpdatedAt = DateTimeOffset.UtcNow;

        if (eventType == "tool_call" && string.Equals(req.ToolName, "ask_question", StringComparison.OrdinalIgnoreCase))
        {
            session.Status = "waiting_for_input";
        }
        else if (eventType is "prompt" or "thought" or "tool_call")
        {
            session.Status = "thinking";
        }
        else if (eventType == "response")
        {
            if (session.Status != "waiting_for_input")
            {
                session.Status = "active";
            }
        }

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == session.CreatorAgentId);
            if (agent != null)
            {
                var isFromAgent = string.Equals(senderId, agent.Id, StringComparison.OrdinalIgnoreCase);
                var isConnected = isFromAgent || AgentMeshHub.IsAgentConnected(agent.Id) || agent.Status is "online" or "busy";

                if (isConnected)
                {
                    agent.Status = session.Status == "thinking" ? "busy" : "online";
                    agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
                }
            }
        }

        await db.SaveChangesAsync();

        var messageDto = ToMessageDto(message);

        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", messageDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", messageDto);

        if (eventType == "prompt" && !string.IsNullOrWhiteSpace(message.TargetAgentId))
        {
            var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
            var contextSize = ExtractSessionContextSize(session.MetadataJson);
            var (personaId, personaName, personaRole, personaPrompt) = await ResolveSessionPersonaAsync(session);
            var targetGroup = $"agent-{message.TargetAgentId.Trim().ToLowerInvariant()}";
            var promptPayload = new
            {
                sessionId = id,
                messageId = message.Id,
                agentId = message.TargetAgentId.Trim().ToLowerInvariant(),
                driverId = message.TargetAgentId.Trim().ToLowerInvariant(),
                personaId = personaId,
                personaName = personaName,
                personaRole = personaRole,
                personaPrompt = personaPrompt,
                prompt = message.Content,
                sender = message.SenderAgentId,
                workingDirectory = workDir,
                model = model,
                effort = effort,
                mode = mode,
                tier = tier,
                contextSize = contextSize,
                attachments = messageAttachments.Count > 0 ? messageAttachments : null,
                createdAt = message.CreatedAt
            };
            await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", promptPayload);
            await hubContext.Clients.All.SendAsync("PromptReceived", promptPayload);
        }
        else if (eventType == "response")
        {
            await CheckAndForwardJobTurnResponseAsync(session, message);

            if (wasThinking)
            {
                // Auto-advance queued prompts sequentially if any are pending
                var queue = ExtractPromptQueue(session.MetadataJson);
                if (queue.Count > 0)
                {
                    var nextPrompt = queue[0];
                    queue.RemoveAt(0);
                    session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, queue);

                    var nextMsg = new SessionMessage
                    {
                        SessionId = id,
                        SenderAgentId = nextPrompt.Sender,
                        TargetAgentId = session.CreatorAgentId,
                        EventType = "prompt",
                        Content = string.IsNullOrWhiteSpace(nextPrompt.Prompt) ? DefaultAttachmentPrompt : nextPrompt.Prompt,
                        StructuredPayloadJson = MergeAttachmentsIntoStructuredPayload(null, nextPrompt.Attachments),
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    db.SessionMessages.Add(nextMsg);
                    session.Status = "thinking";
                    session.UpdatedAt = DateTimeOffset.UtcNow;

                    if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
                    {
                        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == session.CreatorAgentId);
                        if (agent != null)
                        {
                            agent.Status = "busy";
                            agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
                        }
                    }

                    await db.SaveChangesAsync();

                    var nextDto = ToMessageDto(nextMsg);
                    await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", nextDto);
                    await hubContext.Clients.All.SendAsync("ReceiveMessage", nextDto);
                    await hubContext.Clients.Group($"session-{id}").SendAsync("QueueUpdated", new { sessionId = id, queue });
                    await hubContext.Clients.All.SendAsync("QueueUpdated", new { sessionId = id, queue });
                    await hubContext.Clients.Group($"session-{id}").SendAsync("SessionUpdated", session);
                    await hubContext.Clients.All.SendAsync("SessionUpdated", session);

                    var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
                    var contextSize = ExtractSessionContextSize(session.MetadataJson);
                    var (personaId, personaName, personaRole, personaPrompt) = await ResolveSessionPersonaAsync(session);
                    if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
                    {
                        var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
                        var promptPayload = new
                        {
                            sessionId = id,
                            messageId = nextMsg.Id,
                            agentId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                            driverId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                            personaId = personaId,
                            personaName = personaName,
                            personaRole = personaRole,
                            personaPrompt = personaPrompt,
                            prompt = nextMsg.Content,
                            sender = nextMsg.SenderAgentId,
                            workingDirectory = nextPrompt.WorkingDirectory ?? workDir,
                            model = nextPrompt.Model ?? model,
                            effort = nextPrompt.Effort ?? effort,
                            mode = nextPrompt.Mode ?? mode,
                            tier = nextPrompt.Tier ?? tier,
                            contextSize = nextPrompt.ContextSize ?? contextSize,
                            attachments = nextPrompt.Attachments,
                            createdAt = nextMsg.CreatedAt
                        };
                        await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", promptPayload);
                        await hubContext.Clients.All.SendAsync("PromptReceived", promptPayload);
                    }
                }
                else
                {
                    // Queue is empty: session completed turn and returned to active/idle. Notify all clients to clear thinking/stop button state!
                    await hubContext.Clients.Group($"session-{id}").SendAsync("SessionUpdated", session);
                    await hubContext.Clients.All.SendAsync("SessionUpdated", session);
                }
            }
        }

        return CreatedAtAction(nameof(GetMessages), new { id }, messageDto);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateSession(Guid id, [FromBody] UpdateSessionRequest req)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        if (!string.IsNullOrWhiteSpace(req.Title))
        {
            session.Title = req.Title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            session.Status = req.Status.Trim().ToLowerInvariant();
        }

        var (oldWorkDir, oldModel, oldEffort, oldMode, oldTier) = ExtractSessionSettings(session.MetadataJson);
        var oldContextSize = ExtractSessionContextSize(session.MetadataJson);

        if (req.MetadataJson != null || req.Model != null || req.Effort != null || req.Tier != null || req.Mode != null || req.WorkingDirectory != null || req.ContextSize != null)
        {
            session.MetadataJson = MergeSessionMetadata(
                req.MetadataJson ?? session.MetadataJson,
                req.WorkingDirectory,
                req.Model,
                req.Effort,
                req.Mode,
                req.Tier,
                contextSize: req.ContextSize
            );
        }

        // Detect control parameter changes and append audit event
        var changes = new List<string>();
        if (!string.IsNullOrWhiteSpace(req.Model) && req.Model != oldModel)
            changes.Add($"Model changed to '{req.Model}'");
        if (req.Effort != null && req.Effort != oldEffort)
            changes.Add(string.IsNullOrWhiteSpace(req.Effort) ? "Reasoning effort reset to default" : $"Effort set to '{req.Effort}'");
        if (!string.IsNullOrWhiteSpace(req.Tier) && req.Tier != oldTier)
            changes.Add($"Tier set to '{req.Tier}'");
        if (req.ContextSize != null && req.ContextSize != oldContextSize)
            changes.Add(string.IsNullOrWhiteSpace(req.ContextSize) ? "Context size reset to default" : $"Context size set to '{req.ContextSize}'");
        if (!string.IsNullOrWhiteSpace(req.Mode) && req.Mode != oldMode)
            changes.Add($"Mode set to '{req.Mode}'");
        if (!string.IsNullOrWhiteSpace(req.WorkingDirectory) && req.WorkingDirectory != oldWorkDir)
            changes.Add($"Working directory set to '{req.WorkingDirectory}'");

        if (changes.Count > 0)
        {
            var auditMsg = new SessionMessage
            {
                SessionId = session.Id,
                SenderAgentId = "system",
                TargetAgentId = session.CreatorAgentId,
                EventType = "system",
                Content = $"⚙️ Session updated: {string.Join(", ", changes)}",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(auditMsg);
            var auditDto = ToMessageDto(auditMsg);
            await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", auditDto);
            await hubContext.Clients.All.SendAsync("ReceiveMessage", auditDto);
        }

        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(session);
    }

    [HttpPost("{id:guid}/stop")]
    public async Task<IActionResult> StopSession(Guid id)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        session.Status = "active";
        session.UpdatedAt = DateTimeOffset.UtcNow;

        var emptyQueue = new List<QueuedPromptDto>();
        session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, emptyQueue);
        session.MetadataJson = RecordStopMetadata(session.MetadataJson, DateTimeOffset.UtcNow);

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == session.CreatorAgentId);
            if (agent != null && agent.Status == "busy")
            {
                agent.Status = "online";
                agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
            }
        }

        var auditMsg = new SessionMessage
        {
            SessionId = session.Id,
            SenderAgentId = "system",
            TargetAgentId = session.CreatorAgentId,
            EventType = "system",
            Content = "🛑 Model execution stopped by operator.",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(auditMsg);
        await db.SaveChangesAsync();

        var auditDto = ToMessageDto(auditMsg);
        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", auditDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", auditDto);
        await hubContext.Clients.Group($"session-{id}").SendAsync("QueueUpdated", new { sessionId = id, queue = emptyQueue });
        await hubContext.Clients.All.SendAsync("QueueUpdated", new { sessionId = id, queue = emptyQueue });

        await hubContext.Clients.Group($"session-{id}").SendAsync("StopExecution", new
        {
            sessionId = id,
            agentId = session.CreatorAgentId,
            reason = "Operator clicked stop"
        });

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
            await hubContext.Clients.Group(targetGroup).SendAsync("StopExecution", new
            {
                sessionId = id,
                agentId = session.CreatorAgentId,
                reason = "Operator clicked stop"
            });
        }

        await hubContext.Clients.All.SendAsync("StopExecution", new
        {
            sessionId = id,
            agentId = session.CreatorAgentId,
            reason = "Operator clicked stop"
        });

        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(new { status = "stopped", sessionId = id });
    }

    [HttpPost("{id:guid}/commands/{commandId}/stop")]
    public async Task<IActionResult> StopCommand(Guid id, string commandId)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var auditMsg = new SessionMessage
        {
            SessionId = session.Id,
            SenderAgentId = "system",
            TargetAgentId = session.CreatorAgentId,
            EventType = "tool_result",
            ToolName = "command",
            Content = $"🛑 Command '{commandId}' stopped by operator.",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(auditMsg);
        await db.SaveChangesAsync();

        var auditDto = ToMessageDto(auditMsg);
        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", auditDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", auditDto);

        await hubContext.Clients.Group($"session-{id}").SendAsync("StopCommand", new
        {
            sessionId = id,
            commandId,
            agentId = session.CreatorAgentId
        });

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
            await hubContext.Clients.Group(targetGroup).SendAsync("StopCommand", new
            {
                sessionId = id,
                commandId,
                agentId = session.CreatorAgentId
            });
        }

        await hubContext.Clients.All.SendAsync("StopCommand", new
        {
            sessionId = id,
            commandId,
            agentId = session.CreatorAgentId
        });

        return Ok(new { status = "stopped", commandId });
    }

    [HttpPost("{id:guid}/queue")]
    public async Task<IActionResult> EnqueuePrompt(Guid id, [FromBody] EnqueuePromptRequest req)
    {
        var hasAttachments = req.Attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(req.Prompt) && !hasAttachments)
        {
            return BadRequest(new { error = "Prompt cannot be empty." });
        }

        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var effectivePrompt = !string.IsNullOrWhiteSpace(req.Prompt)
            ? req.Prompt.Trim()
            : DefaultAttachmentPrompt;

        if (effectivePrompt.StartsWith("/mesh", StringComparison.OrdinalIgnoreCase) ||
            effectivePrompt.StartsWith("\\mesh", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleMeshCommandInternalAsync(session, effectivePrompt);
        }

        var senderId = !string.IsNullOrWhiteSpace(req.SenderAgentId)
            ? req.SenderAgentId.Trim()
            : User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? User?.FindFirst("AgentId")?.Value
              ?? "operator";

        var queue = ExtractPromptQueue(session.MetadataJson);
        var structuredPayloadJson = MergeAttachmentsIntoStructuredPayload(null, req.Attachments);

        if (req.SendNow)
        {
            if (session.Status == "thinking")
            {
                await TriggerStopInternalAsync(session);
            }

            var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
            var contextSize = ExtractSessionContextSize(session.MetadataJson);
            var msg = new SessionMessage
            {
                SessionId = id,
                SenderAgentId = senderId,
                TargetAgentId = session.CreatorAgentId,
                EventType = "prompt",
                Content = effectivePrompt,
                StructuredPayloadJson = structuredPayloadJson,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(msg);
            session.Status = "thinking";
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            var msgDto = ToMessageDto(msg);
            await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", msgDto);
            await hubContext.Clients.All.SendAsync("ReceiveMessage", msgDto);
            await hubContext.Clients.All.SendAsync("SessionUpdated", session);

            if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
            {
                var (personaId, personaName, personaRole, personaPrompt) = await ResolveSessionPersonaAsync(session);
                var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
                var promptPayload = new
                {
                    sessionId = id,
                    messageId = msg.Id,
                    agentId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                    driverId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                    personaId = personaId,
                    personaName = personaName,
                    personaRole = personaRole,
                    personaPrompt = personaPrompt,
                    prompt = msg.Content,
                    sender = msg.SenderAgentId,
                    workingDirectory = req.WorkingDirectory ?? workDir,
                    model = req.Model ?? model,
                    effort = req.Effort ?? effort,
                    mode = req.Mode ?? mode,
                    tier = req.Tier ?? tier,
                    contextSize = req.ContextSize ?? contextSize,
                    attachments = req.Attachments,
                    createdAt = msg.CreatedAt
                };
                await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", promptPayload);
                await hubContext.Clients.All.SendAsync("PromptReceived", promptPayload);
            }

            return Ok(new { session, queue, dispatched = true });
        }

        // If session is idle and queue is empty, dispatch immediately
        if (session.Status != "thinking" && queue.Count == 0)
        {
            var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
            var contextSize = ExtractSessionContextSize(session.MetadataJson);
            var msg = new SessionMessage
            {
                SessionId = id,
                SenderAgentId = senderId,
                TargetAgentId = session.CreatorAgentId,
                EventType = "prompt",
                Content = effectivePrompt,
                StructuredPayloadJson = structuredPayloadJson,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(msg);
            session.Status = "thinking";
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            var msgDto = ToMessageDto(msg);
            await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", msgDto);
            await hubContext.Clients.All.SendAsync("ReceiveMessage", msgDto);
            await hubContext.Clients.All.SendAsync("SessionUpdated", session);

            if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
            {
                var (personaId, personaName, personaRole, personaPrompt) = await ResolveSessionPersonaAsync(session);
                var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
                var promptPayload = new
                {
                    sessionId = id,
                    messageId = msg.Id,
                    agentId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                    driverId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                    personaId = personaId,
                    personaName = personaName,
                    personaRole = personaRole,
                    personaPrompt = personaPrompt,
                    prompt = msg.Content,
                    sender = msg.SenderAgentId,
                    workingDirectory = req.WorkingDirectory ?? workDir,
                    model = req.Model ?? model,
                    effort = req.Effort ?? effort,
                    mode = req.Mode ?? mode,
                    tier = req.Tier ?? tier,
                    contextSize = req.ContextSize ?? contextSize,
                    attachments = req.Attachments,
                    createdAt = msg.CreatedAt
                };
                await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", promptPayload);
                await hubContext.Clients.All.SendAsync("PromptReceived", promptPayload);
            }

            return Ok(new { session, queue, dispatched = true });
        }

        // Otherwise enqueue for sequential execution
        var queuedItem = new QueuedPromptDto(
            Id: Guid.NewGuid().ToString(),
            Prompt: effectivePrompt,
            Sender: senderId,
            CreatedAt: DateTimeOffset.UtcNow,
            Model: req.Model,
            Effort: req.Effort,
            Tier: req.Tier,
            Mode: req.Mode,
            WorkingDirectory: req.WorkingDirectory,
            Attachments: req.Attachments,
            ContextSize: req.ContextSize
        );
        queue.Add(queuedItem);

        session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, queue);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await hubContext.Clients.Group($"session-{id}").SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(new { session, queue, dispatched = false });
    }

    [HttpDelete("{id:guid}/queue/{queueItemId}")]
    public async Task<IActionResult> DequeuePrompt(Guid id, string queueItemId)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var queue = ExtractPromptQueue(session.MetadataJson);
        queue.RemoveAll(q => string.Equals(q.Id, queueItemId, StringComparison.OrdinalIgnoreCase));

        session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, queue);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await hubContext.Clients.Group($"session-{id}").SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(queue);
    }

    [HttpPost("{id:guid}/queue/{queueItemId}/send-now")]
    public async Task<IActionResult> SendQueuedPromptNow(Guid id, string queueItemId)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var queue = ExtractPromptQueue(session.MetadataJson);
        var item = queue.FirstOrDefault(q => string.Equals(q.Id, queueItemId, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            return NotFound(new { error = $"Queued prompt '{queueItemId}' not found." });
        }

        queue.Remove(item);
        session.MetadataJson = UpdatePromptQueueMetadata(session.MetadataJson, queue);

        if (session.Status == "thinking")
        {
            await TriggerStopInternalAsync(session);
        }

        var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
        var contextSize = ExtractSessionContextSize(session.MetadataJson);
        var structuredPayloadJson = MergeAttachmentsIntoStructuredPayload(null, item.Attachments);

        var msg = new SessionMessage
        {
            SessionId = id,
            SenderAgentId = item.Sender,
            TargetAgentId = session.CreatorAgentId,
            EventType = "prompt",
            Content = item.Prompt,
            StructuredPayloadJson = structuredPayloadJson,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(msg);
        session.Status = "thinking";
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var msgDto = ToMessageDto(msg);
        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", msgDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", msgDto);
        await hubContext.Clients.Group($"session-{id}").SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("QueueUpdated", new { sessionId = id, queue });
        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var (personaId, personaName, personaRole, personaPrompt) = await ResolveSessionPersonaAsync(session);
            var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
            var promptPayload = new
            {
                sessionId = id,
                messageId = msg.Id,
                agentId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                driverId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                personaId = personaId,
                personaName = personaName,
                personaRole = personaRole,
                personaPrompt = personaPrompt,
                prompt = msg.Content,
                sender = msg.SenderAgentId,
                workingDirectory = item.WorkingDirectory ?? workDir,
                model = item.Model ?? model,
                effort = item.Effort ?? effort,
                mode = item.Mode ?? mode,
                tier = item.Tier ?? tier,
                contextSize = item.ContextSize ?? contextSize,
                attachments = item.Attachments,
                createdAt = msg.CreatedAt
            };
            await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", promptPayload);
            await hubContext.Clients.All.SendAsync("PromptReceived", promptPayload);
        }

        return Ok(new { session, queue, dispatched = true });
    }

    [HttpPost("{id:guid}/answer")]
    public async Task<IActionResult> SubmitAnswer(Guid id, [FromBody] SubmitAnswerRequest req)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var senderId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("AgentId")?.Value
            ?? "operator";

        var sb = new StringBuilder();
        foreach (var ans in req.Answers)
        {
            var parts = new List<string>();
            if (ans.SelectedOptions != null && ans.SelectedOptions.Count > 0)
            {
                parts.Add(string.Join(", ", ans.SelectedOptions));
            }
            if (!string.IsNullOrWhiteSpace(ans.CustomText))
            {
                parts.Add(ans.CustomText.Trim());
            }
            var answerStr = parts.Count > 0 ? string.Join("; ", parts) : "User skipped";
            sb.AppendLine($"Q: {ans.Question} -> A: {answerStr}");
        }

        var answerSummary = sb.ToString().Trim();
        var answersJson = System.Text.Json.JsonSerializer.Serialize(req.Answers);

        var toolResultMsg = new SessionMessage
        {
            SessionId = id,
            SenderAgentId = senderId,
            TargetAgentId = session.CreatorAgentId,
            EventType = "tool_result",
            ToolName = "ask_question",
            Content = answerSummary,
            ToolResultJson = answersJson,
            StructuredPayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                toolCallId = req.ToolCallId,
                answers = req.Answers
            }),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(toolResultMsg);

        var promptContent = $"User answered question:\n{answerSummary}\nPlease proceed with this answer.";
        var promptMsg = new SessionMessage
        {
            SessionId = id,
            SenderAgentId = senderId,
            TargetAgentId = session.CreatorAgentId,
            EventType = "prompt",
            Content = promptContent,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(promptMsg);

        session.MetadataJson = ClearStopMetadata(session.MetadataJson);
        session.Status = "thinking";
        session.UpdatedAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == session.CreatorAgentId);
            if (agent != null)
            {
                agent.Status = "busy";
                agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync();

        var toolResultDto = ToMessageDto(toolResultMsg);
        var promptDto = ToMessageDto(promptMsg);

        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", toolResultDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", toolResultDto);

        await hubContext.Clients.Group($"session-{id}").SendAsync("ReceiveMessage", promptDto);
        await hubContext.Clients.All.SendAsync("ReceiveMessage", promptDto);

        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        if (!string.IsNullOrWhiteSpace(session.CreatorAgentId))
        {
            var (workDir, model, effort, mode, tier) = ExtractSessionSettings(session.MetadataJson);
            var contextSize = ExtractSessionContextSize(session.MetadataJson);
            var (personaId, personaName, personaRole, personaPrompt) = await ResolveSessionPersonaAsync(session);
            var targetGroup = $"agent-{session.CreatorAgentId.Trim().ToLowerInvariant()}";
            var promptPayload = new
            {
                sessionId = id,
                messageId = promptMsg.Id,
                agentId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                driverId = session.CreatorAgentId.Trim().ToLowerInvariant(),
                personaId = personaId,
                personaName = personaName,
                personaRole = personaRole,
                personaPrompt = personaPrompt,
                prompt = promptMsg.Content,
                sender = promptMsg.SenderAgentId,
                workingDirectory = workDir,
                model = model,
                effort = effort,
                mode = mode,
                tier = tier,
                contextSize = contextSize,
                createdAt = promptMsg.CreatedAt
            };
            await hubContext.Clients.Group(targetGroup).SendAsync("PromptReceived", promptPayload);
            await hubContext.Clients.All.SendAsync("PromptReceived", promptPayload);
        }

        return Ok(new { toolResult = toolResultDto, prompt = promptDto, session });
    }

    [HttpPost("{id:guid}/attachments")]
    [RequestSizeLimit(262_144_000)]
    public async Task<IActionResult> UploadAttachments(Guid id, [FromForm] IFormFileCollection? files = null)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var formFiles = files is { Count: > 0 }
            ? files
            : (Request?.HasFormContentType == true ? Request.Form.Files : null);

        if (formFiles == null || formFiles.Count == 0)
        {
            return BadRequest(new { error = "At least one file must be provided." });
        }

        if (formFiles.Count > MaxAttachmentsPerMessage)
        {
            return BadRequest(new { error = $"A maximum of {MaxAttachmentsPerMessage} files can be attached per message." });
        }

        foreach (var file in formFiles)
        {
            if (file.Length <= 0)
            {
                return BadRequest(new { error = $"File '{file.FileName}' is empty." });
            }

            if (file.Length > MaxAttachmentSizeBytes)
            {
                return BadRequest(new { error = $"File '{file.FileName}' exceeds the 25 MB maximum file size limit." });
            }

            if (!IsAllowedAttachment(file.FileName, file.ContentType))
            {
                return BadRequest(new { error = $"File '{file.FileName}' has an unsupported file type." });
            }
        }

        var existingAttachments = ExtractAttachments(session.MetadataJson);
        var uploaded = new List<SessionAttachmentDto>();
        var localAttachDir = GetAttachmentDirectory(id);

        foreach (var file in formFiles)
        {
            var attachmentId = Guid.NewGuid().ToString("N");
            var safeFileName = SanitizeFileName(file.FileName);
            var contentType = !string.IsNullOrWhiteSpace(file.ContentType) && !string.Equals(file.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                ? file.ContentType
                : GuessContentTypeFromExtension(safeFileName);
            var s3Key = $"sessions/{id}/attachments/{attachmentId}/{safeFileName}";
            var downloadUrl = $"/api/sessions/{id}/attachments/{attachmentId}/content";

            var localFilePath = Path.Combine(localAttachDir, $"{attachmentId}_{safeFileName}");
            await using (var localStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await file.CopyToAsync(localStream, HttpContext?.RequestAborted ?? CancellationToken.None);
            }

            if (storageService != null)
            {
                try
                {
                    await using var s3Stream = file.OpenReadStream();
                    await storageService.UploadStreamAsync(s3Key, s3Stream, contentType, HttpContext?.RequestAborted ?? CancellationToken.None);
                }
                catch
                {
                    // Graceful fallback to local disk if SeaweedFS S3 is unreachable
                }
            }

            var dto = new SessionAttachmentDto(
                Id: attachmentId,
                SessionId: id,
                FileName: safeFileName,
                ContentType: contentType,
                SizeBytes: file.Length,
                S3Key: s3Key,
                DownloadUrl: downloadUrl,
                CreatedAt: DateTimeOffset.UtcNow
            );

            uploaded.Add(dto);
            existingAttachments.Add(dto);
        }

        session.MetadataJson = UpdateAttachmentsMetadata(session.MetadataJson, existingAttachments);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return Ok(uploaded);
    }

    [HttpGet("{id:guid}/attachments/{attachmentId}/content")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAttachmentContent(Guid id, string attachmentId, [FromQuery] bool download = false)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var attachments = ExtractAttachments(session.MetadataJson);
        var attachment = attachments.FirstOrDefault(a =>
            string.Equals(a.Id, attachmentId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.FileName, attachmentId, StringComparison.OrdinalIgnoreCase));

        if (attachment == null)
        {
            // Fallback: look into messages of this session
            var recentMessages = await db.SessionMessages
                .Where(m => m.SessionId == id && m.StructuredPayloadJson != null)
                .OrderByDescending(m => m.CreatedAt)
                .Take(25)
                .ToListAsync();

            foreach (var m in recentMessages)
            {
                var msgAttachments = ExtractAttachmentsFromPayload(m.StructuredPayloadJson);
                var matched = msgAttachments.FirstOrDefault(a =>
                    string.Equals(a.Id, attachmentId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.FileName, attachmentId, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                {
                    attachment = matched;
                    break;
                }
            }
        }

        if (attachment == null && (string.Equals(attachmentId, "undefined", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(attachmentId)))
        {
            attachment = attachments.LastOrDefault();
        }

        if (attachment == null)
        {
            var localAttachDir = GetAttachmentDirectory(id);
            if (Directory.Exists(localAttachDir))
            {
                var matchingFile = Directory.GetFiles(localAttachDir, $"{attachmentId}_*").FirstOrDefault()
                    ?? Directory.GetFiles(localAttachDir, $"*{attachmentId}*").FirstOrDefault();
                if (matchingFile != null)
                {
                    var fn = Path.GetFileName(matchingFile);
                    var underscoreIdx = fn.IndexOf('_');
                    var safeFn = underscoreIdx >= 0 ? fn[(underscoreIdx + 1)..] : fn;
                    attachment = new SessionAttachmentDto(
                        Id: attachmentId,
                        SessionId: id,
                        FileName: safeFn,
                        ContentType: GuessContentTypeFromExtension(safeFn),
                        SizeBytes: new FileInfo(matchingFile).Length,
                        S3Key: $"sessions/{id}/attachments/{attachmentId}/{safeFn}",
                        DownloadUrl: $"/api/sessions/{id}/attachments/{attachmentId}/content",
                        CreatedAt: DateTimeOffset.UtcNow
                    );
                }
            }
        }

        if (attachment == null && !string.IsNullOrWhiteSpace(session.MetadataJson))
        {
            try
            {
                using var metaDoc = JsonDocument.Parse(session.MetadataJson);
                if (metaDoc.RootElement.TryGetProperty("jobId", out var jProp) &&
                    Guid.TryParse(jProp.GetString(), out var jobId))
                {
                    var jobAttachDir = ProjectJobsController.GetAttachmentDirectory(jobId);
                    if (Directory.Exists(jobAttachDir))
                    {
                        var matchingJobFile = Directory.GetFiles(jobAttachDir, $"{attachmentId}_*").FirstOrDefault()
                            ?? Directory.GetFiles(jobAttachDir, $"*{attachmentId}*").FirstOrDefault();
                        if (matchingJobFile != null)
                        {
                            var fn = Path.GetFileName(matchingJobFile);
                            var underscoreIdx = fn.IndexOf('_');
                            var safeFn = underscoreIdx >= 0 ? fn[(underscoreIdx + 1)..] : fn;
                            attachment = new SessionAttachmentDto(
                                Id: attachmentId,
                                SessionId: id,
                                FileName: safeFn,
                                ContentType: GuessContentTypeFromExtension(safeFn),
                                SizeBytes: new FileInfo(matchingJobFile).Length,
                                S3Key: $"jobs/{jobId}/attachments/{attachmentId}/{safeFn}",
                                DownloadUrl: $"/api/sessions/{id}/attachments/{attachmentId}/content",
                                CreatedAt: DateTimeOffset.UtcNow
                            );
                        }
                    }
                }
            }
            catch { }
        }

        if (attachment == null)
        {
            return NotFound(new { error = $"Attachment '{attachmentId}' not found in session '{id}'." });
        }

        Stream? contentStream = null;
        if (storageService != null && !string.IsNullOrWhiteSpace(attachment.S3Key))
        {
            try
            {
                contentStream = await storageService.DownloadStreamAsync(attachment.S3Key, HttpContext?.RequestAborted ?? CancellationToken.None);
            }
            catch
            {
                contentStream = null;
            }
        }

        if (contentStream == null)
        {
            var localAttachDir = GetAttachmentDirectory(id);
            var localFilePath = Path.Combine(localAttachDir, $"{attachment.Id}_{attachment.FileName}");
            if (!System.IO.File.Exists(localFilePath))
            {
                var candidate = Directory.GetFiles(localAttachDir, $"{attachment.Id}_*").FirstOrDefault()
                    ?? Directory.GetFiles(localAttachDir, $"*{attachment.FileName}*").FirstOrDefault();
                if (candidate != null && System.IO.File.Exists(candidate))
                {
                    localFilePath = candidate;
                }
                else
                {
                    return NotFound(new { error = $"Attachment content for '{attachment.FileName}' was not found." });
                }
            }

            contentStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        var contentType = !string.IsNullOrWhiteSpace(attachment.ContentType)
            ? attachment.ContentType
            : GuessContentTypeFromExtension(attachment.FileName);

        var isInlineRenderable = !download && (
            contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
            contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

        var disposition = isInlineRenderable ? "inline" : "attachment";
        if (Response != null)
        {
            Response.Headers.ContentDisposition = $"{disposition}; filename=\"{SanitizeFileName(attachment.FileName)}\"";
        }

        return File(contentStream, contentType);
    }

    [HttpGet("{id:guid}/artifacts")]
    [AllowAnonymous]
    public async Task<IActionResult> GetArtifacts(Guid id)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var artifacts = ExtractArtifacts(session.MetadataJson);

        var dir = GetArtifactDirectory(id);
        if (Directory.Exists(dir))
        {
            var files = Directory.GetFiles(dir);
            bool updated = false;
            foreach (var f in files)
            {
                var fInfo = new FileInfo(f);
                var fName = fInfo.Name;
                if (!artifacts.Any(a => string.Equals(a.Name, fName, StringComparison.OrdinalIgnoreCase)))
                {
                    var artifactId = Guid.NewGuid().ToString();
                    var cType = GuessContentTypeFromExtension(fName);
                    artifacts.Add(new SessionArtifactDto(
                        Id: artifactId,
                        SessionId: id,
                        Name: fName,
                        Summary: null,
                        ContentType: cType,
                        SizeBytes: fInfo.Length,
                        CreatedAt: fInfo.CreationTimeUtc,
                        UpdatedAt: fInfo.LastWriteTimeUtc,
                        Content: null,
                        S3Key: $"sessions/{id}/artifacts/{artifactId}/{fName}",
                        DownloadUrl: $"/api/sessions/{id}/artifacts/{artifactId}/content"
                    ));
                    updated = true;
                }
            }
            if (updated)
            {
                session.MetadataJson = UpdateArtifactsMetadata(session.MetadataJson, artifacts);
                await db.SaveChangesAsync();
            }
        }

        return Ok(artifacts);
    }

    [HttpGet("{id:guid}/artifacts/{artifactId}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetArtifact(Guid id, string artifactId)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var artifacts = ExtractArtifacts(session.MetadataJson);
        var artifact = artifacts.FirstOrDefault(a => string.Equals(a.Id, artifactId, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, artifactId, StringComparison.OrdinalIgnoreCase));

        var dir = GetArtifactDirectory(id);
        var safeName = artifact != null ? artifact.Name : Path.GetFileName(artifactId);
        var filePath = Path.Combine(dir, safeName);

        if (!System.IO.File.Exists(filePath) && artifact == null)
        {
            return NotFound(new { error = $"Artifact '{artifactId}' not found in session '{id}'." });
        }

        var contentType = artifact?.ContentType ?? GuessContentTypeFromExtension(safeName);
        var isBinary = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(contentType, "application/zip", StringComparison.OrdinalIgnoreCase);

        string? content = null;
        if (!isBinary)
        {
            content = System.IO.File.Exists(filePath) ? await System.IO.File.ReadAllTextAsync(filePath) : artifact?.Content ?? string.Empty;
        }

        var now = DateTimeOffset.UtcNow;
        var resolvedId = artifact?.Id ?? Guid.NewGuid().ToString();
        var downloadUrl = artifact?.DownloadUrl ?? $"/api/sessions/{id}/artifacts/{resolvedId}/content";
        var resultDto = artifact != null
            ? artifact with { Content = content, DownloadUrl = downloadUrl }
            : new SessionArtifactDto(
                Id: resolvedId,
                SessionId: id,
                Name: safeName,
                Summary: null,
                ContentType: contentType,
                SizeBytes: System.IO.File.Exists(filePath) ? new FileInfo(filePath).Length : 0,
                CreatedAt: now,
                UpdatedAt: now,
                Content: content,
                S3Key: $"sessions/{id}/artifacts/{resolvedId}/{safeName}",
                DownloadUrl: downloadUrl
            );

        return Ok(resultDto);
    }

    [HttpGet("{id:guid}/artifacts/{artifactId}/content")]
    [AllowAnonymous]
    public async Task<IActionResult> GetArtifactContent(Guid id, string artifactId, [FromQuery] bool download = false)
    {
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var artifacts = ExtractArtifacts(session.MetadataJson);
        var artifact = artifacts.FirstOrDefault(a =>
            string.Equals(a.Id, artifactId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Name, artifactId, StringComparison.OrdinalIgnoreCase));

        var dir = GetArtifactDirectory(id);
        var safeName = artifact != null ? artifact.Name : Path.GetFileName(artifactId);
        var filePath = Path.Combine(dir, safeName);

        Stream? contentStream = null;
        if (storageService != null && !string.IsNullOrWhiteSpace(artifact?.S3Key))
        {
            try
            {
                contentStream = await storageService.DownloadStreamAsync(artifact.S3Key, HttpContext?.RequestAborted ?? CancellationToken.None);
            }
            catch
            {
                contentStream = null;
            }
        }

        if (contentStream == null)
        {
            if (System.IO.File.Exists(filePath))
            {
                contentStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            else if (!string.IsNullOrEmpty(artifact?.Content))
            {
                contentStream = new MemoryStream(Encoding.UTF8.GetBytes(artifact.Content));
            }
            else
            {
                return NotFound(new { error = $"Artifact '{artifactId}' content not found in session '{id}'." });
            }
        }

        var contentType = !string.IsNullOrWhiteSpace(artifact?.ContentType)
            ? artifact.ContentType
            : GuessContentTypeFromExtension(safeName);

        var disposition = download ? "attachment" : "inline";
        if (Response != null)
        {
            Response.Headers.ContentDisposition = $"{disposition}; filename=\"{SanitizeFileName(safeName)}\"";
        }

        return File(contentStream, contentType);
    }

    [HttpPost("{id:guid}/artifacts")]
    [RequestSizeLimit(262_144_000)]
    public async Task<IActionResult> SyncArtifact(Guid id, [FromBody] SyncArtifactRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest(new { error = "Artifact Name is required." });
        }

        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id);
        if (session == null)
        {
            return NotFound(new { error = $"Session '{id}' not found." });
        }

        var dir = GetArtifactDirectory(id);
        var safeFileName = Path.GetFileName(req.Name.Trim());
        var filePath = Path.Combine(dir, safeFileName);
        var contentType = !string.IsNullOrWhiteSpace(req.ContentType)
            ? req.ContentType
            : GuessContentTypeFromExtension(safeFileName);

        byte[] rawBytes;
        if (!string.IsNullOrWhiteSpace(req.Base64Content))
        {
            rawBytes = Convert.FromBase64String(req.Base64Content);
            await System.IO.File.WriteAllBytesAsync(filePath, rawBytes);
        }
        else
        {
            var textContent = req.Content ?? string.Empty;
            rawBytes = Encoding.UTF8.GetBytes(textContent);
            await System.IO.File.WriteAllTextAsync(filePath, textContent);
        }

        var fileInfo = new FileInfo(filePath);
        var artifacts = ExtractArtifacts(session.MetadataJson);
        var existingIndex = artifacts.FindIndex(a => string.Equals(a.Name, safeFileName, StringComparison.OrdinalIgnoreCase));

        var now = DateTimeOffset.UtcNow;
        SessionArtifactDto artifactDto;
        bool isNew = existingIndex < 0;
        var artifactId = isNew ? Guid.NewGuid().ToString() : artifacts[existingIndex].Id;
        var s3Key = $"sessions/{id}/artifacts/{artifactId}/{safeFileName}";
        var downloadUrl = $"/api/sessions/{id}/artifacts/{artifactId}/content";

        if (storageService != null)
        {
            try
            {
                await using var s3Stream = new MemoryStream(rawBytes);
                await storageService.UploadStreamAsync(s3Key, s3Stream, contentType, HttpContext?.RequestAborted ?? CancellationToken.None);
            }
            catch
            {
                // Graceful fallback to local disk if SeaweedFS S3 is unreachable
            }
        }

        if (isNew)
        {
            artifactDto = new SessionArtifactDto(
                Id: artifactId,
                SessionId: id,
                Name: safeFileName,
                Summary: req.Summary,
                ContentType: contentType,
                SizeBytes: fileInfo.Length,
                CreatedAt: now,
                UpdatedAt: now,
                Content: string.IsNullOrWhiteSpace(req.Base64Content) ? req.Content : null,
                S3Key: s3Key,
                DownloadUrl: downloadUrl
            );
            artifacts.Add(artifactDto);
        }
        else
        {
            var old = artifacts[existingIndex];
            artifactDto = old with
            {
                Summary = req.Summary ?? old.Summary,
                ContentType = contentType,
                SizeBytes = fileInfo.Length,
                UpdatedAt = now,
                Content = string.IsNullOrWhiteSpace(req.Base64Content) ? req.Content : null,
                S3Key = s3Key,
                DownloadUrl = downloadUrl
            };
            artifacts[existingIndex] = artifactDto;
        }

        session.MetadataJson = UpdateArtifactsMetadata(session.MetadataJson, artifacts);
        session.UpdatedAt = now;
        await db.SaveChangesAsync();

        var eventName = isNew ? "ArtifactCreated" : "ArtifactUpdated";
        await hubContext.Clients.Group($"session-{id}").SendAsync(eventName, artifactDto);
        await hubContext.Clients.All.SendAsync(eventName, artifactDto);
        await hubContext.Clients.All.SendAsync("SessionUpdated", session);

        return Ok(artifactDto);
    }

    private static SessionMessageDto ToMessageDto(SessionMessage m) => new(
        m.Id,
        m.SessionId,
        m.SenderAgentId,
        m.TargetAgentId,
        m.Content,
        m.StructuredPayloadJson,
        m.CreatedAt,
        m.EventType,
        m.ToolName,
        m.ToolArgsJson,
        m.ToolResultJson
    );

    private async Task CheckAndForwardJobTurnResponseAsync(AgentSession session, SessionMessage responseMessage)
    {
        if (string.IsNullOrWhiteSpace(session.MetadataJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(session.MetadataJson);
            if (!doc.RootElement.TryGetProperty("jobId", out var jElem)) return;
            var jobIdStr = jElem.GetString();
            if (string.IsNullOrWhiteSpace(jobIdStr) || !Guid.TryParse(jobIdStr, out var jobId)) return;

            var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == jobId);
            if (job == null) return;

            var alreadyPosted = await db.JobMessages.AnyAsync(m => m.JobId == jobId && m.TurnSessionId == session.Id && m.Content == responseMessage.Content);
            if (alreadyPosted) return;

            var (pId, pName, _, _) = await ResolveSessionPersonaAsync(session);

            var matches = Regex.Matches(responseMessage.Content, @"@([a-zA-Z0-9_\-]+)");
            var mentions = matches.Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();

            var jobMsg = new JobMessage
            {
                Id = Guid.NewGuid(),
                JobId = jobId,
                SenderType = "persona",
                SenderId = pId ?? session.PersonaId,
                SenderName = pName ?? session.PersonaId,
                Content = responseMessage.Content,
                MentionsJson = JsonSerializer.Serialize(mentions, JsonOptions),
                TurnSessionId = session.Id,
                CreatedAt = DateTimeOffset.UtcNow
            };

            db.JobMessages.Add(jobMsg);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            var msgDto = new JobMessageDto(
                jobMsg.Id,
                jobMsg.JobId,
                jobMsg.SenderType,
                jobMsg.SenderId,
                jobMsg.SenderName,
                jobMsg.Content,
                mentions,
                jobMsg.TurnSessionId,
                jobMsg.StructuredPayloadJson,
                jobMsg.CreatedAt
            );

            await hubContext.Clients.All.SendAsync("JobMessageAdded", msgDto);

            // Autonomous Handoff Check (Loop Guard: max 5 consecutive persona hops)
            var recentMessages = await db.JobMessages
                .AsNoTracking()
                .Where(m => m.JobId == jobId)
                .OrderByDescending(m => m.CreatedAt)
                .Take(10)
                .ToListAsync();

            var consecutivePersonaCount = recentMessages.TakeWhile(m => m.SenderType == "persona").Count();
            if (consecutivePersonaCount < 5)
            {
                var assignedPersonaIds = await db.ProjectPersonas
                    .AsNoTracking()
                    .Where(pp => pp.ProjectId == job.ProjectId)
                    .Select(pp => pp.PersonaId)
                    .ToListAsync();

                var nextPersonaId = mentions.FirstOrDefault(m => assignedPersonaIds.Contains(m) && !string.Equals(m, pId, StringComparison.OrdinalIgnoreCase));
                if (nextPersonaId != null)
                {
                    var nextPersona = await db.Personas.FirstOrDefaultAsync(p => p.Id == nextPersonaId);
                    var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == job.ProjectId);
                    if (nextPersona != null && project != null)
                    {
                        var driverId = nextPersona.DriverId.Trim().ToLowerInvariant();
                        var driver = await db.Agents.FirstOrDefaultAsync(d => d.Id == driverId);
                        if (driver == null)
                        {
                            driver = new Agent
                            {
                                Id = driverId,
                                Name = $"Driver {driverId}",
                                RuntimeType = RuntimeType.CliAgy,
                                TrustTier = TrustTier.SeniorManager
                            };
                            db.Agents.Add(driver);
                        }

                        var turnMetadata = new Dictionary<string, object?>
                        {
                            ["jobId"] = job.Id.ToString(),
                            ["projectId"] = project.Id,
                            ["projectName"] = project.Name,
                            ["workspaceType"] = project.WorkspaceType.ToString(),
                            ["gitBranch"] = job.GitBranchName,
                            ["gitUrl"] = project.GitRepositoryUrl,
                            ["gitBaseBranch"] = project.GitBaseBranch,
                            ["s3ArchiveKey"] = job.S3ArchiveKey,
                            ["projectInstructions"] = project.ProjectInstructions,
                            ["personaId"] = nextPersona.Id,
                            ["personaName"] = nextPersona.Name,
                            ["personaRole"] = nextPersona.Role,
                            ["personaPrompt"] = nextPersona.PersonaPrompt,
                            ["workingDirectory"] = nextPersona.DefaultWorkingDirectory,
                            ["model"] = nextPersona.DefaultModel,
                            ["effort"] = nextPersona.DefaultEffort,
                            ["mode"] = nextPersona.DefaultMode,
                            ["tier"] = nextPersona.DefaultTier,
                            ["contextSize"] = nextPersona.DefaultContextSize
                        };

                        var nextTurnSession = new AgentSession
                        {
                            Id = Guid.NewGuid(),
                            Title = $"Job Turn: {job.Title} (@{nextPersona.Name})",
                            Status = "thinking",
                            MetadataJson = JsonSerializer.Serialize(turnMetadata, JsonOptions),
                            PersonaId = nextPersona.Id,
                            DriverId = driverId,
                            CreatorAgentId = driverId,
                            CreatedAt = DateTimeOffset.UtcNow,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        db.AgentSessions.Add(nextTurnSession);

                        var promptWithContext = $"[Project Context: Project \"{project.Name}\" ({project.WorkspaceType}), Branch: \"{job.GitBranchName}\"]\n" +
                                                (!string.IsNullOrWhiteSpace(project.ProjectInstructions) ? $"[Project Guidelines: {project.ProjectInstructions}]\n" : "") +
                                                $"[Job Context: {job.Title} - {job.Description}]\n" +
                                                $"[Previous Handoff from @{pName ?? pId}]:\n{responseMessage.Content}";

                        var promptMsg = new SessionMessage
                        {
                            SessionId = nextTurnSession.Id,
                            SenderAgentId = pId ?? "agent",
                            TargetAgentId = driverId,
                            EventType = "prompt",
                            Content = promptWithContext,
                            CreatedAt = DateTimeOffset.UtcNow
                        };
                        db.SessionMessages.Add(promptMsg);

                        if (AgentMeshHub.IsAgentConnected(driverId))
                        {
                            driver.Status = "busy";
                            driver.LastHeartbeatAt = DateTimeOffset.UtcNow;
                        }

                        await db.SaveChangesAsync();

                        var promptPayload = new
                        {
                            sessionId = nextTurnSession.Id,
                            messageId = promptMsg.Id,
                            agentId = driverId,
                            driverId = driverId,
                            personaId = nextPersona.Id,
                            personaName = nextPersona.Name,
                            personaRole = nextPersona.Role,
                            personaPrompt = nextPersona.PersonaPrompt,
                            prompt = promptMsg.Content,
                            sender = promptMsg.SenderAgentId,
                            workingDirectory = nextPersona.DefaultWorkingDirectory,
                            model = nextPersona.DefaultModel,
                            effort = nextPersona.DefaultEffort,
                            mode = nextPersona.DefaultMode,
                            tier = nextPersona.DefaultTier,
                            contextSize = nextPersona.DefaultContextSize,
                            jobId = job.Id,
                            projectId = project.Id,
                            gitBranch = job.GitBranchName,
                            createdAt = promptMsg.CreatedAt
                        };

                        await hubContext.Clients.Group($"agent-{driverId}").SendAsync("PromptReceived", promptPayload);
                        await hubContext.Clients.All.SendAsync("PromptReceived", promptPayload);
                    }
                }
            }
        }
        catch { }
    }
}

public record CreateSessionRequest(
    string Title,
    string? MetadataJson = null,
    string? InitialMessage = null,
    string? CreatorAgentId = null,
    string? WorkingDirectory = null,
    string? Model = null,
    string? Effort = null,
    string? Mode = null,
    string? Tier = null,
    string? PersonaId = null,
    string? DriverId = null,
    List<SessionAttachmentDto>? Attachments = null,
    string? ContextSize = null
);

public record PostMessageRequest(
    string Content,
    string? SenderAgentId = null,
    string? TargetAgentId = null,
    string? StructuredPayloadJson = null,
    string? EventType = null,
    string? ToolName = null,
    string? ToolArgsJson = null,
    string? ToolResultJson = null,
    DateTimeOffset? CreatedAt = null,
    List<SessionAttachmentDto>? Attachments = null
);

public record UpdateSessionRequest(
    string? Title = null,
    string? Status = null,
    string? MetadataJson = null,
    string? Model = null,
    string? Effort = null,
    string? Tier = null,
    string? Mode = null,
    string? WorkingDirectory = null,
    string? ContextSize = null
);

public record SessionSummaryDto(
    Guid Id,
    string Title,
    string Status,
    string MetadataJson,
    string? CreatorAgentId,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? DriverId = null,
    string? DriverName = null,
    string? PersonaId = null,
    string? PersonaName = null
);

public record SessionDetailDto(
    Guid Id,
    string Title,
    string Status,
    string MetadataJson,
    string? CreatorAgentId,
    string? CreatorAgentName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<SessionMessageDto> Messages,
    string? DriverId = null,
    string? DriverName = null,
    string? PersonaId = null,
    string? PersonaName = null,
    string? PersonaRole = null
);

public record SessionMessageDto(
    Guid Id,
    Guid SessionId,
    string SenderAgentId,
    string? TargetAgentId,
    string Content,
    string? StructuredPayloadJson,
    DateTimeOffset CreatedAt,
    string EventType = "response",
    string? ToolName = null,
    string? ToolArgsJson = null,
    string? ToolResultJson = null
);

public record QueuedPromptDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("effort")] string? Effort = null,
    [property: JsonPropertyName("tier")] string? Tier = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("workingDirectory")] string? WorkingDirectory = null,
    [property: JsonPropertyName("attachments")] List<SessionAttachmentDto>? Attachments = null,
    [property: JsonPropertyName("contextSize")] string? ContextSize = null
);

public record EnqueuePromptRequest(
    string Prompt,
    string? SenderAgentId = null,
    bool SendNow = false,
    string? Model = null,
    string? Effort = null,
    string? Tier = null,
    string? Mode = null,
    string? WorkingDirectory = null,
    List<SessionAttachmentDto>? Attachments = null,
    string? ContextSize = null
);

public record QuestionAnswerDto(
    int QuestionIndex,
    string Question,
    List<string> SelectedOptions,
    string? CustomText = null
);

public record SubmitAnswerRequest(
    Guid? ToolCallId,
    List<QuestionAnswerDto> Answers
);

public record SessionArtifactDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("s3Key")] string? S3Key = null,
    [property: JsonPropertyName("downloadUrl")] string? DownloadUrl = null
);

public record SyncArtifactRequest(
    string Name,
    string Content,
    string? Summary = null,
    string? ContentType = "text/markdown",
    string? Base64Content = null
);

public record SessionAttachmentDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("s3Key")] string S3Key,
    [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt
);



