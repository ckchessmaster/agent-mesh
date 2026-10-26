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
                var currentImage = inspect.Config?.Image ?? inspect.Image;
                var updateAvailable = !string.IsNullOrWhiteSpace(currentImage) &&
                                      !string.Equals(currentImage, _defaultImage, StringComparison.OrdinalIgnoreCase);

                return new PersonaEnvironmentStatus(
                    PersonaId: personaId,
                    Status: isRunning ? "running" : "stopped",
                    Runtime: _runtimeName,
                    ContainerId: inspect.ID[..Math.Min(12, inspect.ID.Length)],
                    StartedAt: startedAt,
                    LastActivityAt: DateTimeOffset.UtcNow,
                    Image: currentImage,
                    ConfiguredImage: _defaultImage,
                    UpdateAvailable: updateAvailable);
            }
            catch (DockerContainerNotFoundException)
            {
                return new PersonaEnvironmentStatus(personaId, "stopped", _runtimeName, ConfiguredImage: _defaultImage);
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

                        string? currentImage = null;
                        if (elem.TryGetProperty("Config", out var cfg) && cfg.TryGetProperty("Image", out var imgP))
                        {
                            currentImage = imgP.GetString();
                        }
                        var updateAvailable = !string.IsNullOrWhiteSpace(currentImage) &&
                                              !string.Equals(currentImage, _defaultImage, StringComparison.OrdinalIgnoreCase);

                        return new PersonaEnvironmentStatus(
                            PersonaId: personaId,
                            Status: running ? "running" : "stopped",
                            Runtime: _runtimeName,
                            ContainerId: id != null ? id[..Math.Min(12, id.Length)] : null,
                            StartedAt: started,
                            LastActivityAt: DateTimeOffset.UtcNow,
                            Image: currentImage,
                            ConfiguredImage: _defaultImage,
                            UpdateAvailable: updateAvailable);
                    }
                }
                catch { }
            }
        }

        return new PersonaEnvironmentStatus(personaId, "stopped", _runtimeName, ConfiguredImage: _defaultImage);
    }

    public async Task EnsureRunningAsync(string personaId, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        var status = await GetStatusAsync(normPersonaId, ct);
        var containerName = GetContainerName(normPersonaId);
        var sidecarConfig = await GetPersonaSidecarConfigAsync(normPersonaId, ct);

        if (status.Status == "running")
        {
            if (status.UpdateAvailable)
            {
                _logger.LogInformation("Container for @{PersonaId} is running outdated image {Image} (configured: {Configured}). Recreating...",
                    normPersonaId, status.Image, status.ConfiguredImage);
                await RestartAsync(normPersonaId, ct);
                return;
            }
            await SeedHostCredentialsIfAvailableAsync(containerName, ct);
            await EnsureSidecarRunningAsync(normPersonaId, containerName, sidecarConfig, ct);
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

        if (sidecarConfig != null && sidecarConfig.Enabled)
        {
            envList.Add("SIDECAR_ENABLED=true");
            envList.Add($"SIDECAR_TYPE={sidecarConfig.Type}");
            envList.Add($"SIDECAR_USERSPACE={(sidecarConfig.UserspaceMode ? "true" : "false")}");
        }

        var forwardedVars = new[]
        {
            "OPENAI_API_KEY",
            "OPENAI_BASE_URL",
            "ANTHROPIC_API_KEY",
            "GEMINI_API_KEY"
        };

        foreach (var v in forwardedVars)
        {
            var val = _config[v] ?? Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrWhiteSpace(val))
            {
                envList.Add($"{v}={val}");
            }
        }

        // Resolve per-persona GitHub credentials & Git identity with strict isolation
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
            var encService = scope.ServiceProvider.GetService<ICredentialEncryptionService>();
            var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normPersonaId, ct);
            if (persona != null)
            {
                if (!string.IsNullOrWhiteSpace(persona.GitHubTokenEncrypted) && encService != null)
                {
                    var token = encService.Decrypt(persona.GitHubTokenEncrypted);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        envList.Add($"GITHUB_TOKEN={token}");
                        envList.Add($"GH_TOKEN={token}");
                    }
                }

                var authorName = !string.IsNullOrWhiteSpace(persona.GitAuthorName) ? persona.GitAuthorName : persona.Name;
                if (!string.IsNullOrWhiteSpace(authorName))
                {
                    envList.Add($"GIT_AUTHOR_NAME={authorName}");
                    envList.Add($"GIT_COMMITTER_NAME={authorName}");
                }

                var authorEmail = !string.IsNullOrWhiteSpace(persona.GitAuthorEmail) ? persona.GitAuthorEmail : $"{normPersonaId}-bot@agentmesh.local";
                if (!string.IsNullOrWhiteSpace(authorEmail))
                {
                    envList.Add($"GIT_AUTHOR_EMAIL={authorEmail}");
                    envList.Add($"GIT_COMMITTER_EMAIL={authorEmail}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to resolve persona credentials for container {PersonaId}: {Error}", normPersonaId, ex.Message);
        }

        if (_dockerClient != null)
        {
            try
            {
                try
                {
                    var inspect = await _dockerClient.Containers.InspectContainerAsync(containerName, ct);
                    var existingImage = inspect.Config?.Image ?? inspect.Image;
                    var isOutdated = !string.IsNullOrWhiteSpace(existingImage) &&
                                     !string.Equals(existingImage, _defaultImage, StringComparison.OrdinalIgnoreCase);

                    if (isOutdated)
                    {
                        _logger.LogInformation("Removing outdated container {Name} with image {Image} (configured: {Target})",
                            containerName, existingImage, _defaultImage);
                        if (inspect.State.Running)
                        {
                            try { await _dockerClient.Containers.StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 3 }, ct); } catch { }
                        }
                        await _dockerClient.Containers.RemoveContainerAsync(containerName, new ContainerRemoveParameters { Force = true }, ct);
                    }
                    else if (!inspect.State.Running)
                    {
                        _logger.LogInformation("Starting existing container {Name} for persona @{PersonaId}", containerName, normPersonaId);
                        await _dockerClient.Containers.StartContainerAsync(containerName, new ContainerStartParameters(), ct);
                        await SeedHostCredentialsIfAvailableAsync(containerName, ct);
                        await EnsureSidecarRunningAsync(normPersonaId, containerName, sidecarConfig, ct);
                        return;
                    }
                    else
                    {
                        await SeedHostCredentialsIfAvailableAsync(containerName, ct);
                        await EnsureSidecarRunningAsync(normPersonaId, containerName, sidecarConfig, ct);
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
                await EnsureSidecarRunningAsync(normPersonaId, containerName, sidecarConfig, ct);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("DockerClient creation failed, attempting CLI fallback: {Error}", ex.Message);
            }
        }

        if (_detectedCli != null)
        {
            var (inspectExit, inspectJson, _) = await RunCliAsync(_detectedCli, $"inspect {containerName} --format json", ct);
            if (inspectExit == 0 && !string.IsNullOrWhiteSpace(inspectJson))
            {
                string? existingImg = null;
                try
                {
                    using var doc = JsonDocument.Parse(inspectJson);
                    var elem = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : doc.RootElement;
                    if (elem.TryGetProperty("Config", out var cfg) && cfg.TryGetProperty("Image", out var imgP))
                    {
                        existingImg = imgP.GetString();
                    }
                }
                catch { }

                var isOutdated = !string.IsNullOrWhiteSpace(existingImg) &&
                                 !string.Equals(existingImg, _defaultImage, StringComparison.OrdinalIgnoreCase);

                if (isOutdated)
                {
                    _logger.LogInformation("Removing outdated container {Name} ({Existing}) via {Cli}", containerName, existingImg, _detectedCli);
                    await RunCliAsync(_detectedCli, $"rm -f {containerName}", ct);
                }
                else
                {
                    _logger.LogInformation("Starting existing container {Name} via {Cli} for @{PersonaId}", containerName, _detectedCli, normPersonaId);
                    var (startExit, _, startErr) = await RunCliAsync(_detectedCli, $"start {containerName}", ct);
                    if (startExit != 0)
                    {
                        throw new InvalidOperationException($"Failed to start container {containerName}: {startErr}");
                    }
                    await SeedHostCredentialsIfAvailableAsync(containerName, ct);
                    await EnsureSidecarRunningAsync(normPersonaId, containerName, sidecarConfig, ct);
                    return;
                }
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
            await EnsureSidecarRunningAsync(normPersonaId, containerName, sidecarConfig, ct);
            return;
        }

        throw new InvalidOperationException($"No active Docker/Podman socket or CLI runtime available to launch persona container for @{personaId}.");
    }

    private async Task<string> ProvisionAgentTokenAsync(string personaId, CancellationToken ct)
    {
        var normPersonaId = personaId.Trim().TrimStart('@').ToLowerInvariant();
        var tokensDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentmesh", "tokens");
        var tokenFile = Path.Combine(tokensDir, $"{normPersonaId}.token");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
            var apiKeyService = scope.ServiceProvider.GetRequiredService<IApiKeyService>();

            var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normPersonaId, ct);
            var rawDriverId = !string.IsNullOrWhiteSpace(persona?.DriverId) ? persona.DriverId : normPersonaId;
            var driverId = rawDriverId.Trim().TrimStart('@').ToLowerInvariant();

            string? existingToken = null;
            if (File.Exists(tokenFile))
            {
                try
                {
                    existingToken = File.ReadAllText(tokenFile).Trim();
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(existingToken))
            {
                var existingHash = apiKeyService.HashToken(existingToken);
                await SyncAgentTokenHashAsync(db, driverId, normPersonaId, persona?.Name, persona?.Role, existingHash, ct);
                _logger.LogInformation("Reused existing AgentMesh API token for persona @{PersonaId} (driver @{DriverId})", normPersonaId, driverId);
                return existingToken;
            }

            var rawToken = apiKeyService.GenerateToken();
            var hashed = apiKeyService.HashToken(rawToken);

            await SyncAgentTokenHashAsync(db, driverId, normPersonaId, persona?.Name, persona?.Role, hashed, ct);

            try
            {
                Directory.CreateDirectory(tokensDir);
                File.WriteAllText(tokenFile, rawToken);
            }
            catch { }

            _logger.LogInformation("Provisioned dedicated AgentMesh API token for persona @{PersonaId} (driver @{DriverId})", normPersonaId, driverId);
            return rawToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to provision dedicated persona token: {Error}. Falling back to config master token.", ex.Message);
            return _config["AGENTMESH_TOKEN"] ?? _config["Auth:StaticApiKey"] ?? "mesh-default-secret-key";
        }
    }

    private static async Task SyncAgentTokenHashAsync(
        AgentMeshDbContext db,
        string driverId,
        string personaId,
        string? personaName,
        string? personaRole,
        string hash,
        CancellationToken ct)
    {
        var targetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { driverId, personaId };
        var agents = await db.Agents.Where(a => targetIds.Contains(a.Id)).ToListAsync(ct);

        foreach (var id in targetIds)
        {
            var agent = agents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            if (agent != null)
            {
                if (agent.ApiKeyHash != hash)
                {
                    agent.ApiKeyHash = hash;
                    agent.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            else
            {
                var name = (id == personaId ? personaName : null) ?? id;
                var role = (id == personaId ? personaRole : null) ?? "Autonomous Driver";
                db.Agents.Add(new Agent
                {
                    Id = id,
                    Name = name,
                    Role = role,
                    Status = "offline",
                    ApiKeyHash = hash,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedHostCredentialsIfAvailableAsync(string containerName, CancellationToken ct)
    {
        if (_detectedCli == null) return;
        try
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var hostAgyToken = Path.Combine(userHome, ".gemini", "antigravity-cli", "antigravity-oauth-token");
            var hostAgyState = Path.Combine(userHome, ".gemini", "antigravity-cli", "antigravity_state.pbtxt");

            // Check persona-specific cached oauth token
            var personaNormId = containerName.Replace("agentmesh-persona-", "");
            var personaOauthFile = Path.Combine(userHome, ".agentmesh", "tokens", $"{personaNormId}.oauth");
            var sourceToken = File.Exists(personaOauthFile) ? personaOauthFile : (File.Exists(hostAgyToken) ? hostAgyToken : null);

            if (!string.IsNullOrEmpty(sourceToken))
            {
                var (chkExit, _, _) = await RunCliAsync(_detectedCli, $"exec {containerName} test -f /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
                if (chkExit != 0)
                {
                    await RunCliAsync(_detectedCli, $"exec {containerName} mkdir -p /root/.gemini/antigravity-cli", ct);
                    await RunCliAsync(_detectedCli, $"cp \"{sourceToken}\" {containerName}:/root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
                    if (File.Exists(hostAgyState))
                    {
                        await RunCliAsync(_detectedCli, $"cp \"{hostAgyState}\" {containerName}:/root/.gemini/antigravity-cli/antigravity_state.pbtxt", ct);
                    }
                    _logger.LogInformation("Seeded Google AI Pro credentials into container {Name}", containerName);
                }
            }

            await SeedPersonaGitHubCredentialsAsync(containerName, personaNormId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not seed host credentials into container {Name}: {Error}", containerName, ex.Message);
        }
    }

    private async Task SeedPersonaGitHubCredentialsAsync(string containerName, string normPersonaId, CancellationToken ct)
    {
        if (_detectedCli == null) return;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
            var encService = scope.ServiceProvider.GetService<ICredentialEncryptionService>();
            var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normPersonaId, ct);
            if (persona == null) return;

            string? token = null;
            if (!string.IsNullOrWhiteSpace(persona.GitHubTokenEncrypted) && encService != null)
            {
                token = encService.Decrypt(persona.GitHubTokenEncrypted);
            }

            var authorName = !string.IsNullOrWhiteSpace(persona.GitAuthorName) ? persona.GitAuthorName : persona.Name;
            var authorEmail = !string.IsNullOrWhiteSpace(persona.GitAuthorEmail) ? persona.GitAuthorEmail : $"{normPersonaId}-bot@agentmesh.local";

            if (!string.IsNullOrWhiteSpace(token))
            {
                var escapedToken = token.Trim().Replace("\"", "\\\"").Replace("$", "\\$");
                var escapedName = authorName.Trim().Replace("\"", "\\\"");
                var escapedEmail = authorEmail.Trim().Replace("\"", "\\\"");

                var setupCmd = $"mkdir -p /root/.config/gh && printf 'github.com:\\n  oauth_token: %s\\n  git_protocol: https\\n' \"{escapedToken}\" > /root/.config/gh/hosts.yml && chmod 600 /root/.config/gh/hosts.yml && git config --global user.name \"{escapedName}\" && git config --global user.email \"{escapedEmail}\" && (gh auth setup-git 2>/dev/null || true)";
                await ExecInContainerAsync(normPersonaId, setupCmd, ct);
                _logger.LogInformation("Seeded GitHub credentials and git config into container {Name} for @{PersonaId}", containerName, normPersonaId);
            }
            else
            {
                // Strict isolation: ensure no stale credentials exist
                var clearCmd = "rm -f /root/.config/gh/hosts.yml && (git config --global --unset-all credential.helper 2>/dev/null || true)";
                await ExecInContainerAsync(normPersonaId, clearCmd, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not seed GitHub credentials into container {Name}: {Error}", containerName, ex.Message);
        }
    }

    public async Task StopAsync(string personaId, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        var containerName = GetContainerName(normPersonaId);
        var sidecarName = $"{containerName}-sidecar";

        if (_dockerClient != null)
        {
            try
            {
                try { await _dockerClient.Containers.StopContainerAsync(sidecarName, new ContainerStopParameters { WaitBeforeKillSeconds = 2 }, ct); } catch { }
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
            try { await RunCliAsync(_detectedCli, $"stop -t 2 {sidecarName}", ct); } catch { }
            await RunCliAsync(_detectedCli, $"stop -t 5 {containerName}", ct);
            _logger.LogInformation("Stopped container {Name} via {Cli} for persona @{PersonaId}", containerName, _detectedCli, personaId);
        }
    }

    public async Task RestartAsync(string personaId, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        var containerName = GetContainerName(normPersonaId);
        var sidecarName = $"{containerName}-sidecar";
        _logger.LogInformation("Restarting & recreating container {Name} for persona @{PersonaId}", containerName, normPersonaId);

        if (_dockerClient != null)
        {
            try
            {
                try { await _dockerClient.Containers.StopContainerAsync(sidecarName, new ContainerStopParameters { WaitBeforeKillSeconds = 2 }, ct); } catch { }
                try { await _dockerClient.Containers.RemoveContainerAsync(sidecarName, new ContainerRemoveParameters { Force = true }, ct); } catch { }

                try
                {
                    await _dockerClient.Containers.StopContainerAsync(containerName, new ContainerStopParameters { WaitBeforeKillSeconds = 3 }, ct);
                }
                catch { }

                await _dockerClient.Containers.RemoveContainerAsync(containerName, new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (DockerContainerNotFoundException) { }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to remove container {Name} during restart: {Error}", containerName, ex.Message);
            }
        }
        else if (_detectedCli != null)
        {
            try { await RunCliAsync(_detectedCli, $"rm -f {sidecarName}", ct); } catch { }
            await RunCliAsync(_detectedCli, $"rm -f {containerName}", ct);
        }

        await EnsureRunningAsync(normPersonaId, ct);
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
            var combinedErr = $"{binErr} {binOut}".Trim();
            var isPclmul = combinedErr.Contains("pclmul", StringComparison.OrdinalIgnoreCase) ||
                           combinedErr.Contains("Illegal instruction", StringComparison.OrdinalIgnoreCase) ||
                           binExit == 132;

            var errorDetails = isPclmul
                ? "Processor is missing PCLMULQDQ instruction required by Google agy binary. In Proxmox VE, edit VM Hardware -> Processor -> Type and set to 'host' (then reboot VM). Alternatively, switch to Direct API Key mode."
                : $"{driverId} binary check failed (exit {binExit}): {binErr}";

            steps.Add(new PersonaDriverVerificationStep(
                "CLI Binary Check",
                "failed",
                errorDetails));
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
            if (!string.IsNullOrWhiteSpace(existing.AuthUrl))
            {
                return new PersonaAuthStartResult(normPersonaId, "agy", "waiting_code", existing.AuthUrl, "Please open the Google authorization link, approve access, and paste the authorization code below.");
            }
            _activeAuthSessions.TryRemove(normPersonaId, out _);
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

                var (chkExit, outStr, _) = await ExecInContainerAsync(normPersonaId, "cat /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
                if (chkExit == 0 && !string.IsNullOrWhiteSpace(outStr))
                {
                    try
                    {
                        var tokensDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentmesh", "tokens");
                        Directory.CreateDirectory(tokensDir);
                        var oauthFile = Path.Combine(tokensDir, $"{normPersonaId}.oauth");
                        File.WriteAllText(oauthFile, outStr.Trim());
                    }
                    catch { }
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

    public async Task<PersonaAuthSubmitResult> ConfigureApiKeyAsync(
        string personaId,
        string apiKey,
        string? provider = null,
        CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        await EnsureRunningAsync(normPersonaId, ct);

        var keyName = (!string.IsNullOrWhiteSpace(provider) && provider.ToLowerInvariant().Contains("claude"))
            ? "ANTHROPIC_API_KEY"
            : "GEMINI_API_KEY";

        var escapedKey = apiKey.Trim().Replace("\"", "\\\"");
        await ExecInContainerAsync(normPersonaId, $"echo 'export {keyName}=\"{escapedKey}\"' >> /root/.bashrc", ct);
        await ExecInContainerAsync(normPersonaId, $"echo 'export {keyName}=\"{escapedKey}\"' >> /root/.profile", ct);

        return new PersonaAuthSubmitResult(normPersonaId, "authenticated", $"{keyName} successfully configured in persona environment!");
    }

    public async Task<IPersonaTerminalSession> AttachTerminalAsync(
        string personaId,
        int cols,
        int rows,
        Func<string, Task> onOutput,
        Func<int, Task> onClosed,
        CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        await EnsureRunningAsync(normPersonaId, ct);

        var containerName = GetContainerName(normPersonaId);
        var cli = _detectedCli ?? "docker";

        var psi = new ProcessStartInfo
        {
            FileName = cli,
            ArgumentList = { "exec", "-i", "-e", $"COLUMNS={cols}", "-e", $"LINES={rows}", "-e", "TERM=xterm-256color", containerName, "/bin/bash" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var proc = new Process { StartInfo = psi };
        proc.Start();

        var session = new DockerProcessTerminalSession(proc, proc.StandardInput);

        _ = Task.Run(async () =>
        {
            var buffer = new char[1024];
            try
            {
                while (!proc.HasExited)
                {
                    var read = await proc.StandardOutput.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    var text = new string(buffer, 0, read);
                    await onOutput(text);
                }
            }
            catch { }
            finally
            {
                await onOutput("\r\n\x1b[33mTerminal session closed.\x1b[0m\r\n");
                await onClosed(proc.ExitCode);
            }
        });

        _ = Task.Run(async () =>
        {
            var errBuffer = new char[1024];
            try
            {
                while (!proc.HasExited)
                {
                    var read = await proc.StandardError.ReadAsync(errBuffer, 0, errBuffer.Length);
                    if (read <= 0) break;
                    var text = new string(errBuffer, 0, read);
                    await onOutput(text);
                }
            }
            catch { }
        });

        return session;
    }

    private sealed class DockerProcessTerminalSession(Process process, StreamWriter writer) : IPersonaTerminalSession
    {
        public async ValueTask SendInputAsync(string data)
        {
            try
            {
                if (!process.HasExited)
                {
                    await writer.WriteAsync(data);
                    await writer.FlushAsync();
                }
            }
            catch { }
        }

        public async ValueTask ResizeAsync(int cols, int rows)
        {
            await ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch { }
            try { writer.Dispose(); } catch { }
            try { process.Dispose(); } catch { }
        }
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

    public async Task<DriverEndpointTestResult> TestDriverEndpointAsync(
        string endpoint,
        string? apiKey,
        DriverSidecarConfig? sidecar,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new DriverEndpointTestResult(false, 400, null, "Endpoint URL is required.");
        }

        var baseEndpoint = endpoint.Trim().TrimEnd('/');
        var modelsUrl = baseEndpoint.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
            ? baseEndpoint
            : $"{baseEndpoint}/models";

        // If sidecar is not enabled, perform direct HTTP probe
        if (sidecar == null || !sidecar.Enabled)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
                }

                using var res = await client.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                var models = ParseModelsFromBody(body);
                return new DriverEndpointTestResult(res.IsSuccessStatusCode, (int)res.StatusCode, body, res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode}: {res.ReasonPhrase}", models);
            }
            catch (Exception ex)
            {
                return new DriverEndpointTestResult(false, 0, null, $"Connection failed: {ex.Message}");
            }
        }

        // With docker/cli runtime
        if (!string.IsNullOrWhiteSpace(_detectedCli))
        {
            var sidecarName = $"mesh-test-sidecar-{Guid.NewGuid():N}"[..24];
            try
            {
                var sidecarImage = !string.IsNullOrWhiteSpace(sidecar.Image) ? sidecar.Image :
                    string.Equals(sidecar.Type, "tailscale", StringComparison.OrdinalIgnoreCase) ? "tailscale/tailscale:v1.78.1" : "alpine:latest";

                var sb = new StringBuilder($"run -d --name {sidecarName} --rm ");
                if (string.Equals(sidecar.Type, "tailscale", StringComparison.OrdinalIgnoreCase))
                {
                    if (!sidecar.UserspaceMode)
                    {
                        sb.Append("--cap-add=NET_ADMIN --device=/dev/net/tun ");
                    }
                    if (!string.IsNullOrWhiteSpace(sidecar.AuthKey))
                    {
                        sb.Append($"-e \"TS_AUTHKEY={sidecar.AuthKey}\" ");
                    }
                    sb.Append($"-e \"TS_EXTRA_ARGS=--ephemeral --hostname=mesh-probe\" ");
                    sb.Append($"-e \"TS_USERSPACE={(sidecar.UserspaceMode ? "true" : "false")}\" ");
                }
                sb.Append(sidecarImage);
                await RunCliAsync(_detectedCli, sb.ToString(), ct);

                var curlCmd = !string.IsNullOrWhiteSpace(apiKey)
                    ? $"curl -s -f -m 10 -H 'Authorization: Bearer {apiKey.Trim()}' {modelsUrl}"
                    : $"curl -s -f -m 10 {modelsUrl}";

                var probeArgs = $"run --rm --net=container:{sidecarName} curlimages/curl:latest sh -c \"sleep 3 && {curlCmd}\"";
                var (probeExit, probeOut, probeErr) = await RunCliAsync(_detectedCli, probeArgs, ct);
                var success = probeExit == 0;
                var models = ParseModelsFromBody(probeOut);
                return new DriverEndpointTestResult(success, success ? 200 : 500, probeOut, success ? null : probeErr, models);
            }
            catch (Exception ex)
            {
                return new DriverEndpointTestResult(false, 0, null, $"Docker sidecar probe failed: {ex.Message}");
            }
            finally
            {
                try { await RunCliAsync(_detectedCli, $"rm -f {sidecarName}", CancellationToken.None); } catch { }
            }
        }

        return new DriverEndpointTestResult(false, 0, null, "No container runtime available for sidecar probe.");
    }

    private static List<string>? ParseModelsFromBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in dataArr.EnumerateArray())
                {
                    if (el.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
                    }
                }
                return list;
            }
        }
        catch { }
        return null;
    }

    private async Task<DriverSidecarConfig?> GetPersonaSidecarConfigAsync(string normPersonaId, CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
            var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normPersonaId, ct);
            if (persona == null) return null;
            var driverId = !string.IsNullOrWhiteSpace(persona.DriverId) ? persona.DriverId.Trim().TrimStart('@').ToLowerInvariant() : null;
            if (string.IsNullOrWhiteSpace(driverId)) return null;

            var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == driverId, ct);
            if (agent == null || string.IsNullOrWhiteSpace(agent.CapabilitiesJson)) return null;

            using var doc = JsonDocument.Parse(agent.CapabilitiesJson);
            if (doc.RootElement.TryGetProperty("sidecar", out var sc) && sc.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<DriverSidecarConfig>(sc.GetRawText());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to resolve driver sidecar config for persona @{PersonaId}: {Error}", normPersonaId, ex.Message);
        }
        return null;
    }

    private async Task EnsureSidecarRunningAsync(
        string normPersonaId,
        string runnerContainerName,
        DriverSidecarConfig? sidecar,
        CancellationToken ct)
    {
        var sidecarContainerName = $"{runnerContainerName}-sidecar";

        if (sidecar == null || !sidecar.Enabled)
        {
            if (_dockerClient != null)
            {
                try
                {
                    await _dockerClient.Containers.StopContainerAsync(sidecarContainerName, new ContainerStopParameters { WaitBeforeKillSeconds = 2 }, ct);
                    await _dockerClient.Containers.RemoveContainerAsync(sidecarContainerName, new ContainerRemoveParameters { Force = true }, ct);
                }
                catch { }
            }
            else if (_detectedCli != null)
            {
                try { await RunCliAsync(_detectedCli, $"rm -f {sidecarContainerName}", ct); } catch { }
            }
            return;
        }

        var isTailscale = string.Equals(sidecar.Type, "tailscale", StringComparison.OrdinalIgnoreCase);
        var sidecarImage = !string.IsNullOrWhiteSpace(sidecar.Image) ? sidecar.Image :
            isTailscale ? "tailscale/tailscale:v1.78.1" : "alpine:latest";

        var sidecarEnv = new List<string>();
        if (isTailscale)
        {
            var hostname = !string.IsNullOrWhiteSpace(sidecar.Hostname) ? sidecar.Hostname : $"mesh-{normPersonaId}";
            var extraArgsSb = new StringBuilder($"--ephemeral --hostname={hostname}");
            if (!string.IsNullOrWhiteSpace(sidecar.Tags))
            {
                extraArgsSb.Append($" --advertise-tags={sidecar.Tags}");
            }
            if (!string.IsNullOrWhiteSpace(sidecar.ExtraArgs))
            {
                extraArgsSb.Append($" {sidecar.ExtraArgs}");
            }

            sidecarEnv.Add($"TS_EXTRA_ARGS={extraArgsSb.ToString().Trim()}");
            sidecarEnv.Add($"TS_USERSPACE={(sidecar.UserspaceMode ? "true" : "false")}");
            if (!string.IsNullOrWhiteSpace(sidecar.AuthKey))
            {
                sidecarEnv.Add($"TS_AUTHKEY={sidecar.AuthKey}");
            }
        }
        else if (sidecar.EnvironmentVariables != null)
        {
            foreach (var (k, v) in sidecar.EnvironmentVariables)
            {
                sidecarEnv.Add($"{k}={v}");
            }
        }

        if (_dockerClient != null)
        {
            try
            {
                try
                {
                    var inspect = await _dockerClient.Containers.InspectContainerAsync(sidecarContainerName, ct);
                    if (!inspect.State.Running)
                    {
                        await _dockerClient.Containers.StartContainerAsync(sidecarContainerName, new ContainerStartParameters(), ct);
                    }
                    return;
                }
                catch (DockerContainerNotFoundException) { }

                var binds = new List<string>();
                var capAdd = new List<string>();
                if (isTailscale && !sidecar.UserspaceMode)
                {
                    capAdd.Add("NET_ADMIN");
                    binds.Add("/dev/net/tun:/dev/net/tun");
                }

                var createParams = new CreateContainerParameters
                {
                    Image = sidecarImage,
                    Name = sidecarContainerName,
                    Env = sidecarEnv,
                    Cmd = !string.IsNullOrWhiteSpace(sidecar.Command) ? new List<string> { sidecar.Command } : sidecar.Arguments,
                    HostConfig = new HostConfig
                    {
                        NetworkMode = $"container:{runnerContainerName}",
                        CapAdd = capAdd.Count > 0 ? capAdd : null,
                        Binds = binds.Count > 0 ? binds : null,
                        RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
                    }
                };

                var res = await _dockerClient.Containers.CreateContainerAsync(createParams, ct);
                await _dockerClient.Containers.StartContainerAsync(res.ID, new ContainerStartParameters(), ct);
                _logger.LogInformation("Sidecar container {Name} started for @{PersonaId}", sidecarContainerName, normPersonaId);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("DockerClient sidecar launch failed, falling back to CLI: {Error}", ex.Message);
            }
        }

        if (_detectedCli != null)
        {
            var (inspectExit, _, _) = await RunCliAsync(_detectedCli, $"inspect {sidecarContainerName}", ct);
            if (inspectExit == 0)
            {
                await RunCliAsync(_detectedCli, $"start {sidecarContainerName}", ct);
                return;
            }

            var sb = new StringBuilder($"run -d --name {sidecarContainerName} --net=container:{runnerContainerName} ");
            if (isTailscale)
            {
                if (!sidecar.UserspaceMode)
                {
                    sb.Append("--cap-add=NET_ADMIN --device=/dev/net/tun ");
                }
                foreach (var e in sidecarEnv)
                {
                    var parts = e.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        sb.Append($"-e \"{parts[0]}={parts[1].Replace("\"", "\\\"")}\" ");
                    }
                }
            }
            else
            {
                foreach (var e in sidecarEnv)
                {
                    var parts = e.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        sb.Append($"-e \"{parts[0]}={parts[1].Replace("\"", "\\\"")}\" ");
                    }
                }
            }

            sb.Append(sidecarImage);
            if (!string.IsNullOrWhiteSpace(sidecar.Command))
            {
                sb.Append($" {sidecar.Command}");
            }
            if (sidecar.Arguments != null)
            {
                foreach (var a in sidecar.Arguments)
                {
                    sb.Append($" \"{a.Replace("\"", "\\\"")}\"");
                }
            }

            var (runExit, _, runErr) = await RunCliAsync(_detectedCli, sb.ToString(), ct);
            if (runExit != 0)
            {
                _logger.LogWarning("Failed to launch sidecar container via {Cli}: {Error}", _detectedCli, runErr);
            }
            else
            {
                _logger.LogInformation("Sidecar container {Name} launched via {Cli} for @{PersonaId}", sidecarContainerName, _detectedCli, normPersonaId);
            }
        }
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
