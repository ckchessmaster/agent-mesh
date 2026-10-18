using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text.Json;
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
[Route("api/[controller]")]
[Authorize]
public class AgentsController(
    AgentMeshDbContext db,
    IApiKeyService apiKeyService,
    IHubContext<AgentMeshHub>? hubContext = null) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterAgent([FromBody] RegisterAgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest(new { error = "Agent Id and Name are required." });
        }

        var normalizedId = req.Id.Trim().ToLowerInvariant();

        var existing = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (existing != null)
        {
            return Conflict(new { error = $"Agent with Id '{normalizedId}' already exists." });
        }

        var rawToken = apiKeyService.GenerateToken();
        var hashedToken = apiKeyService.HashToken(rawToken);

        var agent = new Agent
        {
            Id = normalizedId,
            Name = req.Name.Trim(),
            Role = req.Role ?? "Autonomous Agent",
            Status = req.Status ?? (AgentMeshHub.IsAgentConnected(normalizedId) ? "online" : "offline"),
            RuntimeType = req.RuntimeType,
            TrustTier = req.TrustTier,
            PersonaPrompt = req.PersonaPrompt ?? $"Autonomous AI Agent ({normalizedId})",
            MemorySummary = req.MemorySummary ?? string.Empty,
            CapabilitiesJson = req.CapabilitiesJson ?? "[]",
            ApiKeyHash = hashedToken,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var dto = ToDto(agent, 0);
        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("AgentRegistered", dto);
        }

        return CreatedAtAction(nameof(GetAgent), new { id = agent.Id }, new
        {
            agent = dto,
            token = rawToken,
            instructions = $"Run 'agentmesh start {agent.Id}' or include 'Authorization: Bearer <token>' in your agent requests."
        });
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> UpsertAgent([FromBody] RegisterAgentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest(new { error = "Agent Id and Name are required." });
        }

        var normalizedId = req.Id.Trim().ToLowerInvariant();
        var existing = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);

        if (existing != null)
        {
            existing.Name = req.Name.Trim();
            if (!string.IsNullOrWhiteSpace(req.Role)) existing.Role = req.Role.Trim();
            if (!string.IsNullOrWhiteSpace(req.Status)) existing.Status = req.Status.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(req.PersonaPrompt)) existing.PersonaPrompt = req.PersonaPrompt;
            if (!string.IsNullOrWhiteSpace(req.CapabilitiesJson)) existing.CapabilitiesJson = req.CapabilitiesJson;
            existing.LastHeartbeatAt = DateTimeOffset.UtcNow;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
            var sessionCount = await db.AgentSessions.CountAsync(s => s.CreatorAgentId == normalizedId);
            var updatedDto = ToDto(existing, sessionCount);

            if (hubContext != null)
            {
                await hubContext.Clients.All.SendAsync("AgentUpdated", updatedDto);
            }

            return Ok(updatedDto);
        }

        return await RegisterAgent(req);
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentAgent()
    {
        var agentId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("AgentId")?.Value;

        if (string.IsNullOrEmpty(agentId))
        {
            return Unauthorized(new { error = "No agent identity found in token claims." });
        }

        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent == null)
        {
            return NotFound(new { error = $"Agent '{agentId}' not found." });
        }

        var sessionCount = await db.AgentSessions.CountAsync(s => s.CreatorAgentId == agent.Id);
        return Ok(ToDto(agent, sessionCount));
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgents()
    {
        var agents = await db.Agents
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .ToListAsync();

        var sessionCounts = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.CreatorAgentId != null)
            .GroupBy(s => s.CreatorAgentId!)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count);

        var dtos = agents
            .Select(a => ToDto(a, sessionCounts.GetValueOrDefault(a.Id, 0)))
            .ToList();

        return Ok(dtos);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgent(string id)
    {
        var normalizedId = id.ToLowerInvariant();
        var agent = await db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == normalizedId);

        if (agent == null)
        {
            return NotFound(new { error = $"Agent '{id}' not found." });
        }

        var sessionCount = await db.AgentSessions.CountAsync(s => s.CreatorAgentId == normalizedId);
        return Ok(ToDto(agent, sessionCount));
    }

    [HttpGet("{id}/sessions")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgentSessions(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var sessions = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.CreatorAgentId == normalizedId)
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new SessionSummaryDto(
                s.Id,
                s.Title,
                s.Status,
                s.MetadataJson,
                s.CreatorAgentId,
                s.Messages.Count,
                s.CreatedAt,
                s.UpdatedAt
            ))
            .ToListAsync();

        return Ok(sessions);
    }

    [HttpPost("{id}/sessions")]
    public async Task<IActionResult> CreateAgentSession(string id, [FromBody] CreateAgentSessionRequest req)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (agent == null)
        {
            return NotFound(new { error = $"Agent '{normalizedId}' not found." });
        }

        if (string.IsNullOrWhiteSpace(req.Title))
        {
            return BadRequest(new { error = "Session Title is required." });
        }

        var hasInitialPrompt = !string.IsNullOrWhiteSpace(req.InitialPrompt);
        var initialMetadata = SessionsController.MergeSessionMetadata(req.MetadataJson, req.WorkingDirectory, req.Model, req.Effort, req.Mode, req.Tier);
        var session = new AgentSession
        {
            Title = req.Title.Trim(),
            Status = hasInitialPrompt ? "thinking" : "active",
            MetadataJson = initialMetadata,
            CreatorAgentId = normalizedId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.AgentSessions.Add(session);

        SessionMessage? promptMsg = null;
        if (hasInitialPrompt)
        {
            promptMsg = new SessionMessage
            {
                SessionId = session.Id,
                SenderAgentId = "operator",
                TargetAgentId = normalizedId,
                EventType = "prompt",
                Content = req.InitialPrompt!.Trim(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(promptMsg);
            if (AgentMeshHub.IsAgentConnected(normalizedId))
            {
                agent.Status = "busy";
                agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("SessionCreated", new SessionSummaryDto(
                session.Id,
                session.Title,
                session.Status,
                session.MetadataJson,
                session.CreatorAgentId,
                hasInitialPrompt ? 1 : 0,
                session.CreatedAt,
                session.UpdatedAt
            ));

            if (promptMsg != null)
            {
                var (workDir, model, effort, mode, tier) = SessionsController.ExtractSessionSettings(session.MetadataJson);
                await hubContext.Clients.Group($"agent-{normalizedId}").SendAsync("PromptReceived", new
                {
                    sessionId = session.Id,
                    messageId = promptMsg.Id,
                    agentId = normalizedId,
                    prompt = promptMsg.Content,
                    sender = promptMsg.SenderAgentId,
                    workingDirectory = workDir,
                    model = model,
                    effort = effort,
                    mode = mode,
                    tier = tier
                });
            }
        }

        return CreatedAtAction("GetSession", "Sessions", new { id = session.Id }, session);
    }

    [HttpGet("{id}/usage")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgentUsage(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (agent == null)
        {
            return NotFound(new { error = $"Agent '{normalizedId}' not found." });
        }

        var sessions = await db.AgentSessions
            .Where(s => s.CreatorAgentId == normalizedId)
            .ToListAsync();

        long totalInputTokens = 0;
        long totalOutputTokens = 0;
        long totalThinkingTokens = 0;
        long totalTokens = 0;
        double totalDurationSeconds = 0;

        foreach (var sess in sessions)
        {
            if (string.IsNullOrWhiteSpace(sess.MetadataJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(sess.MetadataJson);
                if (doc.RootElement.TryGetProperty("cumulativeUsage", out var usage))
                {
                    if (usage.TryGetProperty("inputTokens", out var ip)) totalInputTokens += ip.GetInt64();
                    if (usage.TryGetProperty("outputTokens", out var op)) totalOutputTokens += op.GetInt64();
                    if (usage.TryGetProperty("thinkingTokens", out var tp)) totalThinkingTokens += tp.GetInt64();
                    if (usage.TryGetProperty("totalTokens", out var totp)) totalTokens += totp.GetInt64();
                    if (usage.TryGetProperty("totalDurationSeconds", out var dp)) totalDurationSeconds += dp.GetDouble();
                }
            }
            catch { }
        }

        if (totalTokens == 0 && (totalInputTokens > 0 || totalOutputTokens > 0))
        {
            totalTokens = totalInputTokens + totalOutputTokens;
        }

        var providerQuota = await GetAgentProviderQuotaAsync(agent);

        return Ok(new AgentUsageResponse(
            AgentId: normalizedId,
            AgentName: agent.Name,
            SessionCount: sessions.Count,
            LifetimeTokens: new LifetimeTokensDto(
                InputTokens: totalInputTokens,
                OutputTokens: totalOutputTokens,
                ThinkingTokens: totalThinkingTokens,
                TotalTokens: totalTokens,
                TotalDurationSeconds: totalDurationSeconds
            ),
            ProviderQuota: providerQuota
        ));
    }

    private static readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, ProviderQuotaDto Quota)> QuotaCache = new();

    private static async Task<ProviderQuotaDto?> GetAgentProviderQuotaAsync(Agent agent)
    {
        var id = agent.Id.ToLowerInvariant();
        var isAgy = id == "agy" || id.Contains("agy") || id.Contains("antigravity");
        var isCopilot = id == "copilot" || id.Contains("copilot");

        if (isAgy)
        {
            if (QuotaCache.TryGetValue("agy", out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(60))
            {
                return cached.Quota;
            }

            var agyPath = ResolveExecutablePath("agy");
            if (agyPath != null)
            {
                try
                {
                    using var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = agyPath,
                            ArgumentList = { "-p", "/usage", "--output-format", "json" },
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    proc.Start();
                    var outputTask = proc.StandardOutput.ReadToEndAsync();
                    if (await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(5000)) == proc.WaitForExitAsync())
                    {
                        var stdOut = await outputTask;
                        if (!string.IsNullOrWhiteSpace(stdOut))
                        {
                            var parsed = ParseAgyUsageJson(stdOut);
                            if (parsed != null)
                            {
                                QuotaCache["agy"] = (DateTimeOffset.UtcNow, parsed);
                                return parsed;
                            }
                        }
                    }
                    else
                    {
                        try { proc.Kill(true); } catch { }
                    }
                }
                catch { }
            }

            // Fallback default mock if binary not callable
            var defaultAgyQuota = new ProviderQuotaDto(
                Provider: "Google Antigravity",
                PlanName: "Pro Tier",
                PrimaryRemainingFraction: 0.88,
                Groups: new List<QuotaGroupDto>
                {
                    new("Gemini Models", "Gemini Flash & Gemini Pro", new List<QuotaBucketDto>
                    {
                        new("gemini-weekly", "Weekly Limit Remaining", "Refreshes weekly", "weekly", 0.88, DateTimeOffset.UtcNow.AddDays(6).ToString("u")),
                        new("gemini-5h", "Five Hour Limit Remaining", "Refreshes every 5 hours", "5h", 0.90, DateTimeOffset.UtcNow.AddHours(3).ToString("u"))
                    }),
                    new("Claude & GPT Models", "Claude Opus, Sonnet & GPT-OSS", new List<QuotaBucketDto>
                    {
                        new("3p-weekly", "Weekly Limit Remaining", "Shared 3rd-party weekly limit", "weekly", 1.0, DateTimeOffset.UtcNow.AddDays(7).ToString("u")),
                        new("3p-5h", "Five Hour Limit Remaining", "Shared 3rd-party 5-hour limit", "5h", 1.0, DateTimeOffset.UtcNow.AddHours(5).ToString("u"))
                    }),
                    new("Gemini 4 Argon", "Argon Weekly Allowance", new List<QuotaBucketDto>
                    {
                        new("argon-weekly", "Argon Weekly Limit", "Dedicated Argon capacity", "weekly", 0.59, DateTimeOffset.UtcNow.AddDays(6).ToString("u"))
                    })
                }
            );
            return defaultAgyQuota;
        }

        if (isCopilot)
        {
            return new ProviderQuotaDto(
                Provider: "GitHub Copilot",
                PlanName: "Copilot Free / Pro",
                PrimaryRemainingFraction: 1.0,
                Groups: new List<QuotaGroupDto>
                {
                    new("AI Credits & Completions", "Monthly Model Access Allowance", new List<QuotaBucketDto>
                    {
                        new("copilot-credits", "Monthly AI Credits", "Active subscription quota", "monthly", 1.0, DateTimeOffset.UtcNow.AddDays(14).ToString("u")),
                        new("copilot-autopilot", "Autopilot Continuation Cap", "Default 5 continues per turn", "turn", 1.0, null)
                    })
                }
            );
        }

        return null;
    }

    private static ProviderQuotaDto? ParseAgyUsageJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("command", out var cmdProp) ||
                !cmdProp.TryGetProperty("data", out var dataProp) ||
                !dataProp.TryGetProperty("groups", out var groupsElem))
            {
                return null;
            }

            var groups = new List<QuotaGroupDto>();
            double? primaryRemaining = null;

            foreach (var g in groupsElem.EnumerateArray())
            {
                var gName = g.TryGetProperty("name", out var gn) ? gn.GetString() ?? "" : "";
                var gDesc = g.TryGetProperty("description", out var gd) ? gd.GetString() : null;
                var buckets = new List<QuotaBucketDto>();

                if (g.TryGetProperty("buckets", out var bucketsElem))
                {
                    foreach (var b in bucketsElem.EnumerateArray())
                    {
                        var bId = b.TryGetProperty("id", out var bi) ? bi.GetString() ?? "" : "";
                        var bName = b.TryGetProperty("name", out var bn) ? bn.GetString() ?? "" : "";
                        var bDesc = b.TryGetProperty("description", out var bd) ? bd.GetString() : null;
                        var bWindow = b.TryGetProperty("window", out var bw) ? bw.GetString() : null;
                        var bFrac = b.TryGetProperty("remaining_fraction", out var bf) ? bf.GetDouble() : 1.0;
                        var bReset = b.TryGetProperty("reset_time", out var br) ? br.GetString() : null;

                        if (primaryRemaining == null || bFrac < primaryRemaining)
                        {
                            primaryRemaining = bFrac;
                        }

                        buckets.Add(new QuotaBucketDto(bId, bName, bDesc, bWindow, bFrac, bReset));
                    }
                }

                groups.Add(new QuotaGroupDto(gName, gDesc, buckets));
            }

            return new ProviderQuotaDto(
                Provider: "Google Antigravity",
                PlanName: "Pro Tier",
                PrimaryRemainingFraction: primaryRemaining ?? 1.0,
                Groups: groups
            );
        }
        catch
        {
            return null;
        }
    }

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

    private static AgentDto ToDto(Agent a, int sessionCount) => new(
        a.Id,
        a.Name,
        a.RuntimeType.ToString(),
        (int)a.TrustTier,
        a.PersonaPrompt,
        a.CreatedAt,
        a.Role,
        a.Status,
        a.CapabilitiesJson,
        a.LastHeartbeatAt,
        sessionCount
    );
}

