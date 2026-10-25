using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Infrastructure.Containers;

public class DockerPersonaContainerManager : IPersonaContainerManager
{
    private readonly ILogger<DockerPersonaContainerManager> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceProvider _serviceProvider;
    private readonly DockerClient? _dockerClient;
    private readonly string _runtimeName;
    private readonly string? _detectedCli;
    private readonly string _defaultImage;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, ActiveAuthSession> _activeAuthSessions = new();

    public string RuntimeName => _runtimeName;

    public DockerPersonaContainerManager(
        ILogger<DockerPersonaContainerManager> logger,
        IConfiguration config,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _config = config;
        _serviceProvider = serviceProvider;
        _defaultImage = config["PersonaContainer:Image"] ?? "agentmesh-persona-runner:latest";

        var (client, runtime) = InitializeDockerClient(logger, config);
        _dockerClient = client;
        _runtimeName = runtime;
        _detectedCli = ResolveCliBinary();

        _logger.LogInformation("PersonaContainerManager initialized using runtime: {Runtime} (CLI: {Cli}, DockerDotNet: {Available})",
            _runtimeName, _detectedCli ?? "none", _dockerClient != null);
    }

    private static (DockerClient? client, string runtime) InitializeDockerClient(
        ILogger logger,
        IConfiguration config)
    {
        var explicitHost = config["DOCKER_HOST"] ?? Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(explicitHost))
        {
            try
            {
                var uri = new Uri(explicitHost);
                var client = new DockerClientConfiguration(uri).CreateClient();
                var rt = explicitHost.Contains("podman", StringComparison.OrdinalIgnoreCase) ? "podman" : "docker";
                return (client, rt);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to connect to explicit DOCKER_HOST {Host}: {Error}", explicitHost, ex.Message);
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var winUri = new Uri("npipe://./pipe/docker_engine");
                var client = new DockerClientConfiguration(winUri).CreateClient();
                return (client, "docker");
            }
            catch { }
        }
        else
        {
            var candidateSockets = new (string path, string runtime)[]
            {
                ("/var/run/docker.sock", "docker"),
                ("/run/podman/podman.sock", "podman"),
                (GetPodmanUserSocketPath(), "podman")
            };

            foreach (var (sockPath, rt) in candidateSockets)
            {
                if (!string.IsNullOrWhiteSpace(sockPath) && File.Exists(sockPath))
                {
                    try
                    {
                        var uri = new Uri($"unix://{sockPath}");
                        var client = new DockerClientConfiguration(uri).CreateClient();
                        return (client, rt);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Found socket at {Path} but failed to initialize DockerClient: {Error}", sockPath, ex.Message);
                    }
                }
            }
        }

