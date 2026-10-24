using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Infrastructure.Containers;

public class DockerPersonaContainerManager : IPersonaContainerManager
{
    private readonly ILogger<DockerPersonaContainerManager> _logger;
    private readonly IConfiguration _config;
    private readonly DockerClient? _dockerClient;
    private readonly string _runtimeName;
    private readonly string? _detectedCli;
    private readonly string _defaultImage;
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    public string RuntimeName => _runtimeName;

    public DockerPersonaContainerManager(
        ILogger<DockerPersonaContainerManager> logger,
        IConfiguration config)
    {
        _logger = logger;
        _config = config;
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
            // Probe Linux / macOS sockets
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

        // Fall back to CLI
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

        // CLI fallback
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
        var status = await GetStatusAsync(personaId, ct);
        if (status.Status == "running")
        {
            return;
        }

        var containerName = GetContainerName(personaId);
        var volumeName = $"agentmesh-persona-{personaId.Trim().ToLowerInvariant()}-data";

        await EnsureImageAvailableAsync(ct);

        var meshUrl = _config["AGENTMESH_URL"] ??
                      _config["PersonaContainer:MeshUrl"] ??
                      "http://host.docker.internal:5000";

        var token = _config["AGENTMESH_TOKEN"] ??
                    _config["MasterApiKey"] ??
                    "";

        var envList = new List<string>
        {
            $"AGENTMESH_URL={meshUrl}",
            $"AGENTMESH_TOKEN={token}",
            $"AGENTMESH_PERSONA_ID={personaId.Trim().ToLowerInvariant()}"
        };

        // Forward LLM secrets if present in host environment
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
                // Check if container exists in stopped state
                try
                {
                    var inspect = await _dockerClient.Containers.InspectContainerAsync(containerName, ct);
                    if (!inspect.State.Running)
                    {
                        _logger.LogInformation("Starting existing container {Name} for persona @{PersonaId}", containerName, personaId);
                        await _dockerClient.Containers.StartContainerAsync(containerName, new ContainerStartParameters(), ct);
                        return;
                    }
                }
                catch (DockerContainerNotFoundException)
                {
                    // Container needs to be created
                }

                _logger.LogInformation("Creating new container {Name} for persona @{PersonaId} from image {Image}", containerName, personaId, _defaultImage);

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
                        Binds = new List<string> { $"{volumeName}:/workspace" },
                        RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
                    }
                };

                var createRes = await _dockerClient.Containers.CreateContainerAsync(createParams, ct);
                await _dockerClient.Containers.StartContainerAsync(createRes.ID, new ContainerStartParameters(), ct);
                _logger.LogInformation("Container {Name} ({ID}) started successfully for @{PersonaId}", containerName, createRes.ID[..12], personaId);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("DockerClient creation failed, attempting CLI fallback: {Error}", ex.Message);
            }
        }

        // CLI fallback
        if (_detectedCli != null)
        {
            // Check if container already exists
            var (inspectExit, _, _) = await RunCliAsync(_detectedCli, $"inspect {containerName}", ct);
            if (inspectExit == 0)
            {
                _logger.LogInformation("Starting existing container {Name} via {Cli} for @{PersonaId}", containerName, _detectedCli, personaId);
                var (startExit, _, startErr) = await RunCliAsync(_detectedCli, $"start {containerName}", ct);
                if (startExit != 0)
                {
                    throw new InvalidOperationException($"Failed to start container {containerName}: {startErr}");
                }
                return;
            }

            // Create and run new container
            var sb = new StringBuilder();
            sb.Append($"run -d --name {containerName} ");
            sb.Append($"--add-host host.docker.internal:host-gateway ");
            sb.Append($"--add-host host.containers.internal:host-gateway ");
            sb.Append($"-v {volumeName}:/workspace ");

            foreach (var e in envList)
            {
                var parts = e.Split('=', 2);
                if (parts.Length == 2)
                {
                    sb.Append($"-e \"{parts[0]}={parts[1].Replace("\"", "\\\"")}\" ");
                }
            }

            sb.Append($"{_defaultImage}");

            _logger.LogInformation("Launching container {Name} via {Cli} for @{PersonaId}", containerName, _detectedCli, personaId);
            var (runExit, runOut, runErr) = await RunCliAsync(_detectedCli, sb.ToString(), ct);
            if (runExit != 0)
            {
                throw new InvalidOperationException($"Failed to run container via {_detectedCli}: {runErr}\n{runOut}");
            }
            _logger.LogInformation("Container {Name} launched successfully via {Cli}", containerName, _detectedCli);
            return;
        }

        throw new InvalidOperationException($"No active Docker/Podman socket or CLI runtime available to launch persona container for @{personaId}.");
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

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
