using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Api.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Route("api/drivers")]
[Authorize]
public class AgentsController(
    AgentMeshDbContext db,
    IApiKeyService apiKeyService,
    IHubContext<AgentMeshHub>? hubContext = null,
    IDriverQuotaProbe? quotaProbe = null,
    IPersonaContainerManager? containerManager = null,
    IDriverCapabilityProbe? capabilityProbe = null) : ControllerBase
{
    private IDriverQuotaProbe QuotaProbe => quotaProbe ?? NullDriverQuotaProbe.Instance;
    private IDriverCapabilityProbe CapabilityProbe => capabilityProbe ?? NullDriverCapabilityProbe.Instance;
    private static readonly ConcurrentDictionary<string, HostAuthSession> _activeHostAuthSessions = new(StringComparer.OrdinalIgnoreCase);

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
            Role = string.IsNullOrWhiteSpace(req.Role) ? AgentKind.DriverRole : req.Role.Trim(),
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
            // Patch, never replace: callers only know the fields they render, and a rebuilt blob
            // used to erase whatever the other caller had written.
            if (!string.IsNullOrWhiteSpace(req.CapabilitiesJson))
            {
                existing.CapabilitiesJson = DriverCapabilities.Merge(existing.CapabilitiesJson, req.CapabilitiesJson)
                    ?? existing.CapabilitiesJson;
            }
            existing.LastHeartbeatAt = DateTimeOffset.UtcNow;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
            var sessionCount = await db.AgentSessions.CountAsync(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId);
            var personas = await GetDriverPersonasSummaryAsync(normalizedId);
            var updatedDto = ToDto(existing, sessionCount, personas);

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

        var sessionCount = await db.AgentSessions.CountAsync(s => s.DriverId == agent.Id || s.CreatorAgentId == agent.Id);
        var personas = await GetDriverPersonasSummaryAsync(agent.Id);
        return Ok(ToDto(agent, sessionCount, personas));
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgents([FromQuery] bool includeRunnerIdentities = false)
    {
        var agents = await db.Agents
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .ToListAsync();

        var personaOwners = await GetPersonaDriverMapAsync();

        // Persona runner identities are rows that exist only so a persona's bearer token can be
        // authenticated (see AgentKind). Listing them here is what made every driver appear twice
        // on the drivers tab, wearing the persona's name and role.
        // They stay reachable by id via GET /api/agents/{id} and by token, so auth is unaffected.
        var drivers = includeRunnerIdentities
            ? agents
            : agents.Where(a => !AgentKind.IsPersonaRunnerIdentity(a, personaOwners)).ToList();

        var sessionCounts = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.CreatorAgentId != null || s.DriverId != null)
            .GroupBy(s => s.DriverId ?? s.CreatorAgentId!)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var allPersonas = await db.Personas
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync();

        var personasByDriver = allPersonas
            .GroupBy(p => p.DriverId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => new DriverPersonaSummaryDto(p.Id, p.Name, p.Role, p.DefaultModel)).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var dtos = drivers
            .Select(a => ToDto(
                a,
                sessionCounts.GetValueOrDefault(a.Id, 0),
                personasByDriver.GetValueOrDefault(a.Id, new List<DriverPersonaSummaryDto>())))
            .ToList();

        return Ok(dtos);
    }

    /// <summary>Persona id -> owning driver id. Used to recognise legacy runner-identity rows.</summary>
    private async Task<Dictionary<string, string>> GetPersonaDriverMapAsync()
    {
        var rows = await db.Personas
            .AsNoTracking()
            .Select(p => new { p.Id, p.DriverId })
            .ToListAsync();

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Id) && !string.IsNullOrWhiteSpace(row.DriverId))
            {
                map[row.Id.Trim().ToLowerInvariant()] = row.DriverId.Trim().ToLowerInvariant();
            }
        }

        return map;
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

        var sessionCount = await db.AgentSessions.CountAsync(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId);
        var personas = await GetDriverPersonasSummaryAsync(normalizedId);
        return Ok(ToDto(agent, sessionCount, personas));
    }

    /// <summary>
    /// Reads a driver's runtime configuration out of its capability blob, without the API key.
    /// </summary>
    [HttpGet("{id}/config")]
    [AllowAnonymous]
    public async Task<IActionResult> GetDriverConfig(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (agent == null)
        {
            return NotFound(new { error = $"Driver '{id}' not found." });
        }

        return Ok(DriverCapabilities.Parse(agent.CapabilitiesJson));
    }

    /// <summary>
    /// Updates a driver's runtime configuration. Sends only the fields being changed; the blob is
    /// merged, so unset fields keep their stored values.
    /// </summary>
    [HttpPut("{id}/config")]
    public async Task<IActionResult> UpdateDriverConfig(string id, [FromBody] UpdateDriverConfigRequest req)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (agent == null)
        {
            return NotFound(new { error = $"Driver '{id}' not found." });
        }

        var patch = new JsonObject();
        if (req.Endpoint is not null) patch["endpoint"] = req.Endpoint;
        if (req.DefaultModel is not null) patch["defaultModel"] = req.DefaultModel;
        if (req.TimeoutSeconds is not null) patch["timeoutSeconds"] = req.TimeoutSeconds;
        if (req.MaxRetries is not null) patch["maxRetries"] = req.MaxRetries;
        if (req.RetryDelayMilliseconds is not null) patch["retryDelayMilliseconds"] = req.RetryDelayMilliseconds;
        if (req.MaxSteps is not null) patch["maxSteps"] = req.MaxSteps;
        if (req.Capabilities is not null)
        {
            var caps = new JsonArray();
            foreach (var cap in req.Capabilities) caps.Add(JsonValue.Create(cap));
            patch["capabilities"] = caps;
        }

        // The key is write-only from the dashboard's point of view: an empty/absent value means
        // "leave what's stored", never "erase". Clearing it requires the CLI's local config.
        if (!string.IsNullOrWhiteSpace(req.ApiKey)) patch["apiKey"] = req.ApiKey;

        if (req.Adapter is not null) patch["adapter"] = req.Adapter;
        if (req.LoginCommand is not null) patch["loginCommand"] = req.LoginCommand;
        if (req.AuthCheckCommand is not null) patch["authCheckCommand"] = req.AuthCheckCommand;
        if (req.Sidecar is not null) patch["sidecar"] = JsonSerializer.SerializeToNode(req.Sidecar);

        if (patch.Count == 0)
        {
            return Ok(DriverCapabilities.Parse(agent.CapabilitiesJson));
        }

        agent.CapabilitiesJson = DriverCapabilities.Merge(agent.CapabilitiesJson, patch.ToJsonString())
            ?? agent.CapabilitiesJson;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        CapabilityProbe.InvalidateCache();
        if (req.Endpoint is not null || !string.IsNullOrWhiteSpace(req.ApiKey) || req.Adapter is not null)
        {
            await SyncDriverCapabilitiesFromProbeAsync(agent, HttpContext?.RequestAborted ?? CancellationToken.None);
        }

        var sessionCount = await db.AgentSessions.CountAsync(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId);
        var personas = await GetDriverPersonasSummaryAsync(normalizedId);
        var dto = ToDto(agent, sessionCount, personas);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("AgentUpdated", dto);
        }

        return Ok(DriverCapabilities.Parse(agent.CapabilitiesJson));
    }

    [HttpPost("{id}/context-limit")]
    public async Task<IActionResult> RecordLearnedContextLimit(
        string id,
        [FromBody] RecordLearnedContextLimitRequest req,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Model) || req.MaxContextTokens <= 0)
        {
            return BadRequest(new { error = "Model and positive MaxContextTokens are required." });
        }

        var normalizedId = id.Trim().TrimStart('@').ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId, ct);
        if (agent == null)
        {
            return NotFound(new { error = $"Driver '{id}' not found." });
        }

        var modelKey = req.Model.Trim();
        ModelContextLimits.RecordLearnedLimit(modelKey, req.MaxContextTokens);

        var patch = new JsonObject
        {
            ["learnedContextLimits"] = new JsonObject
            {
                [modelKey] = req.MaxContextTokens
            }
        };

        agent.CapabilitiesJson = DriverCapabilities.Merge(agent.CapabilitiesJson, patch.ToJsonString())
            ?? agent.CapabilitiesJson;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        CapabilityProbe.InvalidateCache();

        return Ok(DriverCapabilities.Parse(agent.CapabilitiesJson));
    }

    [HttpPost("{id}/verify")]
    public async Task<IActionResult> VerifyDriver(string id, CancellationToken ct = default)
    {
        var normalizedId = id.Trim().TrimStart('@').ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId, ct);
        if (agent == null)
        {
            return NotFound(new { error = $"Driver '{id}' not found." });
        }

        var view = DriverCapabilities.Parse(agent.CapabilitiesJson);
        var adapter = ResolveAdapterType(agent, view);
        var steps = new List<PersonaDriverVerificationStep>();

        if (adapter == DriverAdapters.OpenAi)
        {
            steps.Add(new PersonaDriverVerificationStep(
                "Driver Adapter Check",
                "success",
                $"OpenAI-compatible adapter configured ({view.Endpoint ?? "no endpoint set"})"));

            if (view.HasApiKey || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Authentication Check",
                    "success",
                    "API key is configured on driver"));
            }
            else if (!string.IsNullOrWhiteSpace(view.Endpoint) &&
                     (view.Endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                      view.Endpoint.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)))
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Authentication Check",
                    "success",
                    "Local endpoint configured (no API key required)"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Authentication Check",
                    "warning",
                    "No API key stored for this driver. Configure an API key if required by the server."));
            }

            var probeReading = await SyncDriverCapabilitiesFromProbeAsync(agent, ct);
            if (probeReading != null && probeReading.Models.Count > 0)
            {
                var sample = string.Join(", ", probeReading.Models.Take(5).Select(m => m.Id));
                steps.Add(new PersonaDriverVerificationStep(
                    "Endpoint & Model Discovery",
                    "success",
                    $"Discovered {probeReading.Models.Count} model(s): {sample}"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Endpoint & Model Discovery",
                    "failed",
                    probeReading?.UnavailableReason ?? "Failed to discover models from endpoint."));
            }

            var oaStatus = steps.Any(s => s.Status == "failed") ? "failed" :
                           steps.Any(s => s.Status == "warning") ? "warning" : "passed";
            return Ok(new PersonaDriverVerificationResult(normalizedId, normalizedId, oaStatus, steps));
        }

        var cliBin = adapter switch
        {
            DriverAdapters.Claude => "claude",
            DriverAdapters.Copilot => "gh",
            _ => "agy"
        };

        var hostBinPath = FindHostBinary(cliBin);
        if (hostBinPath != null)
        {
            var (verExit, verOut, verErr) = await RunHostCommandAsync(hostBinPath, "--version", ct);
            if (verExit == 0)
            {
                var verLine = !string.IsNullOrWhiteSpace(verOut) ? verOut.Split('\n')[0].Trim() : "available";
                steps.Add(new PersonaDriverVerificationStep(
                    "CLI Binary Check",
                    "success",
                    $"{cliBin} installed on API host ({verLine})"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "CLI Binary Check",
                    "failed",
                    $"{cliBin} check failed (exit {verExit}): {verErr}"));
            }

            if (adapter == DriverAdapters.Agy)
            {
                await EnsureHostAgyTokenFromDriverAsync(agent, ct);
                var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var hostAgyToken = Path.Combine(userHome, ".gemini", "antigravity-cli", "antigravity-oauth-token");
                if (System.IO.File.Exists(hostAgyToken))
                {
                    var rawTok = (await System.IO.File.ReadAllTextAsync(hostAgyToken, ct)).Trim();
                    if (!string.IsNullOrWhiteSpace(rawTok))
                    {
                        await SaveDriverOAuthTokenAsync(agent, rawTok, ct);
                        steps.Add(new PersonaDriverVerificationStep(
                            "Authentication Check",
                            "success",
                            "Google AI Pro OAuth token active on API host"));
                    }
                    else
                    {
                        steps.Add(new PersonaDriverVerificationStep(
                            "Authentication Check",
                            "warning",
                            "OAuth token file is empty. Click 'Authenticate Driver' to sign in."));
                    }
                }
                else
                {
                    steps.Add(new PersonaDriverVerificationStep(
                        "Authentication Check",
                        "warning",
                        "Not authenticated with Google AI Pro. Click 'Authenticate Driver' to sign in."));
                }
            }
            else if (!string.IsNullOrWhiteSpace(view.AuthCheckCommand))
            {
                var (chkExit, chkOut, chkErr) = await RunHostShellAsync(view.AuthCheckCommand, ct);
                if (chkExit == 0)
                {
                    steps.Add(new PersonaDriverVerificationStep(
                        "Authentication Check",
                        "success",
                        $"Authenticated ({(!string.IsNullOrWhiteSpace(chkOut) ? chkOut.Split('\n')[0].Trim() : "ok")})"));
                }
                else
                {
                    steps.Add(new PersonaDriverVerificationStep(
                        "Authentication Check",
                        "warning",
                        $"Auth check exited with {chkExit}: {chkErr}"));
                }
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Authentication Check",
                    "success",
                    $"{adapter} CLI ready on API host"));
            }

            var reading = await SyncDriverCapabilitiesFromProbeAsync(agent, ct);
            if (reading != null && reading.Models.Count > 0)
            {
                var sample = string.Join(", ", reading.Models.Take(5).Select(m => m.Id));
                steps.Add(new PersonaDriverVerificationStep(
                    "Model Catalog Discovery",
                    "success",
                    $"Discovered {reading.Models.Count} model(s): {sample}"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Model Catalog Discovery",
                    "warning",
                    reading?.UnavailableReason ?? "No models returned by probe."));
            }

            var status = steps.Any(s => s.Status == "failed") ? "failed" :
                         steps.Any(s => s.Status == "warning") ? "warning" : "passed";
            return Ok(new PersonaDriverVerificationResult(normalizedId, normalizedId, status, steps));
        }

        // Fallback: if CLI binary is not on the API host, check via bound persona container
        var targetPersonaId = await ResolveTargetPersonaForDriverAsync(normalizedId, ct);
        if (containerManager != null && targetPersonaId != null)
        {
            var containerResult = await containerManager.VerifyDriverAsync(targetPersonaId, ct);
            await SyncDriverCapabilitiesFromContainerOrProbeAsync(agent, targetPersonaId, adapter, ct);
            return Ok(containerResult with { PersonaId = normalizedId, DriverId = normalizedId });
        }

        steps.Add(new PersonaDriverVerificationStep(
            "CLI Binary Check",
            "failed",
            $"The {cliBin} CLI is not installed on the API host and no container runtime was available."));
        return Ok(new PersonaDriverVerificationResult(normalizedId, normalizedId, "failed", steps));
    }

    [HttpPost("{id}/auth/start")]
    public async Task<IActionResult> StartDriverAuth(string id, CancellationToken ct = default)
    {
        var normalizedId = id.Trim().TrimStart('@').ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId, ct);
        if (agent == null)
        {
            return NotFound(new { error = $"Driver '{id}' not found." });
        }

        var view = DriverCapabilities.Parse(agent.CapabilitiesJson);
        var adapter = ResolveAdapterType(agent, view);
        var loginCmd = !string.IsNullOrWhiteSpace(view.LoginCommand) ? view.LoginCommand : view.Sidecar?.LoginCommand;

        if (adapter == DriverAdapters.OpenAi && string.IsNullOrWhiteSpace(loginCmd))
        {
            if (view.HasApiKey)
            {
                return Ok(new PersonaAuthStartResult(normalizedId, normalizedId, "already_authenticated", Instructions: "OpenAI API key is already configured on this driver."));
            }
            return Ok(new PersonaAuthStartResult(normalizedId, normalizedId, "apikey_required", Instructions: "OpenAI-compatible drivers authenticate via API key. Enter your API key below."));
        }

        var cliBin = adapter switch
        {
            DriverAdapters.Claude => "claude",
            DriverAdapters.Copilot => "gh",
            _ => "agy"
        };

        var hostBinPath = FindHostBinary(cliBin);
        if (hostBinPath != null || !string.IsNullOrWhiteSpace(loginCmd))
        {
            if (_activeHostAuthSessions.TryGetValue(normalizedId, out var existing) && !existing.Process.HasExited)
            {
                if (!string.IsNullOrWhiteSpace(existing.AuthUrl))
                {
                    return Ok(new PersonaAuthStartResult(
                        normalizedId,
                        normalizedId,
                        "waiting_code",
                        existing.AuthUrl,
                        "Click the authorization link to sign in, then paste the authorization code below."));
                }
                _activeHostAuthSessions.TryRemove(normalizedId, out _);
                await existing.DisposeAsync();
            }

            var psi = new ProcessStartInfo
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!string.IsNullOrWhiteSpace(loginCmd))
            {
                psi.FileName = "/bin/bash";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(loginCmd);
            }
            else if (adapter == DriverAdapters.Agy)
            {
                psi.FileName = hostBinPath!;
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add("auth");
            }
            else if (adapter == DriverAdapters.Claude)
            {
                psi.FileName = hostBinPath!;
                psi.ArgumentList.Add("login");
            }
            else if (adapter == DriverAdapters.Copilot)
            {
                psi.FileName = hostBinPath!;
                psi.ArgumentList.Add("auth");
                psi.ArgumentList.Add("login");
                psi.ArgumentList.Add("-w");
            }
            else
            {
                return Ok(new PersonaAuthStartResult(normalizedId, normalizedId, "apikey_required", Instructions: "Enter your API key below or configure a Login Command."));
            }

            var proc = new Process { StartInfo = psi };
            proc.Start();

            var tcsUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readerTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                    {
                        var match = Regex.Match(line, @"https?://[^\s""'>]+");
                        if (match.Success)
                        {
                            tcsUrl.TrySetResult(match.Value);
                        }
                    }
                }
                catch (Exception ex)
                {
                    tcsUrl.TrySetException(ex);
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    {
                        var match = Regex.Match(line, @"https?://[^\s""'>]+");
                        if (match.Success)
                        {
                            tcsUrl.TrySetResult(match.Value);
                        }
                    }
                }
                catch { }
            });

            var completed = await Task.WhenAny(tcsUrl.Task, Task.Delay(10000, ct));
            if (completed == tcsUrl.Task && tcsUrl.Task.IsCompletedSuccessfully)
            {
                var authUrl = await tcsUrl.Task;
                _activeHostAuthSessions[normalizedId] = new HostAuthSession(proc, proc.StandardInput, readerTask, authUrl);
                return Ok(new PersonaAuthStartResult(
                    normalizedId,
                    normalizedId,
                    "waiting_code",
                    authUrl,
                    "Click the authorization link to sign in, then paste the authorization code below."));
            }

            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            return Ok(new PersonaAuthStartResult(normalizedId, normalizedId, "error", ErrorMessage: "Could not capture authorization link from CLI login command."));
        }

        var targetPersonaId = await ResolveTargetPersonaForDriverAsync(normalizedId, ct);
        if (containerManager != null && targetPersonaId != null)
        {
            var res = await containerManager.StartAuthAsync(targetPersonaId, ct);
            return Ok(res with { PersonaId = normalizedId, DriverId = normalizedId });
        }

        return Ok(new PersonaAuthStartResult(normalizedId, normalizedId, "error", ErrorMessage: $"CLI binary '{cliBin}' is not installed on the API host."));
    }

    [HttpPost("{id}/auth/submit")]
    public async Task<IActionResult> SubmitDriverAuthCode(
        string id,
        [FromBody] PersonasController.SubmitAuthCodeRequest req,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Code))
        {
            return BadRequest(new { error = "Authorization code is required." });
        }

        var normalizedId = id.Trim().TrimStart('@').ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId, ct);
        if (agent == null)
        {
            return NotFound(new { error = $"Driver '{id}' not found." });
        }

        var view = DriverCapabilities.Parse(agent.CapabilitiesJson);
        var adapter = ResolveAdapterType(agent, view);

        if (_activeHostAuthSessions.TryRemove(normalizedId, out var session))
        {
            try
            {
                await session.Writer.WriteLineAsync(req.Code.Trim());
                await session.Writer.FlushAsync(ct);

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                try
                {
                    await session.Process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException) { }

                var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var hostAgyToken = Path.Combine(userHome, ".gemini", "antigravity-cli", "antigravity-oauth-token");
                if (System.IO.File.Exists(hostAgyToken))
                {
                    var rawTok = (await System.IO.File.ReadAllTextAsync(hostAgyToken, ct)).Trim();
                    if (!string.IsNullOrWhiteSpace(rawTok))
                    {
                        await SaveDriverOAuthTokenAsync(agent, rawTok, ct);
                        await SeedDriverCredentialsToBoundPersonasAsync(normalizedId, ct);
                        await SyncDriverCapabilitiesFromProbeAsync(agent, ct);
                        return Ok(new PersonaAuthSubmitResult(normalizedId, "authenticated", "Successfully authenticated driver and refreshed model catalog!"));
                    }
                }

                if (session.Process.HasExited && session.Process.ExitCode == 0)
                {
                    await SyncDriverCapabilitiesFromProbeAsync(agent, ct);
                    return Ok(new PersonaAuthSubmitResult(normalizedId, "authenticated", "Driver authentication completed successfully!"));
                }

                return Ok(new PersonaAuthSubmitResult(normalizedId, "failed", ErrorMessage: "Authorization code was submitted, but authentication could not be verified."));
            }
            catch (Exception ex)
            {
                return Ok(new PersonaAuthSubmitResult(normalizedId, "failed", ErrorMessage: ex.Message));
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        var targetPersonaId = await ResolveTargetPersonaForDriverAsync(normalizedId, ct);
        if (containerManager != null && targetPersonaId != null)
        {
            var res = await containerManager.SubmitAuthCodeAsync(targetPersonaId, req.Code, ct);
            if (res.Status == "authenticated")
            {
                await SyncDriverCapabilitiesFromContainerOrProbeAsync(agent, targetPersonaId, adapter, ct);
            }
            return Ok(res with { PersonaId = normalizedId });
        }

        return Ok(new PersonaAuthSubmitResult(normalizedId, "failed", ErrorMessage: "No active authentication session found for this driver. Click 'Authenticate Driver' first."));
    }

    [HttpPost("{id}/auth/apikey")]
    public async Task<IActionResult> ConfigureDriverApiKey(
        string id,
        [FromBody] PersonasController.ConfigureApiKeyRequest req,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.ApiKey))
        {
            return BadRequest(new { error = "API key is required." });
        }

        var normalizedId = id.Trim().TrimStart('@').ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId, ct);
        if (agent == null)
        {
            return NotFound(new { error = $"Driver '{id}' not found." });
        }

        var patch = new JsonObject
        {
            ["apiKey"] = req.ApiKey.Trim()
        };

        agent.CapabilitiesJson = DriverCapabilities.Merge(agent.CapabilitiesJson, patch.ToJsonString())
            ?? agent.CapabilitiesJson;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (containerManager != null)
        {
            var boundPersonas = await db.Personas.AsNoTracking().Where(p => p.DriverId == normalizedId).Select(p => p.Id).ToListAsync(ct);
            foreach (var pId in boundPersonas)
            {
                try
                {
                    var st = await containerManager.GetStatusAsync(pId, ct);
                    if (st.Status == "running")
                    {
                        await containerManager.ConfigureApiKeyAsync(pId, req.ApiKey.Trim(), req.Provider ?? normalizedId, ct);
                    }
                }
                catch { }
            }
        }

        await SyncDriverCapabilitiesFromProbeAsync(agent, ct);

        return Ok(new PersonaAuthSubmitResult(normalizedId, "authenticated", "API key saved to driver configuration and model catalog refreshed!"));
    }

    internal async Task<DriverCapabilityReading?> SyncDriverCapabilitiesFromProbeAsync(Agent agent, CancellationToken ct)
    {
        CapabilityProbe.InvalidateCache();
        var view = DriverCapabilities.Parse(agent.CapabilitiesJson);
        var probeReq = view.ToProbeRequest(agent.Id, agent.RuntimeType, forceRefresh: true, rawCapabilitiesJson: agent.CapabilitiesJson);
        try
        {
            var reading = await CapabilityProbe.ProbeAsync(probeReq, ct);
            if (reading != null && reading.Models.Count > 0)
            {
                var declaration = new DriverCapabilityDeclaration
                {
                    Adapter = reading.Adapter,
                    Endpoint = DriverModelDiscovery.NormalizeEndpoint(view.Endpoint),
                    ObservedAt = DateTimeOffset.UtcNow,
                    Models = reading.Models.Select(m => new DriverCapabilityDeclaration.DeclaredModel
                    {
                        Id = m.Id,
                        Name = m.Name,
                        Efforts = m.Efforts.ToList()
                    }).ToList()
                };

                var patch = new JsonObject
                {
                    ["declared"] = JsonSerializer.SerializeToNode(declaration)
                };

                agent.CapabilitiesJson = DriverCapabilities.Merge(agent.CapabilitiesJson, patch.ToJsonString())
                    ?? agent.CapabilitiesJson;
                agent.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                CapabilityProbe.InvalidateCache();
            }
            return reading;
        }
        catch
        {
            return null;
        }
    }

    private async Task SyncDriverCapabilitiesFromContainerOrProbeAsync(
        Agent agent,
        string personaId,
        string adapter,
        CancellationToken ct)
    {
        if (containerManager != null && adapter == DriverAdapters.Agy)
        {
            try
            {
                var (tokExit, tokOut, _) = await containerManager.ExecInContainerAsync(
                    personaId,
                    "cat /root/.gemini/antigravity-cli/antigravity-oauth-token",
                    ct);
                if (tokExit == 0 && !string.IsNullOrWhiteSpace(tokOut))
                {
                    await SaveDriverOAuthTokenAsync(agent, tokOut.Trim(), ct);
                }

                var (modExit, modOut, _) = await containerManager.ExecInContainerAsync(
                    personaId,
                    "timeout 15 agy models",
                    ct);
                if (modExit == 0 && !string.IsNullOrWhiteSpace(modOut))
                {
                    var parsed = DriverModelDiscovery.ParseAgyModels(modOut);
                    if (parsed.Count > 0)
                    {
                        var declaration = new DriverCapabilityDeclaration
                        {
                            Adapter = DriverAdapters.Agy,
                            ObservedAt = DateTimeOffset.UtcNow,
                            Models = parsed.Select(m => new DriverCapabilityDeclaration.DeclaredModel
                            {
                                Id = m.Id,
                                Name = m.Name,
                                Efforts = m.Efforts.ToList()
                            }).ToList()
                        };

                        var patch = new JsonObject
                        {
                            ["declared"] = JsonSerializer.SerializeToNode(declaration)
                        };
                        agent.CapabilitiesJson = DriverCapabilities.Merge(agent.CapabilitiesJson, patch.ToJsonString())
                            ?? agent.CapabilitiesJson;
                        agent.UpdatedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(ct);
                        CapabilityProbe.InvalidateCache();
                        return;
                    }
                }
            }
            catch { }
        }

        await SyncDriverCapabilitiesFromProbeAsync(agent, ct);
    }

    private async Task SaveDriverOAuthTokenAsync(Agent agent, string rawToken, CancellationToken ct)
    {
        try
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var tokensDir = Path.Combine(userHome, ".agentmesh", "tokens");
            Directory.CreateDirectory(tokensDir);
            await System.IO.File.WriteAllTextAsync(Path.Combine(tokensDir, $"driver-{agent.Id}.oauth"), rawToken, ct);

            var hostAgyToken = Path.Combine(userHome, ".gemini", "antigravity-cli", "antigravity-oauth-token");
            var hostDir = Path.GetDirectoryName(hostAgyToken);
            if (!string.IsNullOrEmpty(hostDir)) Directory.CreateDirectory(hostDir);
            await System.IO.File.WriteAllTextAsync(hostAgyToken, rawToken, ct);
        }
        catch { }

        var patch = new JsonObject
        {
            ["oauthToken"] = rawToken
        };
        agent.CapabilitiesJson = DriverCapabilities.Merge(agent.CapabilitiesJson, patch.ToJsonString())
            ?? agent.CapabilitiesJson;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureHostAgyTokenFromDriverAsync(Agent agent, CancellationToken ct)
    {
        try
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var hostAgyToken = Path.Combine(userHome, ".gemini", "antigravity-cli", "antigravity-oauth-token");
            if (System.IO.File.Exists(hostAgyToken)) return;

            if (!string.IsNullOrWhiteSpace(agent.CapabilitiesJson))
            {
                using var doc = JsonDocument.Parse(agent.CapabilitiesJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("oauthToken", out var tokProp) &&
                    tokProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(tokProp.GetString()))
                {
                    var dir = Path.GetDirectoryName(hostAgyToken);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    await System.IO.File.WriteAllTextAsync(hostAgyToken, tokProp.GetString()!.Trim(), ct);
                }
            }
        }
        catch { }
    }

    private async Task SeedDriverCredentialsToBoundPersonasAsync(string driverId, CancellationToken ct)
    {
        if (containerManager == null) return;
        try
        {
            var personas = await db.Personas.AsNoTracking().Where(p => p.DriverId == driverId).Select(p => p.Id).ToListAsync(ct);
            foreach (var pId in personas)
            {
                var st = await containerManager.GetStatusAsync(pId, ct);
                if (st.Status == "running")
                {
                    await containerManager.EnsureRunningAsync(pId, ct);
                }
            }
        }
        catch { }
    }

    private async Task<string?> ResolveTargetPersonaForDriverAsync(string driverId, CancellationToken ct)
    {
        var persona = await db.Personas.AsNoTracking()
            .Where(p => p.DriverId == driverId)
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(ct);
        return persona?.Id;
    }

    private static string ResolveAdapterType(Agent agent, DriverCapabilitiesView view)
    {
        var canonical = DriverAdapters.Canonicalize(view.Adapter);
        if (!string.IsNullOrEmpty(canonical)) return canonical;
        if (!string.IsNullOrWhiteSpace(view.Endpoint)) return DriverAdapters.OpenAi;
        if (agent.Id.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return DriverAdapters.Claude;
        if (agent.Id.StartsWith("copilot", StringComparison.OrdinalIgnoreCase)) return DriverAdapters.Copilot;
        if (agent.Id.StartsWith("openai", StringComparison.OrdinalIgnoreCase)) return DriverAdapters.OpenAi;
        return agent.RuntimeType switch
        {
            RuntimeType.OpenAiCompatible or RuntimeType.LocalOllama => DriverAdapters.OpenAi,
            RuntimeType.CliAgy => DriverAdapters.Agy,
            _ => DriverAdapters.Agy
        };
    }

    private static string? FindHostBinary(string name)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extraCandidates = new[]
        {
            Path.Combine(userHome, ".local", "bin", name),
            Path.Combine(userHome, ".gemini", "antigravity-cli", "bin", name),
            $"/usr/local/bin/{name}",
            $"/usr/bin/{name}"
        };

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathDirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name));

        foreach (var candidate in pathDirs.Concat(extraCandidates))
        {
            if (System.IO.File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunHostCommandAsync(
        string binary,
        string arguments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, (await outTask).Trim(), (await errTask).Trim());
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunHostShellAsync(
        string command,
        CancellationToken ct)
    {
        var escaped = command.Replace("\"", "\\\"");
        return RunHostCommandAsync("/bin/bash", $"-c \"{escaped}\"", ct);
    }

    private sealed class HostAuthSession(Process process, StreamWriter writer, Task readerTask, string authUrl) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        public StreamWriter Writer { get; } = writer;
        public Task ReaderTask { get; } = readerTask;
        public string AuthUrl { get; } = authUrl;

        public async ValueTask DisposeAsync()
        {
            try { if (!Process.HasExited) Process.Kill(true); } catch { }
            try { Writer.Dispose(); } catch { }
            try { Process.Dispose(); } catch { }
            await ValueTask.CompletedTask;
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAgent(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (agent == null)
        {
            return NotFound(new { error = $"Agent '{id}' not found." });
        }

        var boundPersonas = await db.Personas.Where(p => p.DriverId == normalizedId).ToListAsync();
        if (boundPersonas.Count > 0)
        {
            var names = string.Join(", ", boundPersonas.Select(p => $"@{p.Id} ({p.Name})"));
            return BadRequest(new { error = $"Cannot delete driver '{normalizedId}' because it is assigned to {boundPersonas.Count} persona(s): {names}. Reassign those personas to another driver first." });
        }

        var agentJobs = await db.AgentJobs.Where(j => j.RequestedByAgentId == normalizedId).ToListAsync();
        if (agentJobs.Count > 0)
        {
            db.AgentJobs.RemoveRange(agentJobs);
        }

        var sessions = await db.AgentSessions.Where(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId).ToListAsync();
        foreach (var session in sessions)
        {
            if (session.DriverId == normalizedId) session.DriverId = null;
            if (session.CreatorAgentId == normalizedId) session.CreatorAgentId = null;
        }

        db.Agents.Remove(agent);
        await db.SaveChangesAsync();

        AgentMeshHub.UnregisterAgent(normalizedId);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("AgentDeleted", new { id = normalizedId });
        }

        return NoContent();
    }

    [HttpGet("{id}/personas")]
    [AllowAnonymous]
    public async Task<IActionResult> GetDriverPersonas(string id)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var driver = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (driver == null)
        {
            return NotFound(new { error = $"Driver '{id}' not found." });
        }

        var personas = await db.Personas
            .AsNoTracking()
            .Where(p => p.DriverId == normalizedId)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var sessionCounts = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.PersonaId != null)
            .GroupBy(s => s.PersonaId!)
            .Select(g => new { PersonaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PersonaId, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var dtos = personas
            .Select(p => PersonasController.ToDto(p, driver, sessionCounts.GetValueOrDefault(p.Id, 0)))
            .ToList();

        return Ok(dtos);
    }

    [HttpGet("{id}/sessions")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgentSessions(string id, [FromQuery] int? take = null, [FromQuery] int? skip = null)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var driver = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == normalizedId);
        var personas = await db.Personas
            .AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);

        IQueryable<AgentSession> filtered = db.AgentSessions
            .AsNoTracking()
            .Where(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId);

        // Mirrors the persona card contract: an exact owner-scoped total (the global /api/sessions
        // window is capped at 100 rows) plus an opt-in page. The CLI polls this un-paged.
        // Counted off the unordered queryable so the aggregate carries no sort node.
        var totalCount = await filtered.CountAsync();

        IQueryable<AgentSession> sessionQuery = filtered.OrderByDescending(s => s.UpdatedAt);
        // ControllerContext.HttpContext is null for direct (non-HTTP) action invocation.
        if (ControllerContext.HttpContext is { } httpContext)
        {
            httpContext.Response.Headers["X-Total-Count"] = totalCount.ToString();
        }

        if (take is > 0)
        {
            sessionQuery = sessionQuery.Skip(Math.Max(0, skip ?? 0)).Take(take.Value);
        }

        var rawSessions = await sessionQuery
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
                DriverName: driver?.Name ?? dId,
                PersonaId: s.PersonaId,
                PersonaName: s.PersonaId != null ? personas.GetValueOrDefault(s.PersonaId, s.PersonaId) : null
            );
        }).ToList();

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

        Persona? persona = null;
        if (!string.IsNullOrWhiteSpace(req.PersonaId))
        {
            var normPersonaId = req.PersonaId.Trim().ToLowerInvariant();
            persona = await db.Personas.FirstOrDefaultAsync(p => p.Id == normPersonaId);
        }

        var effectiveWorkDir = !string.IsNullOrWhiteSpace(req.WorkingDirectory) ? req.WorkingDirectory : null;
        var effectiveModel = !string.IsNullOrWhiteSpace(req.Model) ? req.Model : persona?.DefaultModel;
        var effectiveEffort = req.Effort != null ? req.Effort : persona?.DefaultEffort;
        var effectiveMode = !string.IsNullOrWhiteSpace(req.Mode) ? req.Mode : persona?.DefaultMode;
        var effectiveTier = !string.IsNullOrWhiteSpace(req.Tier) ? req.Tier : persona?.DefaultTier;
        var effectiveContextSize = req.ContextSize != null ? req.ContextSize : persona?.DefaultContextSize;

        var hasAttachments = req.Attachments is { Count: > 0 };
        var hasInitialPrompt = !string.IsNullOrWhiteSpace(req.InitialPrompt) || hasAttachments;
        var initialMetadata = SessionsController.MergeSessionMetadata(
            req.MetadataJson,
            effectiveWorkDir,
            effectiveModel,
            effectiveEffort,
            effectiveMode,
            effectiveTier,
            persona?.Id,
            persona?.Name,
            persona?.Role,
            persona?.PersonaPrompt,
            effectiveContextSize);

        var session = new AgentSession
        {
            Title = req.Title.Trim(),
            Status = hasInitialPrompt ? "thinking" : "active",
            MetadataJson = initialMetadata,
            PersonaId = persona?.Id,
            DriverId = normalizedId,
            CreatorAgentId = normalizedId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.AgentSessions.Add(session);

        SessionMessage? promptMsg = null;
        if (hasInitialPrompt)
        {
            var effectivePrompt = !string.IsNullOrWhiteSpace(req.InitialPrompt)
                ? req.InitialPrompt.Trim()
                : SessionsController.DefaultAttachmentPrompt;
            promptMsg = new SessionMessage
            {
                SessionId = session.Id,
                SenderAgentId = "operator",
                TargetAgentId = normalizedId,
                EventType = "prompt",
                Content = effectivePrompt,
                StructuredPayloadJson = SessionsController.MergeAttachmentsIntoStructuredPayload(null, req.Attachments),
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
                session.DriverId,
                hasInitialPrompt ? 1 : 0,
                session.CreatedAt,
                session.UpdatedAt,
                DriverId: session.DriverId,
                DriverName: agent.Name,
                PersonaId: persona?.Id,
                PersonaName: persona?.Name
            ));

            if (promptMsg != null)
            {
                var (workDir, model, effort, mode, tier) = SessionsController.ExtractSessionSettings(session.MetadataJson);
                var contextSize = SessionsController.ExtractSessionContextSize(session.MetadataJson);
                await hubContext.Clients.Group($"agent-{normalizedId}").SendAsync("PromptReceived", new
                {
                    sessionId = session.Id,
                    messageId = promptMsg.Id,
                    agentId = normalizedId,
                    driverId = normalizedId,
                    personaId = persona?.Id,
                    personaName = persona?.Name,
                    personaRole = persona?.Role,
                    personaPrompt = persona?.PersonaPrompt,
                    prompt = promptMsg.Content,
                    sender = promptMsg.SenderAgentId,
                    workingDirectory = workDir,
                    model = model,
                    effort = effort,
                    mode = mode,
                    tier = tier,
                    contextSize = contextSize,
                    attachments = req.Attachments,
                    createdAt = promptMsg.CreatedAt
                });
            }
        }

        return CreatedAtAction("GetSession", "Sessions", new { id = session.Id }, session);
    }

    private async Task<List<DriverPersonaSummaryDto>> GetDriverPersonasSummaryAsync(string driverId)
    {
        return await db.Personas
            .AsNoTracking()
            .Where(p => p.DriverId == driverId)
            .OrderBy(p => p.Name)
            .Select(p => new DriverPersonaSummaryDto(p.Id, p.Name, p.Role, p.DefaultModel))
            .ToListAsync();
    }

    [HttpGet("{id}/usage")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAgentUsage(string id, [FromQuery] bool refresh = false)
    {
        var normalizedId = id.Trim().ToLowerInvariant();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == normalizedId);
        if (agent == null)
        {
            return NotFound(new { error = $"Agent '{normalizedId}' not found." });
        }

        var sessions = await db.AgentSessions
            .Where(s => s.DriverId == normalizedId || s.CreatorAgentId == normalizedId)
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

        var providerQuota = await GetAgentProviderQuotaAsync(agent, refresh);

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

    private async Task<ProviderQuotaDto?> GetAgentProviderQuotaAsync(Agent agent, bool refresh)
    {
        var caps = DriverCapabilities.Parse(agent.CapabilitiesJson);
        var request = new DriverQuotaProbeRequest(
            DriverId: agent.Id,
            RuntimeType: agent.RuntimeType,
            Adapter: caps?.Adapter,
            Endpoint: caps?.Endpoint,
            // `refresh=true` is an explicit user action, so it bypasses the probe's TTL cache.
            ForceRefresh: refresh);

        var reading = await QuotaProbe.ProbeAsync(request, HttpContext?.RequestAborted ?? CancellationToken.None);
        return reading == null ? null : ToDto(reading);

        static ProviderQuotaDto ToDto(DriverQuotaReading reading) => new(
            Provider: reading.Provider,
            PlanName: reading.PlanName,
            PrimaryRemainingFraction: reading.PrimaryRemainingFraction,
            Groups: reading.Groups
                .Select(g => new QuotaGroupDto(
                    g.Name,
                    g.Description,
                    g.Buckets
                        .Select(b => new QuotaBucketDto(
                            b.Id,
                            b.Name,
                            b.Description,
                            b.Window,
                            b.RemainingFraction,
                            b.ResetsAt?.ToString("u")))
                        .ToList()))
                .ToList(),
            Source: reading.Source,
            ObservedAt: reading.ObservedAt);
    }


    private static AgentDto ToDto(Agent a, int sessionCount, List<DriverPersonaSummaryDto>? personas = null)
    {
        var isConnected = AgentMeshHub.IsAgentConnected(a.Id);
        var status = isConnected
            ? (a.Status == "busy" ? "busy" : "online")
            : (a.Status == "busy" && a.LastHeartbeatAt > DateTimeOffset.UtcNow.AddMinutes(-5)
                ? "busy"
                : (a.LastHeartbeatAt > DateTimeOffset.UtcNow.AddSeconds(-60)
                    ? a.Status
                    : "offline"));

        return new(
            a.Id,
            a.Name,
            a.RuntimeType.ToString(),
            (int)a.TrustTier,
            a.PersonaPrompt,
            a.CreatedAt,
            a.Role,
            status,
            a.CapabilitiesJson,
            a.LastHeartbeatAt,
            sessionCount,
            personas ?? new List<DriverPersonaSummaryDto>(),
            DriverCapabilities.Parse(a.CapabilitiesJson)
        );
    }
}

