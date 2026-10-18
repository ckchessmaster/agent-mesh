using System.Collections.Concurrent;
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
    private readonly CancellationTokenSource _cts = new();

    private HubConnection? _hubConnection;
    private string? _agentToken;

    public AgentRuntimeState State { get; }

    public AgentRunnerInstance(
        AgentPresetConfig preset,
        MeshClient meshClient,
        string meshUrl,
        Action<string, string> onLog)
    {
        _preset = preset;
        _meshClient = meshClient;
        _meshUrl = meshUrl.TrimEnd('/');
        _onLog = onLog;

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
        _onLog(_preset.Id, $"Registering agent @{_preset.Id} ({_preset.Name}) with Mesh...");

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

        _hubConnection.Reconnected += async (_) =>
        {
            State.State = RunnerConnectionState.Online;
            _onLog(_preset.Id, $"Reconnected to Mesh hub. Re-announcing @{_preset.Id}...");
            if (_hubConnection != null)
            {
                await _hubConnection.InvokeAsync("RegisterAgentRunner", _preset.Id);
            }
        };

        _hubConnection.Closed += (_) =>
        {
            State.State = RunnerConnectionState.Offline;
            _onLog(_preset.Id, $"Disconnected from Mesh hub.");
            return Task.CompletedTask;
        };

        await _hubConnection.StartAsync(ct);
        await _hubConnection.InvokeAsync("RegisterAgentRunner", _preset.Id, ct);

        State.State = RunnerConnectionState.Online;
        State.LastActivityAt = DateTimeOffset.UtcNow;
        _onLog(_preset.Id, $"Connected and ONLINE (adapter: {_preset.AdapterType})");

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
        try
        {
            State.State = RunnerConnectionState.Busy;
            State.ActiveSessionId = ev.SessionId;
            State.LastActivityAt = DateTimeOffset.UtcNow;

            _onLog(_preset.Id, $"Prompt received [{ev.SessionId.ToString()[..8]}] from {ev.Sender}: {ev.Prompt}");

            var adapter = AgentAdapterFactory.Create(_preset);
            await adapter.ExecutePromptAsync(_preset, ev, async (trajectoryEv) =>
            {
                _onLog(_preset.Id, $"Streaming {trajectoryEv.EventType}: {Truncate(trajectoryEv.Content, 80)}");
                await _meshClient.PostTrajectoryEventAsync(
                    ev.SessionId,
                    _preset.Id,
                    _agentToken,
                    trajectoryEv,
                    ct);
            }, ct);

            State.PromptsHandled++;
            State.State = RunnerConnectionState.Online;
            State.ActiveSessionId = null;
            State.LastActivityAt = DateTimeOffset.UtcNow;
            _onLog(_preset.Id, $"Completed session [{ev.SessionId.ToString()[..8]}] turn.");
        }
        catch (Exception ex)
        {
            State.State = RunnerConnectionState.Online;
            State.LastError = ex.Message;
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
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
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
        _onLog(_preset.Id, $"Stopped @{_preset.Id}.");
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

    public AgentProcessManager(CliConfig config, ConfigStore configStore, MeshClient meshClient)
    {
        _config = config;
        _configStore = configStore;
        _meshClient = meshClient;
    }

    public IReadOnlyList<(DateTimeOffset Timestamp, string AgentId, string Message)> GetRecentLogs(int count = 25) =>
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
        var runner = new AgentRunnerInstance(preset, _meshClient, _config.MeshUrl, RecordLog);
        _runners[normalizedId] = runner;

        try
        {
            await runner.StartAsync(ct);
        }
        catch (Exception ex)
        {
            runner.State.State = RunnerConnectionState.Error;
            runner.State.LastError = ex.Message;
            RecordLog(normalizedId, $"Failed to connect to Mesh at {_config.MeshUrl}: {ex.Message}");
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
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync();
    }
}
