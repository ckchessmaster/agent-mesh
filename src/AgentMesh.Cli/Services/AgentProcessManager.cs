using System.Collections.Concurrent;
using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace AgentMesh.Cli.Services;

public class InfiniteRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        return retryContext.PreviousRetryCount switch
        {
            0 => TimeSpan.Zero,
            1 => TimeSpan.FromSeconds(2),
            2 => TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(10)
        };
    }
}

public class AgentRunnerInstance : IAsyncDisposable
{
    private readonly AgentPresetConfig _preset;
    private readonly MeshClient _meshClient;
    private readonly string _meshUrl;
    private readonly Action<string, string> _onLog;
    private readonly Action _onStateChanged;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, byte> _processedMessages = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _stoppedSessions = new();
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private CancellationTokenSource? _activeTurnCts;
    private Task? _pollTask;

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
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .Build();

        static string? GetJsonPropString(JsonElement elem, string name)
        {
            if (elem.ValueKind != JsonValueKind.Object) return null;
            if (elem.TryGetProperty(name, out var p1) && p1.ValueKind == JsonValueKind.String) return p1.GetString();
            foreach (var prop in elem.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
                }
            }
            return null;
        }

        bool MatchesRunner(string? targetAgent, string? targetPersona)
        {
            var myPersona = _preset.PersonaId?.Trim().TrimStart('@');
            if (!string.IsNullOrEmpty(myPersona))
            {
                if (!string.IsNullOrEmpty(targetPersona))
                {
                    return string.Equals(targetPersona.Trim().TrimStart('@'), myPersona, StringComparison.OrdinalIgnoreCase);
                }
                return string.Equals(targetAgent?.Trim().TrimStart('@'), myPersona, StringComparison.OrdinalIgnoreCase);
            }

            var myAgent = _preset.Id?.Trim().TrimStart('@');
            return string.Equals(targetAgent?.Trim().TrimStart('@'), myAgent, StringComparison.OrdinalIgnoreCase);
        }

        _hubConnection.On<PromptReceivedEvent>("PromptReceived", async (ev) =>
        {
            if (!MatchesRunner(ev.AgentId, ev.PersonaId))
                return;

            await HandlePromptAsync(ev, _cts.Token);
        });

        _hubConnection.On<JsonElement>("PromptReceived", async (elem) =>
        {
            try
            {
                var targetAgent = GetJsonPropString(elem, "agentId")?.Trim().TrimStart('@');
                var personaId = GetJsonPropString(elem, "personaId");
                if (!MatchesRunner(targetAgent, personaId))
                    return;

                var sidStr = GetJsonPropString(elem, "sessionId");
                if (sidStr == null || !Guid.TryParse(sidStr, out var sessionId)) return;

                var midStr = GetJsonPropString(elem, "messageId");
                var messageId = (midStr != null && Guid.TryParse(midStr, out var mid)) ? mid : Guid.NewGuid();

                var prompt = GetJsonPropString(elem, "prompt") ?? "";
                var sender = GetJsonPropString(elem, "sender") ?? "operator";
                var workDir = GetJsonPropString(elem, "workingDirectory");
                var model = GetJsonPropString(elem, "model");
                var effort = GetJsonPropString(elem, "effort");
                var mode = GetJsonPropString(elem, "mode");
                var tier = GetJsonPropString(elem, "tier");
                var personaName = GetJsonPropString(elem, "personaName");
                var personaRole = GetJsonPropString(elem, "personaRole");
                var personaPrompt = GetJsonPropString(elem, "personaPrompt");
                var jobId = GetJsonPropString(elem, "jobId");
                var projectId = GetJsonPropString(elem, "projectId");
                var gitBranch = GetJsonPropString(elem, "gitBranch");
                var workspaceType = GetJsonPropString(elem, "workspaceType");
                var attachments = ExtractJsonAttachments(elem);

                await HandlePromptAsync(new PromptReceivedEvent
                {
                    SessionId = sessionId,
                    MessageId = messageId,
                    AgentId = _preset.Id ?? string.Empty,
                    DriverId = _preset.Id,
                    PersonaId = personaId,
                    PersonaName = personaName,
                    PersonaRole = personaRole,
                    PersonaPrompt = personaPrompt,
                    Prompt = prompt,
                    Sender = sender,
                    WorkingDirectory = workDir,
                    Model = model,
                    Effort = effort,
                    Mode = mode,
                    Tier = tier,
                    JobId = jobId,
                    ProjectId = projectId,
                    GitBranch = gitBranch,
                    WorkspaceType = workspaceType,
                    Attachments = attachments,
                    CreatedAt = DateTimeOffset.UtcNow
                }, _cts.Token);
            }
            catch
            {
                // Ignore malformed broadcast
            }
        });

        _hubConnection.On<StopExecutionEvent>("StopExecution", (ev) =>
        {
            HandleStopExecution(ev.SessionId, ev.AgentId, ev.PersonaId);
        });

        _hubConnection.On<JsonElement>("StopExecution", (elem) =>
        {
            try
            {
                var sidStr = GetJsonPropString(elem, "sessionId");
                var targetAgent = GetJsonPropString(elem, "agentId");
                var targetPersona = GetJsonPropString(elem, "personaId");
                Guid? parsedSid = (sidStr != null && Guid.TryParse(sidStr, out var s)) ? s : null;
                HandleStopExecution(parsedSid, targetAgent, targetPersona);
            }
            catch { }
        });

        _hubConnection.On<StopCommandEvent>("StopCommand", (ev) =>
        {
            HandleStopCommand(ev.SessionId, ev.CommandId, ev.AgentId);
        });

        _hubConnection.On<JsonElement>("StopCommand", (elem) =>
        {
            try
            {
                var sidStr = GetJsonPropString(elem, "sessionId");
                var cmdId = GetJsonPropString(elem, "commandId");
                var targetAgent = GetJsonPropString(elem, "agentId");
                Guid? parsedSid = (sidStr != null && Guid.TryParse(sidStr, out var s)) ? s : null;
                HandleStopCommand(parsedSid, cmdId, targetAgent);
            }
            catch { }
        });

        _hubConnection.Reconnecting += (exception) =>
        {
            State.State = RunnerConnectionState.Connecting;
            _onStateChanged();
            _onLog(_preset.Id, $"Mesh connection interrupted. Reconnecting... ({exception?.Message ?? "network"})");
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += async (_) =>
        {
            State.State = RunnerConnectionState.Online;
            _onStateChanged();
            _onLog(_preset.Id, $"Reconnected to Mesh hub.");
            await _meshClient.SetAgentStatusAsync(_preset, "online");
            if (_hubConnection != null)
            {
                if (!string.IsNullOrWhiteSpace(_preset.PersonaId))
                {
                    try { await _hubConnection.InvokeAsync("RegisterPersonaRunner", _preset.PersonaId, _preset.Id); } catch { }
                }
                try { await _hubConnection.InvokeAsync("RegisterAgentRunner", _preset.Id); } catch { }
            }
            await CheckAndProcessPendingPromptsAsync(_cts.Token);
        };

        _hubConnection.Closed += async (ex) =>
        {
            State.State = RunnerConnectionState.Offline;
            _onStateChanged();
            _onLog(_preset.Id, $"Disconnected from Mesh hub{(ex != null ? $": {ex.Message}" : ".")}");

            if (!_cts.IsCancellationRequested)
            {
                _ = Task.Run(async () =>
                {
                    while (!_cts.IsCancellationRequested && State.State == RunnerConnectionState.Offline)
                    {
                        try
                        {
                            await Task.Delay(5000, _cts.Token);
                            if (_hubConnection != null && _hubConnection.State == HubConnectionState.Disconnected)
                            {
                                State.State = RunnerConnectionState.Connecting;
                                _onStateChanged();
                                _onLog(_preset.Id, "Attempting to re-establish hub connection...");
                                await _hubConnection.StartAsync(_cts.Token);
                                if (!string.IsNullOrWhiteSpace(_preset.PersonaId))
                                {
                                    try { await _hubConnection.InvokeAsync("RegisterPersonaRunner", _preset.PersonaId, _preset.Id, _cts.Token); } catch { }
                                }
                                try { await _hubConnection.InvokeAsync("RegisterAgentRunner", _preset.Id, _cts.Token); } catch { }
                                State.State = RunnerConnectionState.Online;
                                _onStateChanged();
                                _onLog(_preset.Id, "Re-established connection to Mesh hub.");
                                await _meshClient.SetAgentStatusAsync(_preset, "online", _cts.Token);
                                await CheckAndProcessPendingPromptsAsync(_cts.Token);
                                break;
                            }
                        }
                        catch
                        {
                            State.State = RunnerConnectionState.Offline;
                            _onStateChanged();
                        }
                    }
                }, _cts.Token);
            }
        };

        await _hubConnection.StartAsync(ct);

        if (!string.IsNullOrWhiteSpace(_preset.PersonaId))
        {
            try { await _hubConnection.InvokeAsync("RegisterPersonaRunner", _preset.PersonaId, _preset.Id, ct); } catch { }
        }
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
        await CheckAndProcessPendingPromptsAsync(_cts.Token);

        // Start background poll loop to ensure no prompts are missed even during brief network or hub hiccups
        _pollTask = Task.Run(() => StartPendingPromptPollLoopAsync(_cts.Token));
    }

    private async Task StartPendingPromptPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                if (State.State is RunnerConnectionState.Online or RunnerConnectionState.Busy)
                {
                    await CheckAndProcessPendingPromptsAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Silently ignore transient poll failures
            }
        }
    }

    private async Task CheckAndProcessPendingPromptsAsync(CancellationToken ct)
    {
        try
        {
            var pendingPrompts = !string.IsNullOrWhiteSpace(_preset.PersonaId)
                ? await _meshClient.GetPendingPromptsForPersonaAsync(_preset.PersonaId, _agentToken, ct)
                : await _meshClient.GetPendingPromptsForAgentAsync(_preset.Id, _agentToken, ct);

            foreach (var pending in pendingPrompts)
            {
                if (pending.MessageId != Guid.Empty && _processedMessages.ContainsKey(pending.MessageId))
                {
                    continue;
                }
                _onLog(_preset.Id, $"Found pending prompt in session {pending.SessionId.ToString()[..8]}: \"{pending.Prompt}\"");
                _ = Task.Run(() => HandlePromptAsync(pending, _cts.Token), _cts.Token);
            }
        }
        catch
        {
            // Transient fetch error
        }
    }

    private void HandleStopExecution(Guid? parsedSid, string? targetAgent, string? targetPersona = null)
    {
        var targetSid = parsedSid ?? State.ActiveSessionId;
        if (targetSid.HasValue)
        {
            _stoppedSessions[targetSid.Value] = DateTimeOffset.UtcNow;
        }

        var matchesSession = State.ActiveSessionId.HasValue && parsedSid.HasValue && parsedSid.Value == State.ActiveSessionId.Value;
        var matchesPersona = !string.IsNullOrWhiteSpace(_preset.PersonaId) &&
                             !string.IsNullOrWhiteSpace(targetPersona) &&
                             string.Equals(targetPersona.Trim().TrimStart('@'), _preset.PersonaId.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase);
        var matchesAgent = string.IsNullOrWhiteSpace(targetAgent) ||
                           string.Equals(targetAgent.Trim().TrimStart('@'), _preset.Id?.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase) ||
                           (!string.IsNullOrWhiteSpace(_preset.PersonaId) && string.Equals(targetAgent.Trim().TrimStart('@'), _preset.PersonaId.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase));

        if (matchesSession || matchesPersona || (matchesAgent && State.ActiveSessionId != null) || (targetSid.HasValue && targetSid == State.ActiveSessionId))
        {
            _onLog(_preset.Id ?? string.Empty, $"🛑 StopExecution received for session {targetSid?.ToString()[..8] ?? "unknown"}. Aborting active turn immediately.");
            try
            {
                _activeTurnCts?.Cancel();
            }
            catch { }
        }
    }

    private void HandleStopCommand(Guid? parsedSid, string? cmdId, string? targetAgent)
    {
        var targetSid = parsedSid ?? State.ActiveSessionId;
        if (targetSid.HasValue)
        {
            _stoppedSessions[targetSid.Value] = DateTimeOffset.UtcNow;
        }

        var matchesSession = State.ActiveSessionId.HasValue && parsedSid.HasValue && parsedSid.Value == State.ActiveSessionId.Value;
        var matchesAgent = string.IsNullOrWhiteSpace(targetAgent) ||
                           string.Equals(targetAgent, _preset.Id, StringComparison.OrdinalIgnoreCase) ||
                           (!string.IsNullOrWhiteSpace(_preset.PersonaId) && string.Equals(targetAgent, _preset.PersonaId, StringComparison.OrdinalIgnoreCase));

        if (matchesSession || (matchesAgent && State.ActiveSessionId != null) || (targetSid.HasValue && targetSid == State.ActiveSessionId))
        {
            _onLog(_preset.Id, $"🛑 StopCommand received for command '{cmdId}' in session {targetSid?.ToString()[..8] ?? "unknown"}. Aborting active turn immediately.");
            try
            {
                _activeTurnCts?.Cancel();
            }
            catch { }
        }
    }

    private async Task HandlePromptAsync(PromptReceivedEvent ev, CancellationToken ct)
    {
        if (ev.MessageId != Guid.Empty && !_processedMessages.TryAdd(ev.MessageId, 1))
        {
            return; // Already processing or processed this prompt
        }

        if (_stoppedSessions.TryGetValue(ev.SessionId, out var stoppedTime) && ev.CreatedAt <= stoppedTime)
        {
            _onLog(_preset.Id, $"🛑 Discarding prompt for stopped session {ev.SessionId.ToString()[..8]} received before stop: \"{Truncate(ev.Prompt, 50)}\"");
            return;
        }

        try
        {
            await _turnLock.WaitAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        _activeTurnCts = turnCts;
        var turnToken = turnCts.Token;

        try
        {
            if (_stoppedSessions.TryGetValue(ev.SessionId, out var stoppedAtAfterLock))
            {
                if (ev.CreatedAt <= stoppedAtAfterLock)
                {
                    _onLog(_preset.Id, $"🛑 Discarding queued prompt for stopped session {ev.SessionId.ToString()[..8]} (queued before stop): \"{Truncate(ev.Prompt, 50)}\"");
                    return;
                }
                else
                {
                    _stoppedSessions.TryRemove(ev.SessionId, out _);
                }
            }

            State.State = RunnerConnectionState.Busy;
            State.ActiveSessionId = ev.SessionId;
            State.LastActivityAt = DateTimeOffset.UtcNow;
            _onStateChanged();

            // Resolve working directory: explicit working directory or persistent sandbox workspace
            var effectiveWorkDir = !string.IsNullOrWhiteSpace(ev.WorkingDirectory)
                ? ev.WorkingDirectory.Trim()
                : (Directory.Exists("/workspace")
                    ? "/workspace"
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentmesh", "workspaces", ev.SessionId.ToString()));

            try
            {
                Directory.CreateDirectory(effectiveWorkDir);
            }
            catch (Exception ex)
            {
                _onLog(_preset.Id, $"Warning: Could not create workspace directory '{effectiveWorkDir}': {ex.Message}");
            }

            ev.WorkingDirectory = effectiveWorkDir;

            if (ev.Attachments is { Count: > 0 })
            {
                await StagePromptAttachmentsAsync(ev, effectiveWorkDir, _meshClient, (msg) => _onLog(_preset.Id, msg), turnToken);
            }

            _onLog(_preset.Id, $"Prompt [{ev.SessionId.ToString()[..8]}] from {ev.Sender} (model: {ev.Model ?? "default"}, effort: {ev.Effort ?? "default"}): {ev.Prompt} (cwd: {effectiveWorkDir})");

            if (!string.IsNullOrWhiteSpace(ev.GitBranch))
            {
                await GitWorkspaceManager.PrepareJobBranchAsync(
                    effectiveWorkDir,
                    ev.GitBranch,
                    msg => _onLog(_preset.Id, $"[git] {msg}"));
            }

            var adapter = AgentAdapterFactory.Create(_preset);
            var terminalEventEmitted = false;
            await adapter.ExecutePromptAsync(_preset, ev, async (trajectoryEv) =>
            {
                turnToken.ThrowIfCancellationRequested();

                if (string.Equals(trajectoryEv.EventType, "response", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(trajectoryEv.EventType, "tool_call", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(trajectoryEv.ToolName, "ask_question", StringComparison.OrdinalIgnoreCase)))
                {
                    terminalEventEmitted = true;
                }

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

            if (!terminalEventEmitted && !turnToken.IsCancellationRequested)
            {
                _onLog(_preset.Id, $"Warning: Turn completed without a terminal response or question event. Emitting fallback completion response.");
                await _meshClient.PostTrajectoryEventAsync(
                    ev.SessionId,
                    _preset.Id,
                    _agentToken,
                    new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = $"Turn completed by agent `@{_preset.Id}`."
                    },
                    turnToken);
            }

            if (!string.IsNullOrWhiteSpace(ev.GitBranch))
            {
                await GitWorkspaceManager.CommitTurnChangesAsync(
                    effectiveWorkDir,
                    ev.GitBranch,
                    ev.PersonaId ?? _preset.Id,
                    ev.PersonaName ?? _preset.Name,
                    ev.Prompt,
                    msg => _onLog(_preset.Id, $"[git] {msg}"),
                    customAuthorName: ev.GitAuthorName,
                    customAuthorEmail: ev.GitAuthorEmail);
            }

            var convId = AgyAdapter.GetSessionConversationId(ev.SessionId);
            if (!string.IsNullOrWhiteSpace(convId))
            {
                try
                {
                    await AgyAdapter.SyncBrainArtifactsAsync(ev.SessionId, convId, _meshClient, turnToken);
                }
                catch { }
            }

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
                if (!turnToken.IsCancellationRequested)
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
            _turnLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        if (_pollTask != null)
        {
            try { await _pollTask; } catch { }
            _pollTask = null;
        }
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

    public static List<AttachmentItem>? ExtractJsonAttachments(JsonElement elem)
    {
        foreach (var prop in elem.EnumerateObject())
        {
            if (string.Equals(prop.Name, "attachments", StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    return JsonSerializer.Deserialize<List<AttachmentItem>>(prop.Value.GetRawText(), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch
                {
                    return null;
                }
            }
        }
        return null;
    }

    public static async Task<List<string>> StagePromptAttachmentsAsync(
        PromptReceivedEvent ev,
        string effectiveWorkDir,
        MeshClient meshClient,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        var stagedPaths = new List<string>();
        if (ev.Attachments == null || ev.Attachments.Count == 0)
        {
            return stagedPaths;
        }

        var attachDir = Path.Combine(effectiveWorkDir, ".agentmesh", "attachments");
        try
        {
            Directory.CreateDirectory(attachDir);
        }
        catch (Exception ex)
        {
            onLog?.Invoke($"Warning: Could not create attachment directory '{attachDir}': {ex.Message}");
            return stagedPaths;
        }

        foreach (var att in ev.Attachments)
        {
            if (string.IsNullOrWhiteSpace(att.Id) && string.IsNullOrWhiteSpace(att.FileName))
            {
                continue;
            }

            var safeName = Path.GetFileName(string.IsNullOrWhiteSpace(att.FileName) ? "attachment.bin" : att.FileName.Trim());
            var prefix = string.IsNullOrWhiteSpace(att.Id) ? Guid.NewGuid().ToString("N")[..8] : att.Id;
            var targetPath = Path.Combine(attachDir, $"{prefix}_{safeName}");

            var downloaded = false;
            if (File.Exists(targetPath))
            {
                downloaded = true;
            }
            else if (!string.IsNullOrWhiteSpace(att.DownloadUrl))
            {
                downloaded = await meshClient.DownloadAttachmentFromUrlAsync(att.DownloadUrl, targetPath, ct);
            }

            if (!downloaded && !string.IsNullOrWhiteSpace(att.Id))
            {
                downloaded = await meshClient.DownloadSessionAttachmentAsync(ev.SessionId, att.Id, targetPath, ct);
            }

            if (!downloaded)
            {
                // Local disk fallback when running on same machine as API
                var localFallbackPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".agentmesh",
                    "artifacts",
                    ev.SessionId.ToString(),
                    "attachments",
                    $"{att.Id}_{safeName}");
                if (File.Exists(localFallbackPath))
                {
                    try
                    {
                        File.Copy(localFallbackPath, targetPath, overwrite: true);
                        downloaded = true;
                    }
                    catch { }
                }
            }

            if (downloaded)
            {
                att.LocalFilePath = targetPath;
                stagedPaths.Add(targetPath);
                onLog?.Invoke($"📎 Staged attachment '{safeName}' -> {targetPath}");
            }
            else
            {
                onLog?.Invoke($"Warning: Could not download attachment '{safeName}' ({att.Id}) for session {ev.SessionId.ToString()[..8]}");
            }
        }

        return stagedPaths;
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
        string? customAdapter = null,
        string? customEndpoint = null,
        string? customApiKey = null,
        string? customDefaultModel = null,
        CancellationToken ct = default)
    {
        var normalizedId = agentId.Trim().ToLowerInvariant();
        if (_runners.TryGetValue(normalizedId, out var existingRunner) &&
            existingRunner.State.State is RunnerConnectionState.Online or RunnerConnectionState.Busy)
        {
            return existingRunner.State;
        }

        RemoteAgentDto? remoteAgent = null;
        try
        {
            remoteAgent = await _meshClient.GetMeshAgentAsync(normalizedId, ct);
        }
        catch { }

        var preset = _configStore.ResolveOrCreatePreset(_config, normalizedId, customCommand, customRole);
        if (remoteAgent != null)
        {
            _configStore.PopulateFromRemote(preset, remoteAgent);
        }

        if (!string.IsNullOrWhiteSpace(customCommand)) preset.Command = customCommand;
        if (!string.IsNullOrWhiteSpace(customRole)) preset.Role = customRole;
        if (!string.IsNullOrWhiteSpace(customAdapter)) preset.AdapterType = customAdapter.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(customEndpoint)) preset.Endpoint = customEndpoint;
        if (!string.IsNullOrWhiteSpace(customApiKey)) preset.ApiKey = customApiKey;
        if (!string.IsNullOrWhiteSpace(customDefaultModel)) preset.DefaultModel = customDefaultModel;

        if (string.Equals(preset.AdapterType, "openai", StringComparison.OrdinalIgnoreCase) &&
            (preset.Capabilities == null || preset.Capabilities.Count == 0 ||
             preset.Capabilities.SequenceEqual(new[] { "shell", "inspect_workspace" })))
        {
            preset.Capabilities = new List<string> { "run_command", "read_file", "write_file", "edit_file", "ask_question" };
            preset.Command = null;
        }

        _configStore.Save(_config);

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

    public async Task<AgentRuntimeState> StartPersonaAsync(
        string personaId,
        string? customDriver = null,
        string? customCommand = null,
        string? customModel = null,
        CancellationToken ct = default)
    {
        var normalizedPersonaId = personaId.Trim().ToLowerInvariant();
        if (_runners.TryGetValue(normalizedPersonaId, out var existingRunner) &&
            existingRunner.State.State is RunnerConnectionState.Online or RunnerConnectionState.Busy)
        {
            return existingRunner.State;
        }

        RemotePersonaDto? remotePersona = null;
        try
        {
            remotePersona = await _meshClient.GetPersonaAsync(normalizedPersonaId, ct);
        }
        catch { }

        var driverId = (!string.IsNullOrWhiteSpace(customDriver)
            ? customDriver
            : remotePersona?.DriverId ?? "openai").Trim().TrimStart('@').ToLowerInvariant();

        RemoteAgentDto? remoteDriver = null;
        try
        {
            remoteDriver = await _meshClient.GetMeshAgentAsync(driverId, ct);
        }
        catch { }

        var preset = _configStore.ResolveOrCreatePreset(_config, driverId, customCommand);
        if (remoteDriver != null)
        {
            _configStore.PopulateFromRemote(preset, remoteDriver);
        }

        preset.PersonaId = normalizedPersonaId;
        preset.PersonaName = remotePersona?.Name ?? normalizedPersonaId;
        preset.PersonaPrompt = remotePersona?.PersonaPrompt ?? "";
        preset.Name = $"{remotePersona?.Name ?? normalizedPersonaId} (@{driverId})";
        preset.Role = remotePersona?.Role ?? preset.Role;

        if (!string.IsNullOrWhiteSpace(customCommand)) preset.Command = customCommand;
        if (!string.IsNullOrWhiteSpace(customModel)) preset.DefaultModel = customModel;
        else if (!string.IsNullOrWhiteSpace(remotePersona?.DefaultModel)) preset.DefaultModel = remotePersona.DefaultModel;

        if (string.Equals(preset.AdapterType, "openai", StringComparison.OrdinalIgnoreCase) &&
            (preset.Capabilities == null || preset.Capabilities.Count == 0 ||
             preset.Capabilities.SequenceEqual(new[] { "shell", "inspect_workspace" })))
        {
            preset.Capabilities = new List<string> { "run_command", "read_file", "write_file", "edit_file", "ask_question" };
            preset.Command = null;
        }

        var runner = new AgentRunnerInstance(preset, _meshClient, _config.MeshUrl, RecordLog, () => OnStateChanged?.Invoke());
        _runners[normalizedPersonaId] = runner;

        try
        {
            await runner.StartAsync(ct);
        }
        catch (Exception ex)
        {
            runner.State.State = RunnerConnectionState.Error;
            runner.State.LastError = ex.Message;
            OnStateChanged?.Invoke();
            RecordLog(normalizedPersonaId, $"Failed to connect persona @{normalizedPersonaId}: {ex.Message}");
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
