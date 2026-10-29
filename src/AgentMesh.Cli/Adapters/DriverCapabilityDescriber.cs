using System.Diagnostics;
using System.Net;
using System.Text;
using AgentMesh.Cli.Models;
using AgentMesh.Core.Services;

namespace AgentMesh.Cli.Adapters;

/// <summary>
/// Reads a driver's real capabilities from the driver itself, on the machine where it is
/// installed, for the registration payload (option C).
/// </summary>
/// <remarks>
/// <para>
/// The parsing lives in <see cref="DriverModelDiscovery"/> (Core) and is shared with the API's
/// background probe. That sharing is the whole point: two independent readers of one provider is
/// exactly the defect this line of work removes, and the CLI cannot reference the API project, so
/// the primitives had to move to Core rather than be copied.
/// </para>
/// <para>
/// The subprocess and HTTP calls are injectable delegates rather than direct calls. A capability
/// read on the registration path must never be able to hang a runner, and a test that spawns
/// <c>agy</c> or opens a socket is a test that fails on a machine without them - which is every CI
/// runner and every API container.
/// </para>
/// </remarks>
internal static class DriverCapabilityDescriber
{
    /// <summary>
    /// Ceiling for <c>agy models</c>. Deliberately far below the 180s default used for real prompt
    /// execution: this runs on the registration path, where a hung read would delay the runner
    /// coming online and would be indistinguishable from a dead mesh connection.
    /// <para>
    /// Measured against a real signed-out install: 0.9s wall clock, so the ceiling is ~16x the
    /// typical cost and only bites on a hung binary. That 0.9s is paid once at registration and
    /// again on each reconnect, which is in line with what the reconnect path already costs -
    /// <c>SyncPersonaBindingsFromMeshAsync</c> sleeps 1s on the common no-personas branch before it
    /// even reaches the hub. A runner that needs a faster path to first prompt should not be
    /// blocked by this; if that ever matters, the fix is to push the cached declaration on reconnect
    /// and refresh it in the background, not to shorten this ceiling.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan AgyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Ceiling for <c>GET {endpoint}/models</c>, for the same reason.</summary>
    internal static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs <c>agy models</c> and returns its raw output.</summary>
    internal static Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>
        AgyModelsRunner = RunAgyAsync;

    /// <summary>Fetches a <c>/models</c> document over HTTP.</summary>
    internal static Func<string, string?, CancellationToken, Task<(int StatusCode, string Body, string? Error)>>
        ModelsEndpointReader = ReadModelsEndpointAsync;

    /// <summary>
    /// Locates the driver binary on this machine.
    /// </summary>
    /// <remarks>
    /// Injectable for the same reason as the other two seams, and it has to be its own seam rather
    /// than being covered by <see cref="AgyModelsRunner"/>: resolution happens <em>first</em>, so a
    /// test that overrode only the runner would still short-circuit to "not installed" on a machine
    /// without agy - which is every CI runner and every API container. Those tests passed locally
    /// and would have failed in CI, which is the precise failure mode this seam exists to prevent.
    /// </remarks>
    internal static Func<string, string?> AgyExecutableResolver = AgentAdapterFactory.ResolveExecutablePath;