        var cli = ResolveCliBinary();
        var fallbackRuntime = cli ?? "none";
        return (null, fallbackRuntime);
    }

    private static string GetPodmanUserSocketPath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(xdg, "podman", "podman.sock");
        }

        var uid = Environment.GetEnvironmentVariable("UID");
        if (string.IsNullOrWhiteSpace(uid) && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var psi = new ProcessStartInfo("id", "-u") { RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    uid = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                }
            }
            catch { }
        }

        return !string.IsNullOrWhiteSpace(uid) ? $"/run/user/{uid}/podman/podman.sock" : string.Empty;
    }

    private static string? ResolveCliBinary()
    {
        var candidates = new[] { "podman", "docker" };
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var c in candidates)
        {
            foreach (var dir in paths)
            {
                var full = Path.Combine(dir, c);
                if (File.Exists(full)) return c;
            }
        }

        return null;
    }

    public async Task<PersonaEnvironmentStatus> GetStatusAsync(string personaId, CancellationToken ct = default)
    {
        var containerName = GetContainerName(personaId);

        if (_dockerClient != null)
        {
            try
            {
                var inspect = await _dockerClient.Containers.InspectContainerAsync(containerName, ct);
                var isRunning = inspect.State.Running;
                DateTimeOffset? startedAt = DateTimeOffset.TryParse(inspect.State.StartedAt, out var s) ? s : null;

                return new PersonaEnvironmentStatus(
                    PersonaId: personaId,
                    Status: isRunning ? "running" : "stopped",
                    Runtime: _runtimeName,
                    ContainerId: inspect.ID[..Math.Min(12, inspect.ID.Length)],
                    StartedAt: startedAt,
                    LastActivityAt: DateTimeOffset.UtcNow);
            }
            catch (DockerContainerNotFoundException)
            {
                return new PersonaEnvironmentStatus(personaId, "stopped", _runtimeName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("DockerClient inspect failed for {Name}: {Error}", containerName, ex.Message);
            }
        }

        if (_detectedCli != null)
        {
            var (exitCode, stdout, _) = await RunCliAsync(_detectedCli, $"inspect {containerName} --format json", ct);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                try
                {
                    using var doc = JsonDocument.Parse(stdout);
                    var elem = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                        ? doc.RootElement[0]
                        : doc.RootElement;

                    if (elem.TryGetProperty("State", out var stateProp))
                    {
                        var running = stateProp.TryGetProperty("Running", out var rProp) && rProp.GetBoolean();
                        var id = elem.TryGetProperty("Id", out var idProp) ? idProp.GetString() : null;
                        var started = stateProp.TryGetProperty("StartedAt", out var sProp) &&
                                      DateTimeOffset.TryParse(sProp.GetString(), out var s) ? s : (DateTimeOffset?)null;

                        return new PersonaEnvironmentStatus(
                            PersonaId: personaId,
                            Status: running ? "running" : "stopped",
                            Runtime: _runtimeName,
                            ContainerId: id != null ? id[..Math.Min(12, id.Length)] : null,
                            StartedAt: started,
                            LastActivityAt: DateTimeOffset.UtcNow);
                    }
                }
                catch { }
            }
        }

        return new PersonaEnvironmentStatus(personaId, "stopped", _runtimeName);
    }

    public async Task EnsureRunningAsync(string personaId, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        var status = await GetStatusAsync(normPersonaId, ct);
        var containerName = GetContainerName(normPersonaId);

        if (status.Status == "running")
        {
            await SeedHostCredentialsIfAvailableAsync(containerName, ct);
            return;
        }

        var volumeName = $"agentmesh-persona-{normPersonaId}-data";
        var homeVolumeName = $"agentmesh-persona-{normPersonaId}-home";

        await EnsureImageAvailableAsync(ct);

        var meshUrl = _config["AGENTMESH_URL"] ??
                      _config["PersonaContainer:MeshUrl"] ??
                      "http://host.docker.internal:5000";

        // Auto-provision or retrieve dedicated agent token to prevent 401 Unauthorized
        var agentToken = await ProvisionAgentTokenAsync(normPersonaId, ct);

        var envList = new List<string>
        {
            $"AGENTMESH_URL={meshUrl}",
            $"AGENTMESH_TOKEN={agentToken}",
            $"AGENTMESH_PERSONA_ID={normPersonaId}"
        };

        var forwardedVars = new[]
        {
            "OPENAI_API_KEY",
            "OPENAI_BASE_URL",
            "ANTHROPIC_API_KEY",
            "GEMINI_API_KEY",
            "GITHUB_TOKEN"
        };

        foreach (var v in forwardedVars)
        {
            var val = _config[v] ?? Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrWhiteSpace(val))
            {
                envList.Add($"{v}={val}");
            }
        }

        if (_dockerClient != null)
        {
            try
            {
                try
                {
                    var inspect = await _dockerClient.Containers.InspectContainerAsync(containerName, ct);
                    if (!inspect.State.Running)
                    {
                        _logger.LogInformation("Starting existing container {Name} for persona @{PersonaId}", containerName, normPersonaId);
                        await _dockerClient.Containers.StartContainerAsync(containerName, new ContainerStartParameters(), ct);
                        await SeedHostCredentialsIfAvailableAsync(containerName, ct);
                        return;
                    }
                }
                catch (DockerContainerNotFoundException) { }

                _logger.LogInformation("Creating new container {Name} for persona @{PersonaId} from image {Image}", containerName, normPersonaId, _defaultImage);

                var extraHosts = new List<string>
                {
                    "host.docker.internal:host-gateway",
                    "host.containers.internal:host-gateway"
                };

                var createParams = new CreateContainerParameters
                {
                    Image = _defaultImage,
                    Name = containerName,
                    Env = envList,
                    HostConfig = new HostConfig
                    {
                        ExtraHosts = extraHosts,
                        Binds = new List<string>
                        {
                            $"{volumeName}:/workspace",
                            $"{homeVolumeName}:/root"
                        },
                        RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
                    }
                };

                var createRes = await _dockerClient.Containers.CreateContainerAsync(createParams, ct);
                await _dockerClient.Containers.StartContainerAsync(createRes.ID, new ContainerStartParameters(), ct);
                _logger.LogInformation("Container {Name} ({ID}) started successfully for @{PersonaId}", containerName, createRes.ID[..12], normPersonaId);
                await SeedHostCredentialsIfAvailableAsync(containerName, ct);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("DockerClient creation failed, attempting CLI fallback: {Error}", ex.Message);
            }
        }

        if (_detectedCli != null)
        {
            var (inspectExit, _, _) = await RunCliAsync(_detectedCli, $"inspect {containerName}", ct);
            if (inspectExit == 0)
            {
                _logger.LogInformation("Starting existing container {Name} via {Cli} for @{PersonaId}", containerName, _detectedCli, normPersonaId);
                var (startExit, _, startErr) = await RunCliAsync(_detectedCli, $"start {containerName}", ct);
                if (startExit != 0)
                {
                    throw new InvalidOperationException($"Failed to start container {containerName}: {startErr}");
                }
                await SeedHostCredentialsIfAvailableAsync(containerName, ct);
                return;
            }

            var sb = new StringBuilder();
            sb.Append($"run -d --name {containerName} ");
            sb.Append($"--add-host host.docker.internal:host-gateway ");
            sb.Append($"--add-host host.containers.internal:host-gateway ");
            sb.Append($"-v {volumeName}:/workspace ");
            sb.Append($"-v {homeVolumeName}:/root ");

            foreach (var e in envList)
            {
                var parts = e.Split('=', 2);
                if (parts.Length == 2)
                {
                    sb.Append($"-e \"{parts[0]}={parts[1].Replace("\"", "\\\"")}\" ");
                }
            }

            sb.Append($"{_defaultImage}");

            _logger.LogInformation("Launching container {Name} via {Cli} for @{PersonaId}", containerName, _detectedCli, normPersonaId);
            var (runExit, runOut, runErr) = await RunCliAsync(_detectedCli, sb.ToString(), ct);
            if (runExit != 0)
            {
                throw new InvalidOperationException($"Failed to run container via {_detectedCli}: {runErr}\n{runOut}");
            }
            _logger.LogInformation("Container {Name} launched successfully via {Cli}", containerName, _detectedCli);
            await SeedHostCredentialsIfAvailableAsync(containerName, ct);
            return;
        }

        throw new InvalidOperationException($"No active Docker/Podman socket or CLI runtime available to launch persona container for @{personaId}.");
    }

    private async Task<string> ProvisionAgentTokenAsync(string personaId, CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
            var apiKeyService = scope.ServiceProvider.GetRequiredService<IApiKeyService>();

            var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == personaId, ct);
            var driverId = (!string.IsNullOrWhiteSpace(persona?.DriverId) ? persona.DriverId : personaId).Trim().ToLowerInvariant();

            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == driverId, ct);
            var rawToken = apiKeyService.GenerateToken();
            var hashed = apiKeyService.HashToken(rawToken);

            if (agent != null)
            {
                agent.ApiKeyHash = hashed;
                agent.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                agent = new Agent
                {
                    Id = driverId,
                    Name = persona?.Name ?? driverId,
                    Role = persona?.Role ?? "Autonomous AI Persona",
                    Status = "offline",
                    ApiKeyHash = hashed,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.Agents.Add(agent);
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Provisioned dedicated AgentMesh API token for persona @{PersonaId} (driver @{DriverId})", personaId, driverId);
            return rawToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to provision dedicated persona token: {Error}. Falling back to config master token.", ex.Message);
            return _config["AGENTMESH_TOKEN"] ?? _config["Auth:StaticApiKey"] ?? "mesh-default-secret-key";
        }
    }

    private async Task SeedHostCredentialsIfAvailableAsync(string containerName, CancellationToken ct)
    {
        if (_detectedCli == null) return;
        try
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var hostAgyToken = Path.Combine(userHome, ".gemini", "antigravity-cli", "antigravity-oauth-token");
            var hostAgyState = Path.Combine(userHome, ".gemini", "antigravity-cli", "antigravity_state.pbtxt");

            if (File.Exists(hostAgyToken))
            {
                var (chkExit, _, _) = await RunCliAsync(_detectedCli, $"exec {containerName} test -f /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
                if (chkExit != 0)
                {
                    await RunCliAsync(_detectedCli, $"exec {containerName} mkdir -p /root/.gemini/antigravity-cli", ct);
                    await RunCliAsync(_detectedCli, $"cp \"{hostAgyToken}\" {containerName}:/root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
                    if (File.Exists(hostAgyState))
                    {
                        await RunCliAsync(_detectedCli, $"cp \"{hostAgyState}\" {containerName}:/root/.gemini/antigravity-cli/antigravity_state.pbtxt", ct);
                    }
                    _logger.LogInformation("Seeded host Google AI Pro credentials into container {Name}", containerName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not seed host credentials into container {Name}: {Error}", containerName, ex.Message);
        }
    }

    public async Task StopAsync(string personaId, CancellationToken ct = default)
    {
        var containerName = GetContainerName(personaId);

        if (_dockerClient != null)
        {
            try
            {
                await _dockerClient.Containers.StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 5 }, ct);
                _logger.LogInformation("Stopped container {Name} for persona @{PersonaId}", containerName, personaId);
                return;
            }
            catch (DockerContainerNotFoundException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("DockerClient stop failed: {Error}", ex.Message);
            }
        }

        if (_detectedCli != null)
        {
            await RunCliAsync(_detectedCli, $"stop -t 5 {containerName}", ct);
            _logger.LogInformation("Stopped container {Name} via {Cli} for persona @{PersonaId}", containerName, _detectedCli, personaId);
        }
    }

    public async Task<string> GetLogsAsync(string personaId, int tailLines = 100, CancellationToken ct = default)
    {
        var containerName = GetContainerName(personaId);

        if (_dockerClient != null)
        {
            try
            {
                var logParams = new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Tail = tailLines.ToString()
                };

                using var stream = await _dockerClient.Containers.GetContainerLogsAsync(containerName, false, logParams, ct);
                var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct);
                return !string.IsNullOrWhiteSpace(stdout) ? stdout : stderr;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("DockerClient GetLogs failed for {Name}: {Error}", containerName, ex.Message);
            }
        }

        if (_detectedCli != null)
        {
            var (exitCode, stdout, stderr) = await RunCliAsync(_detectedCli, $"logs --tail {tailLines} {containerName}", ct);
            if (exitCode == 0)
            {
                return stdout;
            }
            return !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
        }

        return "No container logs available.";
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> ExecInContainerAsync(
        string personaId,
        string command,
        CancellationToken ct = default)
    {
        var containerName = GetContainerName(personaId);
        var cli = _detectedCli ?? "docker";
        var escaped = command.Replace("\"", "\\\"");
        return await RunCliAsync(cli, $"exec {containerName} sh -c \"{escaped}\"", ct);
    }

    public async Task<PersonaDriverVerificationResult> VerifyDriverAsync(string personaId, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        await EnsureRunningAsync(normPersonaId, ct);

        string driverId = "agy";
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
            var p = await db.Personas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == normPersonaId, ct);
            if (!string.IsNullOrWhiteSpace(p?.DriverId))
            {
                driverId = p.DriverId.Trim().ToLowerInvariant();
            }
        }
        catch { }

        var steps = new List<PersonaDriverVerificationStep>();

        // 1. Binary presence check
        string checkCmd = driverId.StartsWith("claude") ? "claude --version" :
                          driverId.StartsWith("copilot") ? "gh --version" :
                          driverId.StartsWith("openai") ? "echo 'OpenAI API Adapter'" :
                          "agy --version";

        var (binExit, binOut, binErr) = await ExecInContainerAsync(normPersonaId, checkCmd, ct);
        if (binExit == 0)
        {
            var ver = !string.IsNullOrWhiteSpace(binOut) ? binOut.Split('\n')[0].Trim() : "available";
            steps.Add(new PersonaDriverVerificationStep(
                "CLI Binary Check",
                "success",
                $"{driverId} binary detected in container ({ver})"));
        }
        else
        {
            steps.Add(new PersonaDriverVerificationStep(
                "CLI Binary Check",
                "failed",
                $"{driverId} binary not found in container: {binErr}"));
        }

        // 2. Authentication check
        if (driverId.StartsWith("agy"))
        {
            var (authExit, _, _) = await ExecInContainerAsync(normPersonaId, "test -f /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
            var (keyExit, keyOut, _) = await ExecInContainerAsync(normPersonaId, "printenv GEMINI_API_KEY", ct);
            if (authExit == 0)
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Authentication Check",
                    "success",
                    "Google AI Pro OAuth token detected at /root/.gemini/antigravity-cli/antigravity-oauth-token"));
            }
            else if (keyExit == 0 && !string.IsNullOrWhiteSpace(keyOut))
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Authentication Check",
                    "success",
                    "GEMINI_API_KEY detected in container environment"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Authentication Check",
                    "warning",
                    "Not authenticated with Google AI Pro. Click 'Authenticate Driver' in the UI to log in."));
            }
        }
        else if (driverId.StartsWith("claude"))
        {
            var (keyExit, keyOut, _) = await ExecInContainerAsync(normPersonaId, "printenv ANTHROPIC_API_KEY", ct);
            var (authExit, _, _) = await ExecInContainerAsync(normPersonaId, "test -d /root/.claude", ct);
            if (authExit == 0 || (keyExit == 0 && !string.IsNullOrWhiteSpace(keyOut)))
            {
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "success", "Claude Code credentials active"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "warning", "No Claude credentials found."));
            }
        }
        else
        {
            steps.Add(new PersonaDriverVerificationStep("Authentication Check", "success", "Driver adapter active"));
        }

        // 3. Mesh API Connectivity & Token Check
        var (curlExit, curlOut, curlErr) = await ExecInContainerAsync(normPersonaId, "curl -s -f -H \"Authorization: Bearer $AGENTMESH_TOKEN\" $AGENTMESH_URL/api/status", ct);
        if (curlExit == 0)
        {
            steps.Add(new PersonaDriverVerificationStep(
                "Mesh API Connectivity",
                "success",
                "Successfully authenticated with AgentMesh API using dedicated AGENTMESH_TOKEN"));
        }
        else
        {
            steps.Add(new PersonaDriverVerificationStep(
                "Mesh API Connectivity",
                "failed",
                $"API connectivity test failed (exit {curlExit}): {(!string.IsNullOrWhiteSpace(curlErr) ? curlErr : curlOut)}"));
        }

        // 4. Dry-run prompt test
        if (steps.All(s => s.Status != "failed"))
        {
            var dryRunCmd = driverId.StartsWith("agy") ? "timeout 15 agy -p \"hello\"" :
                            driverId.StartsWith("claude") ? "timeout 15 claude -p \"hello\"" :
                            "echo 'OK'";
            var (dryExit, dryOut, dryErr) = await ExecInContainerAsync(normPersonaId, dryRunCmd, ct);
            if (dryExit == 0)
            {
                var summary = !string.IsNullOrWhiteSpace(dryOut) ? dryOut.Split('\n')[0].Trim() : "completed";
                steps.Add(new PersonaDriverVerificationStep(
                    "Dry-Run Prompt Test",
                    "success",
                    $"Driver executed test prompt successfully: {summary}"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Dry-Run Prompt Test",
                    "warning",
                    $"Dry-run prompt exited with code {dryExit}: {(!string.IsNullOrWhiteSpace(dryErr) ? dryErr : dryOut)}"));
            }
        }

        var overallStatus = steps.Any(s => s.Status == "failed") ? "failed" :
                            steps.Any(s => s.Status == "warning") ? "warning" : "passed";

        return new PersonaDriverVerificationResult(normPersonaId, driverId, overallStatus, steps);
    }

    public async Task<PersonaAuthStartResult> StartAuthAsync(string personaId, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        await EnsureRunningAsync(normPersonaId, ct);

        // Check if already authenticated
        var (chkExit, _, _) = await ExecInContainerAsync(normPersonaId, "test -f /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
        if (chkExit == 0)
        {
            return new PersonaAuthStartResult(normPersonaId, "agy", "already_authenticated", Instructions: "Driver is already authenticated with Google AI Pro.");
        }

        if (_activeAuthSessions.TryGetValue(normPersonaId, out var existing) && !existing.Process.HasExited)
        {
            return new PersonaAuthStartResult(normPersonaId, "agy", "waiting_code", existing.AuthUrl, "Please open the Google authorization link, approve access, and paste the authorization code below.");
        }

        var containerName = GetContainerName(normPersonaId);
        var cli = _detectedCli ?? "docker";

        var psi = new ProcessStartInfo
        {
            FileName = cli,
            ArgumentList = { "exec", "-i", containerName, "agy", "-p", "auth" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var proc = new Process { StartInfo = psi };
        proc.Start();

        var tcsUrl = new TaskCompletionSource<string>();
        var readerTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                {
                    var match = Regex.Match(line, @"https://accounts\.google\.com/o/oauth2/auth[^\s]+");
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

        try
        {
            var completed = await Task.WhenAny(tcsUrl.Task, Task.Delay(10000, ct));
            if (completed == tcsUrl.Task)
            {
                var authUrl = await tcsUrl.Task;
                var session = new ActiveAuthSession(proc, proc.StandardInput, readerTask, authUrl);
                _activeAuthSessions[normPersonaId] = session;
                return new PersonaAuthStartResult(normPersonaId, "agy", "waiting_code", authUrl, "Click the link to sign in with your Google account, then paste the authorization code below.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to capture auth URL: {Error}", ex.Message);
        }

        return new PersonaAuthStartResult(normPersonaId, "agy", "error", ErrorMessage: "Could not initiate device-code login in container.");
    }

    public async Task<PersonaAuthSubmitResult> SubmitAuthCodeAsync(string personaId, string code, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();

        if (_activeAuthSessions.TryRemove(normPersonaId, out var session))
        {
            try
            {
                await session.Writer.WriteLineAsync(code.Trim());
                await session.Writer.FlushAsync();

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                await session.Process.WaitForExitAsync(linkedCts.Token);
                await session.DisposeAsync();

                var (chkExit, _, _) = await ExecInContainerAsync(normPersonaId, "test -f /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
                if (chkExit == 0)
                {
                    return new PersonaAuthSubmitResult(normPersonaId, "authenticated", "Successfully authenticated Google AI Pro credentials!");
                }

                return new PersonaAuthSubmitResult(normPersonaId, "failed", ErrorMessage: "Authorization code was submitted, but the token file was not created.");
            }
            catch (Exception ex)
            {
                return new PersonaAuthSubmitResult(normPersonaId, "failed", ErrorMessage: ex.Message);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        return new PersonaAuthSubmitResult(normPersonaId, "failed", ErrorMessage: "No active authentication session found for this persona. Please click Authenticate Driver first.");
    }

    private async Task EnsureImageAvailableAsync(CancellationToken ct)
    {
        if (_dockerClient != null)
        {
            try
            {
                var images = await _dockerClient.Images.ListImagesAsync(new ImagesListParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["reference"] = new Dictionary<string, bool> { [_defaultImage] = true }
                    }
                }, ct);

                if (images.Count > 0)
                {
                    return;
                }
            }
            catch { }
        }

        if (_detectedCli != null)
        {
            var (exitCode, outText, _) = await RunCliAsync(_detectedCli, $"image inspect {_defaultImage}", ct);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(outText))
            {
                return;
            }
        }

        // Auto-build image
        await _buildLock.WaitAsync(ct);
        try
        {
            _logger.LogInformation("Persona container image {Image} not found. Triggering automated build...", _defaultImage);
            var cli = _detectedCli ?? "docker";

            var repoRoot = FindRepoRoot();
            var dockerfilePath = Path.Combine(repoRoot, "deploy", "docker", "Dockerfile.persona");

            if (!File.Exists(dockerfilePath))
            {
                _logger.LogWarning("Dockerfile.persona not found at {Path}. Skipping auto-build.", dockerfilePath);
                return;
            }

            // Auto-stage host agy binary if present
            var stagedBinDir = Path.Combine(repoRoot, "deploy", "docker", "bin");
            Directory.CreateDirectory(stagedBinDir);
            var targetAgy = Path.Combine(stagedBinDir, "agy");
            if (!File.Exists(targetAgy))
            {
                var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var candidates = new[]
                {
                    Path.Combine(userHome, ".local", "bin", "agy"),
                    Path.Combine(userHome, ".gemini", "antigravity-cli", "bin", "agy"),
                    "/usr/local/bin/agy"
                };
                foreach (var src in candidates)
                {
                    if (File.Exists(src))
                    {
                        try
                        {
                            File.Copy(src, targetAgy, overwrite: true);
                            _logger.LogInformation("Staged host agy binary from {Src} to {Dst}", src, targetAgy);
                        }
                        catch { }
                        break;
                    }
                }
            }

            var (buildExit, buildOut, buildErr) = await RunCliAsync(
                cli,
                $"build -f \"{dockerfilePath}\" -t {_defaultImage} \"{repoRoot}\"",
                ct);

            if (buildExit != 0)
            {
                _logger.LogError("Automated build of {Image} failed: {Err}\n{Out}", _defaultImage, buildErr, buildOut);
                throw new InvalidOperationException($"Failed to build persona container image {_defaultImage}: {buildErr}");
            }

            _logger.LogInformation("Successfully built persona container image {Image}", _defaultImage);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    private static string FindRepoRoot()
    {
        var cwd = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(cwd);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentMesh.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "AgentMesh.sln")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Environment.CurrentDirectory;
    }

    private static string GetContainerName(string personaId) =>
        $"agentmesh-persona-{personaId.Trim().ToLowerInvariant()}";

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
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

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (proc.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private sealed class ActiveAuthSession(Process process, StreamWriter writer, Task readerTask, string authUrl) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        public StreamWriter Writer { get; } = writer;
        public Task ReaderTask { get; } = readerTask;
        public string AuthUrl { get; } = authUrl;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(true);
                }
            }
            catch { }
            try { Writer.Dispose(); } catch { }
            try { Process.Dispose(); } catch { }
        }
    }
}