public record RecordLearnedContextLimitRequest(
    string Model,
    int MaxContextTokens
);

public record DriverPersonaSummaryDto(
    string Id,
    string Name,
    string Role,
    string? DefaultModel
);

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

/// <summary>
/// Patch-style driver config update. Null fields are left untouched; a blank ApiKey means
/// "keep the stored one" rather than "clear it".
/// </summary>
public record UpdateDriverConfigRequest(
    string? Adapter = null,
    string? Endpoint = null,
    string? ApiKey = null,
    string? DefaultModel = null,
    int? TimeoutSeconds = null,
    int? MaxRetries = null,
    int? RetryDelayMilliseconds = null,
    int? MaxSteps = null,
    List<string>? Capabilities = null,
    DriverSidecarConfig? Sidecar = null,
    string? LoginCommand = null,
    string? AuthCheckCommand = null
);

public record CreateAgentSessionRequest(
    string Title,
    string? InitialPrompt = null,
    string? MetadataJson = null,
    string? WorkingDirectory = null,
    string? Model = null,
    string? Effort = null,
    string? Mode = null,
    string? Tier = null,
    string? PersonaId = null,
    List<SessionAttachmentDto>? Attachments = null,
    string? ContextSize = null
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
    int SessionCount = 0,
    List<DriverPersonaSummaryDto>? Personas = null,
    /// <summary>Parsed runtime limits. Safe to render: never contains the API key.</summary>
    DriverCapabilitiesView? Config = null
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
    List<QuotaGroupDto> Groups,
    /// <summary>Which introspection channel produced this reading, e.g. "agy:/usage".</summary>
    string? Source = null,
    /// <summary>When the reading was taken, so the UI can age it instead of implying liveness.</summary>
    DateTimeOffset? ObservedAt = null
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
