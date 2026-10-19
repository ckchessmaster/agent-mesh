using System.Collections.Concurrent;
using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace AgentMesh.Cli.Services;

public class AgentRunnerInstance : IAsyncDisposable
{
    private readonly AgentPresetConfig _preset;
    private readonly MeshClient _meshClient;
    private readonly string _meshUrl;
    private readonly Action<string, string> _onLog;
    private readonly Action _onStateChanged;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, byte> _processedMessages = new();
    private CancellationTokenSource? _activeTurnCts;

    private HubConnection? _hubConnection;
    private string? _agentToken;

    public AgentRuntimeState State { get; }

    public AgentRunnerInstance(
        AgentPresetConfig preset,
        MeshClient meshClient,
        string meshUrl,
        Action<string, string> onLog,
        Action onStateChanged)
    {
        _preset = preset;
        _meshClient = meshClient;
        _meshUrl = meshUrl.TrimEnd('/');
        _onLog = onLog;
        _onStateChanged = onStateChanged;

        State = new AgentRuntimeState
        {
            Id = preset.Id,
            Name = preset.Name,
            Role = preset.Role,
            AdapterType = preset.AdapterType,
            State = RunnerConnectionState.Offline
        };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        State.State = RunnerConnectionState.Connecting;
        State.LastError = null;
        _onStateChanged();
        _onLog(_preset.Id, $"Registering @{_preset.Id} ({_preset.Name}) with Mesh...");

        _agentToken = await _meshClient.EnsureAgentRegisteredAsync(_preset, ct);

        var hubUrl = $"{_meshUrl}/hubs/agent-mesh";
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_agentToken);
                options.Headers["X-Agent-Id"] = _preset.Id;
            })
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<PromptReceivedEvent>("PromptReceived", async (ev) =>
        {
            if (!string.Equals(ev.AgentId, _preset.Id, StringComparison.OrdinalIgnoreCase))
                return;

            await HandlePromptAsync(ev, _cts.Token);
        });

        // Also listen to ReceiveMessage so prompts are caught even if backend is running an older hub build
        _hubConnection.On<JsonElement>("ReceiveMessage", async (msgElem) =>
        {
            try
            {
                var evType = msgElem.TryGetProperty("eventType", out var et) ? et.GetString() : null;
                if (!string.Equals(evType, "prompt", StringComparison.OrdinalIgnoreCase))
                    return;

                var targetAgent = msgElem.TryGetProperty("targetAgentId", out var ta) ? ta.GetString() : null;
                if (!string.Equals(targetAgent, _preset.Id, StringComparison.OrdinalIgnoreCase))
                    return;

                if (!msgElem.TryGetProperty("sessionId", out var sidProp) || !Guid.TryParse(sidProp.GetString(), out var sessionId))
                    return;

                var msgId = msgElem.TryGetProperty("id", out var idProp) && Guid.TryParse(idProp.GetString(), out var parsedId)
                    ? parsedId
                    : Guid.NewGuid();

                var content = msgElem.TryGetProperty("content", out var cProp) ? cProp.GetString() ?? "" : "";
                var sender = msgElem.TryGetProperty("senderAgentId", out var sProp) ? sProp.GetString() ?? "operator" : "operator";
                var workDir = msgElem.TryGetProperty("workingDirectory", out var wd) ? wd.GetString() : null;
                var model = msgElem.TryGetProperty("model", out var m) ? m.GetString() : null;
                var effort = msgElem.TryGetProperty("effort", out var ef) ? ef.GetString() : null;
                var mode = msgElem.TryGetProperty("mode", out var md) ? md.GetString() : null;

                await HandlePromptAsync(new PromptReceivedEvent
                {
                    SessionId = sessionId,
                    MessageId = msgId,
                    AgentId = _preset.Id,
                    Prompt = content,
                    Sender = sender,
                    WorkingDirectory = workDir,
                    Model = model,
                    Effort = effort,
                    Mode = mode
                }, _cts.Token);
            }
            catch
            {
                // Ignore malformed broadcast
            }
        });

        _hubConnection.On<JsonElement>("StopExecution", (elem) =>
        {
            try
            {
                var sidStr = elem.TryGetProperty("sessionId", out var sidProp) ? sidProp.GetString() : null;
                if (sidStr != null && Guid.TryParse(sidStr, out var sid) && sid == State.ActiveSessionId)
                {
                    _onLog(_preset.Id, $"🛑 StopExecution received for session {sid.ToString()[..8]}. Aborting active turn.");
                    _activeTurnCts?.Cancel();
                }
            }
            catch { }
        });

        _hubConnection.On<JsonElement>("StopCommand", (elem) =>
        {
            try
            {
                var sidStr = elem.TryGetProperty("sessionId", out var sidProp) ? sidProp.GetString() : null;
                var cmdId = elem.TryGetProperty("commandId", out var cProp) ? cProp.GetString() : null;
                if (sidStr != null && Guid.TryParse(sidStr, out var sid) && sid == State.ActiveSessionId)
                {
                    _onLog(_preset.Id, $"🛑 StopCommand received for command '{cmdId}' in session {sid.ToString()[..8]}. Aborting active turn.");
                    _activeTurnCts?.Cancel();
                }
            }
            catch { }
        });

        _hubConnection.Reconnected += async (_) =>
        {
            State.State = RunnerConnectionState.Online;
            _onStateChanged();
            _onLog(_preset.Id, $"Reconnected to Mesh hub.");
            await _meshClient.SetAgentStatusAsync(_preset, "online");
            if (_hubConnection != null)
            {
                try { await _hubConnection.InvokeAsync("RegisterAgentRunner", _preset.Id); } catch { }
            }
        };

        _hubConnection.Closed += (_) =>
        {
            State.State = RunnerConnectionState.Offline;
            _onStateChanged();
            _onLog(_preset.Id, $"Disconnected from Mesh hub.");
            return Task.CompletedTask;
        };

        await _hubConnection.StartAsync(ct);

        try
        {
            await _hubConnection.InvokeAsync("RegisterAgentRunner", _preset.Id, ct);
        }
        catch
        {
            // Ignore if the running backend hub doesn't expose RegisterAgentRunner yet; HTTP upsert already set status="online"
        }

        State.State = RunnerConnectionState.Online;
        State.LastActivityAt = DateTimeOffset.UtcNow;
        _onStateChanged();
        _onLog(_preset.Id, $"ONLINE & listening for prompts (adapter: {_preset.AdapterType})");

        // Process any prompts that were queued while this agent was offline
        var pendingPrompts = await _meshClient.GetPendingPromptsForAgentAsync(_preset.Id, _agentToken, ct);
        foreach (var pending in pendingPrompts)
        {
            _onLog(_preset.Id, $"Found queued prompt in session {pending.SessionId.ToString()[..8]}: \"{pending.Prompt}\"");
            _ = Task.Run(() => HandlePromptAsync(pending, _cts.Token), _cts.Token);
        }
    }

    private async Task HandlePromptAsync(PromptReceivedEvent ev, CancellationToken ct)
    {
        if (ev.MessageId != Guid.Empty && !_processedMessages.TryAdd(ev.MessageId, 1))
        {
            return; // Already processing or processed this prompt
        }

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        _activeTurnCts = turnCts;
        var turnToken = turnCts.Token;

        try
        {
            State.State = RunnerConnectionState.Busy;
            State.ActiveSessionId = ev.SessionId;
            State.LastActivityAt = DateTimeOffset.UtcNow;
            _onStateChanged();

            // Resolve working directory: explicit working directory or persistent sandbox workspace
            var effectiveWorkDir = !string.IsNullOrWhiteSpace(ev.WorkingDirectory)
                ? ev.WorkingDirectory.Trim()
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentmesh", "workspaces", ev.SessionId.ToString());

            try
            {
                Directory.CreateDirectory(effectiveWorkDir);
            }
            catch (Exception ex)
            {
                _onLog(_preset.Id, $"Warning: Could not create workspace directory '{effectiveWorkDir}': {ex.Message}");
            }

            ev.WorkingDirectory = effectiveWorkDir;

            _onLog(_preset.Id, $"Prompt [{ev.SessionId.ToString()[..8]}] from {ev.Sender}: {ev.Prompt} (cwd: {effectiveWorkDir})");

            var adapter = AgentAdapterFactory.Create(_preset);
            await adapter.ExecutePromptAsync(_preset, ev, async (trajectoryEv) =>
            {
                if (!string.IsNullOrWhiteSpace(trajectoryEv.StructuredPayloadJson))
                {
                    try
                    {
                        using var pDoc = JsonDocument.Parse(trajectoryEv.StructuredPayloadJson);
                        if (pDoc.RootElement.TryGetProperty("usage", out var uElem))
                        {
                            var total = uElem.TryGetProperty("totalTokens", out var tot)
                                ? tot.GetInt64()
                                : (uElem.TryGetProperty("inputTokens", out var ip) ? ip.GetInt64() : 0) +
                                  (uElem.TryGetProperty("outputTokens", out var op) ? op.GetInt64() : 0);
                            var dur = uElem.TryGetProperty("durationSeconds", out var d) ? d.GetDouble() : 0;
                            State.TotalTokensUsed += total;
                            State.TotalDurationSeconds += dur;
                        }
                    }
                    catch { }
                }

                _onLog(_preset.Id, $"-> {trajectoryEv.EventType}: {Truncate(trajectoryEv.Content, 70)}");
                await _meshClient.PostTrajectoryEventAsync(
                    ev.SessionId,
                    _preset.Id,
                    _agentToken,
                    trajectoryEv,
                    turnToken);
            }, turnToken);

            State.PromptsHandled++;
            State.State = RunnerConnectionState.Online;
            State.ActiveSessionId = null;
            State.LastActivityAt = DateTimeOffset.UtcNow;
            _onStateChanged();
            _onLog(_preset.Id, $"Completed session [{ev.SessionId.ToString()[..8]}] response (total tokens: {State.TotalTokensUsed:N0}).");
        }
        catch (OperationCanceledException)
        {
            State.State = RunnerConnectionState.Online;
            State.ActiveSessionId = null;
            State.LastActivityAt = DateTimeOffset.UtcNow;
            _onStateChanged();
            _onLog(_preset.Id, $"🛑 Execution halted for session [{ev.SessionId.ToString()[..8]}].");
        }
        catch (Exception ex)
        {
            State.State = RunnerConnectionState.Online;
            State.ActiveSessionId = null;
            State.LastError = ex.Message;
            _onStateChanged();
            _onLog(_preset.Id, $"Error executing prompt: {ex.Message}");

            try
            {
                await _meshClient.PostTrajectoryEventAsync(
                    ev.SessionId,
                    _preset.Id,
                    _agentToken,
                    new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = $"⚠️ Agent `@{_preset.Id}` encountered an error while executing prompt: `{ex.Message}`"
                    },
                    ct);
            }
            catch
            {
                // Ignore secondary failure
            }
        }
        finally
        {
            if (_activeTurnCts == turnCts)
            {
                _activeTurnCts = null;
            }
        }
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        await _meshClient.SetAgentStatusAsync(_preset, "offline");
        if (_hubConnection != null)
        {
            try
            {
                await _hubConnection.StopAsync();
                await _hubConnection.DisposeAsync();
            }
            catch
            {
                // Ignore disconnect errors on shutdown
            }
            _hubConnection = null;
        }
        State.State = RunnerConnectionState.Offline;
        _onStateChanged();
        _onLog(_preset.Id, $"Stopped @{_preset.Id} (status -> offline).");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }

    private static string Truncate(string input, int maxLen)
    {
        var singleLine = input.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= maxLen ? singleLine : singleLine[..maxLen] + "...";
    }
}

