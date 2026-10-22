using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentMesh.Cli.Models;

namespace AgentMesh.Cli.Services;

public class MeshClient
{
    private readonly HttpClient _http;
    private readonly CliConfig _config;
    private readonly ConfigStore _configStore;

    public MeshClient(CliConfig config, ConfigStore configStore, HttpClient? httpClient = null)
    {
        _config = config;
        _configStore = configStore;
        _http = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri(_config.MeshUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "api/status", _config.MasterToken);
            using var res = await _http.SendAsync(req, ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<RemoteAgentDto>> GetMeshAgentsAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "api/agents", _config.MasterToken);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<List<RemoteAgentDto>>(cancellationToken: ct) ?? new();
    }

    public async Task<RemoteAgentDto?> GetMeshAgentAsync(string agentId, CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, $"api/agents/{Uri.EscapeDataString(agentId.Trim().ToLowerInvariant())}", _config.MasterToken);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadFromJsonAsync<RemoteAgentDto>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }

    public static string SerializePresetCapabilities(AgentPresetConfig preset)
    {
        if (string.Equals(preset.AdapterType, "openai", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(preset.Endpoint))
        {
            var caps = new Dictionary<string, object?>
            {
                ["adapter"] = "openai",
                ["endpoint"] = preset.Endpoint,
                ["apiKey"] = preset.ApiKey,
                ["defaultModel"] = preset.DefaultModel,
                ["timeoutSeconds"] = preset.TimeoutSeconds,
                ["capabilities"] = preset.Capabilities
            };
            return JsonSerializer.Serialize(caps);
        }

        return JsonSerializer.Serialize(preset.Capabilities);
    }

    public async Task<string> EnsureAgentRegisteredAsync(AgentPresetConfig preset, CancellationToken ct = default)
    {
        var capabilitiesJson = SerializePresetCapabilities(preset);

        var registerPayload = new
        {
            id = preset.Id,
            name = preset.Name,
            role = preset.Role,
            status = "online",
            capabilitiesJson
        };

        using var regReq = CreateRequest(HttpMethod.Post, "api/agents/register", _config.MasterToken);
        regReq.Content = JsonContent.Create(registerPayload);

        using var regRes = await _http.SendAsync(regReq, ct);
        if (regRes.IsSuccessStatusCode)
        {
            using var doc = await JsonDocument.ParseAsync(await regRes.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("token", out var tokProp))
            {
                var token = tokProp.GetString();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    preset.Token = token;
                    _configStore.Save(_config);
                    return token;
                }
            }
        }
        else if (regRes.StatusCode == HttpStatusCode.Conflict)
        {
            // Agent already exists; update its metadata and status via upsert
            using var upsertReq = CreateRequest(HttpMethod.Post, "api/agents", preset.Token ?? _config.MasterToken, preset.Id);
            upsertReq.Content = JsonContent.Create(registerPayload);
            using var upsertRes = await _http.SendAsync(upsertReq, ct);
            upsertRes.EnsureSuccessStatusCode();
        }
        else
        {
            regRes.EnsureSuccessStatusCode();
        }

        return preset.Token ?? _config.MasterToken;
    }

    public async Task SetAgentStatusAsync(AgentPresetConfig preset, string status, CancellationToken ct = default)
    {
        try
        {
            var capabilitiesJson = SerializePresetCapabilities(preset);
            using var req = CreateRequest(HttpMethod.Post, "api/agents", preset.Token ?? _config.MasterToken, preset.Id);
            req.Content = JsonContent.Create(new
            {
                id = preset.Id,
                name = preset.Name,
                role = preset.Role,
                status,
                capabilitiesJson
            });
            using var res = await _http.SendAsync(req, ct);
        }
        catch
        {
            // Ignore errors during shutdown status update
        }
    }

    public async Task PostTrajectoryEventAsync(
        Guid sessionId,
        string agentId,
        string? agentToken,
        TrajectoryEventPayload ev,
        CancellationToken ct = default)
    {
        var token = !string.IsNullOrWhiteSpace(agentToken) ? agentToken : _config.MasterToken;
        using var req = CreateRequest(HttpMethod.Post, $"api/sessions/{sessionId}/events", token, agentId);
        req.Content = JsonContent.Create(new
        {
            senderAgentId = agentId,
            eventType = ev.EventType,
            content = ev.Content,
            toolName = ev.ToolName,
            toolArgsJson = ev.ToolArgsJson,
            toolResultJson = ev.ToolResultJson,
            structuredPayloadJson = ev.StructuredPayloadJson,
            createdAt = ev.CreatedAt
        });

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<List<PromptReceivedEvent>> GetPendingPromptsForAgentAsync(string agentId, string? agentToken, CancellationToken ct = default)
    {
        var pending = new List<PromptReceivedEvent>();
        try
        {
            var token = !string.IsNullOrWhiteSpace(agentToken) ? agentToken : _config.MasterToken;
            using var sessionsReq = CreateRequest(HttpMethod.Get, $"api/agents/{Uri.EscapeDataString(agentId)}/sessions", token, agentId);
            using var sessionsRes = await _http.SendAsync(sessionsReq, ct);
            if (!sessionsRes.IsSuccessStatusCode) return pending;

            using var sessionsDoc = await JsonDocument.ParseAsync(await sessionsRes.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            foreach (var sElem in sessionsDoc.RootElement.EnumerateArray())
            {
                var status = sElem.TryGetProperty("status", out var stProp) ? stProp.GetString() : null;
                if (!string.Equals(status, "thinking", StringComparison.OrdinalIgnoreCase)) continue;

                if (!sElem.TryGetProperty("id", out var idProp) || !Guid.TryParse(idProp.GetString(), out var sessionId))
                    continue;

                string? workDir = null, model = null, effort = null, mode = null;
                string? personaId = null, personaName = null, personaRole = null, personaPrompt = null;
                string? jobId = null, projectId = null, gitBranch = null, workspaceType = null;
                if (sElem.TryGetProperty("personaId", out var topPid) && topPid.ValueKind == JsonValueKind.String)
                {
                    personaId = topPid.GetString();
                }
                if (sElem.TryGetProperty("personaName", out var topPname) && topPname.ValueKind == JsonValueKind.String)
                {
                    personaName = topPname.GetString();
                }
                if (sElem.TryGetProperty("metadataJson", out var metaProp) && !string.IsNullOrWhiteSpace(metaProp.GetString()))
                {
                    try
                    {
                        using var mDoc = JsonDocument.Parse(metaProp.GetString()!);
                        var mRoot = mDoc.RootElement;
                        if (mRoot.TryGetProperty("workingDirectory", out var wd)) workDir = wd.GetString();
                        if (mRoot.TryGetProperty("model", out var m)) model = m.GetString();
                        if (mRoot.TryGetProperty("effort", out var ef)) effort = ef.GetString();
                        if (mRoot.TryGetProperty("mode", out var md)) mode = md.GetString();
                        if (mRoot.TryGetProperty("personaId", out var pid)) personaId ??= pid.GetString();
                        if (mRoot.TryGetProperty("personaName", out var pn)) personaName ??= pn.GetString();
                        if (mRoot.TryGetProperty("personaRole", out var pr)) personaRole = pr.GetString();
                        if (mRoot.TryGetProperty("personaPrompt", out var pp)) personaPrompt = pp.GetString();
                        if (mRoot.TryGetProperty("jobId", out var jid)) jobId = jid.GetString();
                        if (mRoot.TryGetProperty("projectId", out var projid)) projectId = projid.GetString();
                        if (mRoot.TryGetProperty("gitBranch", out var gb)) gitBranch = gb.GetString();
                        if (mRoot.TryGetProperty("workspaceType", out var wt)) workspaceType = wt.GetString();
                    }
                    catch { }
                }

                using var eventsReq = CreateRequest(HttpMethod.Get, $"api/sessions/{sessionId}/events", token, agentId);
                using var eventsRes = await _http.SendAsync(eventsReq, ct);
                if (!eventsRes.IsSuccessStatusCode) continue;

                using var eventsDoc = await JsonDocument.ParseAsync(await eventsRes.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var arr = eventsDoc.RootElement;
                if (arr.GetArrayLength() == 0) continue;

                var lastEv = arr[arr.GetArrayLength() - 1];
                var evType = lastEv.TryGetProperty("eventType", out var etProp) ? etProp.GetString() : null;
                if (string.Equals(evType, "prompt", StringComparison.OrdinalIgnoreCase))
                {
                    var content = lastEv.TryGetProperty("content", out var cProp) ? cProp.GetString() ?? "" : "";
                    var sender = lastEv.TryGetProperty("senderAgentId", out var saProp) ? saProp.GetString() ?? "operator" : "operator";
                    var msgId = lastEv.TryGetProperty("id", out var mProp) && Guid.TryParse(mProp.GetString(), out var mid)
                        ? mid
                        : Guid.NewGuid();

                    var createdAt = lastEv.TryGetProperty("createdAt", out var caProp) && caProp.TryGetDateTimeOffset(out var dto)
                        ? dto
                        : DateTimeOffset.UtcNow;

                    List<AttachmentItem>? attachments = null;
                    if (lastEv.TryGetProperty("structuredPayloadJson", out var spProp) &&
                        spProp.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(spProp.GetString()))
                    {
                        try
                        {
                            using var spDoc = JsonDocument.Parse(spProp.GetString()!);
                            if (spDoc.RootElement.TryGetProperty("attachments", out var attElem) &&
                                attElem.ValueKind == JsonValueKind.Array)
                            {
                                attachments = JsonSerializer.Deserialize<List<AttachmentItem>>(attElem.GetRawText(), new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                });
                            }
                        }
                        catch { }
                    }

                    pending.Add(new PromptReceivedEvent
                    {
                        SessionId = sessionId,
                        MessageId = msgId,
                        AgentId = agentId,
                        DriverId = agentId,
                        PersonaId = personaId,
                        PersonaName = personaName,
                        PersonaRole = personaRole,
                        PersonaPrompt = personaPrompt,
                        Prompt = content,
                        Sender = sender,
                        WorkingDirectory = workDir,
                        Model = model,
                        Effort = effort,
                        Mode = mode,
                        JobId = jobId,
                        ProjectId = projectId,
                        GitBranch = gitBranch,
                        WorkspaceType = workspaceType,
                        Attachments = attachments,
                        CreatedAt = createdAt
                    });
                }
            }
        }
        catch
        {
            // Ignore transient lookup errors on startup
        }

        return pending;
    }

    public virtual async Task<bool> DownloadSessionAttachmentAsync(Guid sessionId, string attachmentId, string destinationFilePath, CancellationToken ct = default)
    {
        try
        {
            var token = _config.MasterToken;
            using var req = CreateRequest(HttpMethod.Get, $"api/sessions/{sessionId}/attachments/{Uri.EscapeDataString(attachmentId)}/content", token);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
            {
                return false;
            }

            var dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await using var netStream = await res.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await netStream.CopyToAsync(fileStream, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SyncSessionArtifactAsync(Guid sessionId, string name, string content, string? summary = null, string? contentType = "text/markdown", CancellationToken ct = default, string? base64Content = null)
    {
        try
        {
            var token = _config.MasterToken;
            using var req = CreateRequest(HttpMethod.Post, $"api/sessions/{sessionId}/artifacts", token);
            req.Content = JsonContent.Create(new
            {
                name,
                content,
                summary,
                contentType,
                base64Content
            });
            using var res = await _http.SendAsync(req, ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string relativeUrl, string token, string? agentId = null)
    {
        var req = new HttpRequestMessage(method, relativeUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            req.Headers.Add("X-Agent-Id", agentId);
        }
        return req;
    }
}