    /// <summary>
    /// Declares what the local <c>agy</c> install can run.
    /// </summary>
    /// <remarks>
    /// A failure here is reported, never papered over. The runner is the component best placed to
    /// know why its own driver is unreadable - "not signed in on this machine" is ground truth
    /// about an account that the API can never obtain - so the reason is worth shipping even when
    /// the model list is empty.
    /// </remarks>
    internal static async Task<DriverCapabilityDeclaration> DescribeAgyAsync(
        AgentPresetConfig preset,
        CancellationToken ct = default)
    {
        var executable = AgyExecutableResolver(
            string.IsNullOrWhiteSpace(preset.Command) ? "agy" : preset.Command!);

        if (executable == null)
        {
            return Unavailable(
                DriverAdapters.Agy,
                "The agy CLI is not installed on the runner machine for this driver.");
        }

        try
        {
            var (exitCode, stdOut, stdErr) = await AgyModelsRunner(
                executable, new[] { "models" }, AgyTimeout, ct);

            // agy prints its catalogue to stdout and its errors to stdout as well, so the exit
            // code is the only reliable signal. Treating a non-zero exit as "no models but here is
            // why" is what keeps a signed-out account from looking like an account with no models.
            var models = exitCode == 0 ? DriverModelDiscovery.ParseAgyModels(stdOut) : Array.Empty<DriverModelCapability>();

            if (models.Count == 0)
            {
                var detail = FirstMeaningfulLine(exitCode == 0 ? stdOut : stdErr);
                return Unavailable(
                    DriverAdapters.Agy,
                    detail == null
                        ? "agy reported no models. It is most likely signed out on this machine."
                        : $"agy reported no models ({detail}).");
            }

            return new DriverCapabilityDeclaration
            {
                Adapter = DriverAdapters.Agy,
                Models = models.Select(ToDeclared).ToList(),
                // agy has no context-size flag, so the picker is hidden rather than offering a
                // control that changes nothing. Same rule the API applies.
                SupportsContextSelection = ModelContextLimits.SupportsContextSelection(DriverAdapters.Agy),
                Source = "agy-models",
                ObservedAt = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Shutdown, not a driver problem. Do not record it as one.
        }
        catch (Exception ex)
        {
            return Unavailable(DriverAdapters.Agy, $"Could not read agy's model list: {ex.Message}");
        }
    }

    /// <summary>
    /// Declares what an OpenAI-compatible server actually serves.
    /// </summary>
    /// <remarks>
    /// This is the case the API could already handle but should not have had to: the server is
    /// reachable from anywhere, so moving the read to the runner buys latency and request-path I/O
    /// removal rather than new reach. It is still worth doing for the same reason - a dashboard
    /// load should not fan out to N third-party servers.
    /// </remarks>
    internal static async Task<DriverCapabilityDeclaration> DescribeOpenAiAsync(
        AgentPresetConfig preset,
        string? apiKey,
        CancellationToken ct = default)
    {
        var endpoint = preset.Endpoint?.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Unavailable(
                DriverAdapters.OpenAi,
                "This driver has no endpoint configured, so its model list cannot be read.");
        }

        var url = DriverModelDiscovery.ToModelsUrl(endpoint);

        try
        {
            var (statusCode, body, error) = await ModelsEndpointReader(url, apiKey, ct);

            // Status 0 is not an HTTP status: it means no response was ever obtained, and the
            // transport error is the only real explanation. Reporting "returned HTTP 0" would turn
            // "connection refused" and "TLS handshake failed" into the same meaningless sentence -
            // the class of unhelpful message this whole line of work is trying to remove.
            if (statusCode == 0)
            {
                return Unavailable(
                    DriverAdapters.OpenAi,
                    error ?? $"Could not reach {endpoint}.");
            }

            if (statusCode == (int)HttpStatusCode.Unauthorized || statusCode == (int)HttpStatusCode.Forbidden)
            {
                return Unavailable(
                    DriverAdapters.OpenAi,
                    $"{endpoint} rejected the configured API key (HTTP {statusCode}).");
            }

            if (statusCode < 200 || statusCode >= 300)
            {
                return Unavailable(
                    DriverAdapters.OpenAi,
                    $"{endpoint} returned HTTP {statusCode} for /models.");
            }

            var ids = DriverModelDiscovery.ParseOpenAiModelIds(body);
            if (ids.Count == 0)
            {
                return Unavailable(
                    DriverAdapters.OpenAi,
                    error ?? $"{endpoint} returned no models. Check that the endpoint points at a model server, not a proxy page.");
            }

            var supportsContext = ModelContextLimits.SupportsContextSelection(DriverAdapters.OpenAi);

            return new DriverCapabilityDeclaration
            {
                Adapter = DriverAdapters.OpenAi,
                Models = ids.Select(id => new DriverCapabilityDeclaration.DeclaredModel
                {
                    Id = id,
                    Name = id,
                    // /models reports no effort support, and the adapter forwards none, so an
                    // empty list here is the honest answer rather than a plausible one.
                    Efforts = Array.Empty<string>()
                }).ToList(),
                SupportsContextSelection = supportsContext,
                Source = "openai-models-endpoint",
                ObservedAt = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unavailable(DriverAdapters.OpenAi, $"Could not reach {endpoint}: {ex.Message}");
        }
    }

    private static DriverCapabilityDeclaration.DeclaredModel ToDeclared(DriverModelCapability model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Efforts = model.Efforts
        // No ContextSizes: see the note on DeclaredModel. The runner reports what exists, the API
        // decides what is selectable.
    };

    private static DriverCapabilityDeclaration Unavailable(string adapter, string reason) => new()
    {
        Adapter = adapter,
        Models = Array.Empty<DriverCapabilityDeclaration.DeclaredModel>(),
        SupportsContextSelection = ModelContextLimits.SupportsContextSelection(adapter),
        Source = null,
        UnavailableReason = reason,
        ObservedAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// The first line that is not blank or a braille spinner, for use in a reason string.
    /// </summary>
    /// <remarks>
    /// ANSI is stripped because this text is rendered in a picker header. Verified against the real
    /// binary: a signed-out <c>agy models</c> writes
    /// <c>ESC[K Error: Please sign in to view available models...</c> to stderr, and
    /// <c>string.Trim()</c> does not remove <c>ESC</c> (it is not whitespace), so the raw escape
    /// would otherwise reach the DOM. The API's own probe never sees this text - it discards stderr
    /// and returns null on any non-zero exit - so this path needed its own handling.
    /// </remarks>
    private static string? FirstMeaningfulLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        foreach (var line in raw.Split('\n'))
        {
            var trimmed = StripAnsi(line).Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.All(IsSpinnerGlyph)) continue;

            // A reason is rendered in a picker header, so it is truncated rather than allowed to
            // become a stack trace.
            return trimmed.Length > 160 ? trimmed[..157] + "..." : trimmed;
        }

        return null;
    }