public class AgentProcessManager : IAsyncDisposable
{
    private readonly CliConfig _config;
    private readonly ConfigStore _configStore;
    private readonly MeshClient _meshClient;
    private readonly ConcurrentDictionary<string, AgentRunnerInstance> _runners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(DateTimeOffset Timestamp, string AgentId, string Message)> _activityLogs = new();

    public event Action<string, string>? OnActivityLog;
    public event Action? OnStateChanged;

    public AgentProcessManager(CliConfig config, ConfigStore configStore, MeshClient meshClient)
    {
        _config = config;
        _configStore = configStore;
        _meshClient = meshClient;
    }

    public IReadOnlyList<(DateTimeOffset Timestamp, string AgentId, string Message)> GetRecentLogs(int count = 10) =>
        _activityLogs.TakeLast(count).ToList();

    public IReadOnlyList<AgentRuntimeState> GetAllAgentStates()
    {
        var list = new List<AgentRuntimeState>();
        foreach (var preset in _config.Agents.Values.OrderBy(a => a.Id))
        {
            if (_runners.TryGetValue(preset.Id, out var runner))
            {
                list.Add(runner.State);
            }
            else
            {
                list.Add(new AgentRuntimeState
                {
                    Id = preset.Id,
                    Name = preset.Name,
                    Role = preset.Role,
                    AdapterType = preset.AdapterType,
                    State = RunnerConnectionState.Offline
                });
            }
        }
        return list;
    }