public record RegisterAgentRequest(
    string Id,
    string Name,
    RuntimeType RuntimeType = RuntimeType.CliAgy,
    TrustTier TrustTier = TrustTier.Intern,
    string? PersonaPrompt = null,
    string? MemorySummary = null,
    string? Role = null,
    string? Status = null,
    string? CapabilitiesJson = null
);

public record CreateAgentSessionRequest(
    string Title,
    string? InitialPrompt = null,
    string? MetadataJson = null,
    string? WorkingDirectory = null,
    string? Model = null,
    string? Effort = null,
    string? Mode = null,
    string? Tier = null
);

public record AgentDto(
    string Id,
    string Name,
    string RuntimeType,
    int TrustTier,
    string PersonaPrompt,
    DateTimeOffset CreatedAt,
    string Role = "Autonomous Agent",
    string Status = "online",
    string CapabilitiesJson = "[]",
    DateTimeOffset? LastHeartbeatAt = null,
    int SessionCount = 0
);

public record QuotaBucketDto(
    string Id,
    string Name,
    string? Description,
    string? Window,
    double RemainingFraction,
    string? ResetTime
);

public record QuotaGroupDto(
    string Name,
    string? Description,
    List<QuotaBucketDto> Buckets
);

public record ProviderQuotaDto(
    string Provider,
    string PlanName,
    double PrimaryRemainingFraction,
    List<QuotaGroupDto> Groups
);

public record LifetimeTokensDto(
    long InputTokens,
    long OutputTokens,
    long ThinkingTokens,
    long TotalTokens,
    double TotalDurationSeconds
);

public record AgentUsageResponse(
    string AgentId,
    string AgentName,
    int SessionCount,
    LifetimeTokensDto LifetimeTokens,
    ProviderQuotaDto? ProviderQuota
);
