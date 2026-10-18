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

    public async Task<string> EnsureAgentRegisteredAsync(AgentPresetConfig preset, CancellationToken ct = default)
    {
        var capabilitiesJson = JsonSerializer.Serialize(preset.Capabilities);

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
            var capabilitiesJson = JsonSerializer.Serialize(preset.Capabilities);
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
            structuredPayloadJson = ev.StructuredPayloadJson
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

                    pending.Add(new PromptReceivedEvent
                    {
                        SessionId = sessionId,
                        MessageId = msgId,
                        AgentId = agentId,
                        Prompt = content,
                        Sender = sender,
                        WorkingDirectory = workDir,
                        Model = model,
                        Effort = effort,
                        Mode = mode
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