    public bool IsRunning(string agentId) =>
        _runners.TryGetValue(agentId, out var r) &&
        r.State.State is RunnerConnectionState.Online or RunnerConnectionState.Busy or RunnerConnectionState.Connecting;

    public async Task<AgentRuntimeState> StartAgentAsync(
        string agentId,
        string? customCommand = null,
        string? customRole = null,
        CancellationToken ct = default)
    {
        var normalizedId = agentId.Trim().ToLowerInvariant();
        if (_runners.TryGetValue(normalizedId, out var existingRunner) &&
            existingRunner.State.State is RunnerConnectionState.Online or RunnerConnectionState.Busy)
        {
            return existingRunner.State;
        }

        var preset = _configStore.ResolveOrCreatePreset(_config, normalizedId, customCommand, customRole);
        var runner = new AgentRunnerInstance(preset, _meshClient, _config.MeshUrl, RecordLog, () => OnStateChanged?.Invoke());
        _runners[normalizedId] = runner;

        try
        {
            await runner.StartAsync(ct);
        }
        catch (Exception ex)
        {
            runner.State.State = RunnerConnectionState.Error;
            runner.State.LastError = ex.Message;
            OnStateChanged?.Invoke();
            RecordLog(normalizedId, $"Failed to connect: {ex.Message}");
            throw;
        }

        return runner.State;
    }

    public async Task StopAgentAsync(string agentId)
    {
        var normalizedId = agentId.Trim().ToLowerInvariant();
        if (_runners.TryRemove(normalizedId, out var runner))
        {
            await runner.DisposeAsync();
            OnStateChanged?.Invoke();
        }
    }

    public async Task StopAllAsync()
    {
        var keys = _runners.Keys.ToList();
        foreach (var key in keys)
        {
            await StopAgentAsync(key);
        }
    }

    private void RecordLog(string agentId, string message)
    {
        _activityLogs.Enqueue((DateTimeOffset.Now, agentId, message));
        while (_activityLogs.Count > 200)
        {
            _activityLogs.TryDequeue(out _);
        }
        OnActivityLog?.Invoke(agentId, message);
        OnStateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
    }
}