    /// <summary>
    /// Removes CSI sequences (colours, cursor moves, line clears) from a captured line.
    /// </summary>
    private static string StripAnsi(string value)
    {
        if (value.IndexOf('\u001b') < 0) return value;

        var sb = new StringBuilder(value.Length);
        var i = 0;

        while (i < value.Length)
        {
            var c = value[i];
            if (c != '\u001b')
            {
                sb.Append(c);
                i++;
                continue;
            }

            // Escape sequence: ESC '[' <parameter bytes 0x30-0x3F> <final byte 0x40-0x7E>,
            // or ESC followed by a single character.
            i++;
            if (i < value.Length && value[i] == '[')
            {
                i++;
                while (i < value.Length && value[i] < '@') i++;
                if (i < value.Length) i++; // consume the final byte
            }
            else if (i < value.Length)
            {
                i++;
            }
        }

        return sb.ToString();
    }

    private static bool IsSpinnerGlyph(char c) => c >= '\u2800' && c <= '\u28FF';

    /// <summary>
    /// Runs a short-lived driver command and captures both streams.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the kill-on-timeout path can be tested against a real
    /// process. That path decides whether a hung <c>agy models</c> becomes a hung runner, and it is
    /// not reachable through the injected seam.
    /// </remarks>
    internal static async Task<(int ExitCode, string StdOut, string StdErr)> RunAgyAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            process.Start();

            // Both streams are drained concurrently: a process whose pipe buffer fills blocks on
            // write, so reading only stdout would let a chatty `agy models` deadlock until the
            // timeout and report a healthy install as unreadable.
            var stdOutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (OperationCanceledException)
        {
            // A hung `agy models` must not become a hung runner. Kill it and report the read as
            // failed; the API's fallback path is then exactly today's behaviour.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return (-1, string.Empty, $"Timed out after {timeout.TotalSeconds:F0}s reading agy's model list.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static async Task<(int StatusCode, string Body, string? Error)> ReadModelsEndpointAsync(
        string url,
        string? apiKey,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(EndpointTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }

            // No proxy: a model listing is a request to a server the operator named explicitly,
            // and routing it through a corporate proxy is how it comes back as an HTML login page.
            using var client = CreateDirectClient();

            using var response = await client.SendAsync(request, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return ((int)response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return (0, string.Empty, $"Could not reach {url}: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            return (0, string.Empty, $"Timed out reaching {url}: {ex.Message}");
        }
    }

    private static HttpClient CreateDirectClient() => new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = EndpointTimeout
    };
}
