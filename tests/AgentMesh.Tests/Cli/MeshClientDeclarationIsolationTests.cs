using System.Net;
using System.Text;
using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Cli;

/// <summary>
/// One <see cref="MeshClient"/> is shared by every runner in a process, so the declaration cache
/// has to be per driver. This file is the guard against handing one driver's catalogue to another.
/// </summary>
/// <remarks>
/// <para>
/// <c>Program.cs:115</c> constructs a single client and passes it to <c>AgentProcessManager</c>,
/// which hands that same instance to every <c>AgentRunnerInstance</c> (<c>agentmesh start --all</c>,
/// several driver targets, or the TUI). A cache that is not keyed by driver therefore leaks:
/// runner B's read lands on runner A's row.
/// </para>
/// <para>
/// The declarations come from the real adapters through their existing seams
/// (<see cref="DriverCapabilityDescriber.AgyModelsRunner"/>,
/// <see cref="DriverCapabilityDescriber.ModelsEndpointReader"/>), not from a fake describer. The
/// leak is about which cached value a <em>given preset</em> resolves to, so the
/// preset-to-adapter-to-declaration path has to be the production one. Nothing spawns a binary or
/// opens a socket; the mesh is a capturing handler.
/// </para>
/// <para>
/// Shares the <c>DriverCapabilityDescriberSeams</c> collection with
/// <c>DriverCapabilityDescriberTests</c> because both swap the same static seams. xUnit runs
/// different classes in parallel, and one class's constructor captures "whatever is current" as the
/// original to restore, so two classes doing that concurrently cross-restore each other's fakes and
/// a test can end up running against the real subprocess runner. That is not theoretical: with agy
/// off PATH - which is what CI's image has - the leaked real runner returns null and the case
/// sensitivity case loses its two models.
/// </para>
/// </remarks>
[Collection("DriverCapabilityDescriberSeams")]
public sealed class MeshClientDeclarationIsolationTests : IDisposable
{
    private const string AgyCatalogue = """
        Fetching available models...
        agy-model-a      Agy Model A
        agy-model-b      Agy Model B
        """;

    private const string OpenAiCatalogue = """
        {"data":[{"id":"openai-model-c"},{"id":"openai-model-d"}]}
        """;

    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>> _originalAgyRunner;
    private readonly Func<string, string?, CancellationToken, Task<(int StatusCode, string Body, string? Error)>> _originalLister;
    private readonly Func<string, string?> _originalResolver;
    private readonly string _configPath;

    /// <summary>Counts <c>agy models</c> invocations, for the "never read at shutdown" guard.</summary>
    private int _agyReads;

    public MeshClientDeclarationIsolationTests()
    {
        _originalAgyRunner = DriverCapabilityDescriber.AgyModelsRunner;
        _originalLister = DriverCapabilityDescriber.ModelsEndpointReader;
        _originalResolver = DriverCapabilityDescriber.AgyExecutableResolver;

        // The resolver runs before the subprocess seam, so without this every case below
        // short-circuits to "not installed" on a machine without agy - which is CI - and the
        // assertions would pass on a declaration that was never attempted.
        DriverCapabilityDescriber.AgyExecutableResolver = _ => "/usr/local/bin/agy";
        DriverCapabilityDescriber.AgyModelsRunner = (_, _, _, _) =>
        {
            _agyReads++;
            return Task.FromResult((0, AgyCatalogue, string.Empty));
        };
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((200, OpenAiCatalogue, (string?)null));

        // EnsureAgentRegisteredAsync persists the returned token through the store, so this has to
        // be disposable scratch rather than the developer's real ~/.agentmesh/config.json.
        _configPath = Path.Combine(Path.GetTempPath(), $"agentmesh-decl-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        DriverCapabilityDescriber.AgyModelsRunner = _originalAgyRunner;
        DriverCapabilityDescriber.ModelsEndpointReader = _originalLister;
        DriverCapabilityDescriber.AgyExecutableResolver = _originalResolver;

        if (File.Exists(_configPath)) File.Delete(_configPath);
    }

    [Fact]
    public async Task ASecondRunnerStartingDoesNotRewriteTheFirstRunnersDeclaration()
    {
        // The shutdown overwrite from the review. A registers, B registers and overwrites the
        // shared field, then A reports offline and pushes B's catalogue onto A's row. The API
        // cannot tell that happened: the blob is a self-report and reads as authoritative, so an
        // agy driver starts offering models only its neighbour's server serves.
        var mesh = new Mesh();
        var client = Client(mesh);

        await client.EnsureAgentRegisteredAsync(Preset("agy-driver", adapter: "agy"));
        await client.EnsureAgentRegisteredAsync(Preset("openai-driver", adapter: "openai", endpoint: "http://llm:8000/v1"));
        await client.SetAgentStatusAsync(Preset("agy-driver", adapter: "agy"), "offline");

        var pushed = DeclarationOn(mesh.LastBodyFor("agy-driver"));

        pushed.Should().NotBeNull("agy declares its own catalogue, so its row must keep one");
        pushed!.Adapter.Should().Be(DriverAdapters.Agy);
        pushed.Models.Select(m => m.Id).Should().Equal("agy-model-a", "agy-model-b");
    }

    [Fact]
    public async Task AnAdapterThatCannotDeclareDoesNotInheritAnotherRunnersDeclaration()
    {
        // The reconnect inheritance from the review. `claude` has no discovery surface, so
        // DescribeCapabilitiesAsync returns null, and `null ?? _declaredCapabilities` means "keep
        // whatever is cached" - which belongs to whichever runner read its driver last.
        //
        // The ordering is the point: registering claude nulls the shared field, so the leak only
        // appears once another runner writes it again afterwards. That is exactly the steady state
        // of a long-lived `--all` process, where reconnects interleave freely.
        var mesh = new Mesh();
        var client = Client(mesh);

        await client.EnsureAgentRegisteredAsync(Preset("agy-driver", adapter: "agy"));
        await client.EnsureAgentRegisteredAsync(Preset("claude-driver", adapter: "claude"));
        await client.SetAgentStatusAsync(Preset("agy-driver", adapter: "agy"), "online");
        await client.SetAgentStatusAsync(Preset("claude-driver", adapter: "claude"), "online");

        var pushed = DeclarationOn(mesh.LastBodyFor("claude-driver"));

        pushed.Should().BeNull(
            "claude has no discovery surface, so a declaration on its row is another driver's claim");
    }

    [Fact]
    public async Task EachRunnerKeepsItsOwnDeclarationAcrossInterleavedStatusPushes()
    {
        // Both directions at once. A fix that keys the dictionary but reads the wrong key passes
        // the two single-driver cases above and fails here.
        var mesh = new Mesh();
        var client = Client(mesh);

        var agy = Preset("agy-driver", adapter: "agy");
        var openAi = Preset("openai-driver", adapter: "openai", endpoint: "http://llm:8000/v1");

        await client.EnsureAgentRegisteredAsync(agy);
        await client.EnsureAgentRegisteredAsync(openAi);
        await client.SetAgentStatusAsync(openAi, "online");
        await client.SetAgentStatusAsync(agy, "online");
        await client.SetAgentStatusAsync(openAi, "offline");
        await client.SetAgentStatusAsync(agy, "offline");

        DeclarationOn(mesh.LastBodyFor("agy-driver"))!
            .Models.Select(m => m.Id).Should().Equal("agy-model-a", "agy-model-b");
        DeclarationOn(mesh.LastBodyFor("openai-driver"))!
            .Models.Select(m => m.Id).Should().Equal("openai-model-c", "openai-model-d");
    }

    [Fact]
    public async Task ReportingOfflineNeverReadsTheDriverButStillCarriesItsOwnDeclaration()
    {
        // Guard rather than reproduction: the existing design promises that a shutdown push does
        // not spawn a binary (nobody reads a dead runner's catalogue, and it would delay exit by
        // up to the probe timeout). Keying the cache must not cost that promise, and must not drop
        // the declaration either - the row keeps its last real answer until it ages out.
        var mesh = new Mesh();
        var client = Client(mesh);
        var agy = Preset("agy-driver", adapter: "agy");

        await client.EnsureAgentRegisteredAsync(agy);
        _agyReads.Should().Be(1, "registration reads the driver exactly once");

        _agyReads = 0;
        await client.SetAgentStatusAsync(agy, "offline");

        _agyReads.Should().Be(0, "a shutdown push must not spawn a subprocess");
        DeclarationOn(mesh.LastBodyFor("agy-driver"))!.Models.Should().HaveCount(2);
    }

    [Fact]
    public async Task TheCacheKeyIsCaseInsensitiveLikeEveryOtherAgentIdInTheCli()
    {
        // Ids are normalized to lower case in some paths (`ResolveOrCreatePreset`) and not in
        // others. A case-sensitive dictionary turns that into a silent cache miss: the row loses
        // its declaration and the API falls back to probing a binary it does not have - the
        // original Docker defect, re-opened by a string comparison.
        var mesh = new Mesh();
        var client = Client(mesh);

        await client.EnsureAgentRegisteredAsync(Preset("Agy-Driver", adapter: "agy"));
        await client.SetAgentStatusAsync(Preset("agy-driver", adapter: "agy"), "offline");

        DeclarationOn(mesh.LastBodyFor("agy-driver"))!.Models.Should().HaveCount(2);
    }

    private MeshClient Client(Mesh mesh)
        => new(new CliConfig { MeshUrl = "http://mesh.test", MasterToken = "master" },
            new ConfigStore(_configPath),
            new HttpClient(mesh) { BaseAddress = new Uri("http://mesh.test/") });

    private static AgentPresetConfig Preset(string id, string adapter, string? endpoint = null)
        => new()
        {
            Id = id,
            Name = id,
            AdapterType = adapter,
            Endpoint = endpoint,
            Capabilities = new List<string> { "chat" }
        };

    /// <summary>
    /// The declaration a push carried, read back out of the request body the way the API reads it.
    /// </summary>
    private static DriverCapabilityDeclaration? DeclarationOn(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var capabilitiesJson = doc.RootElement.TryGetProperty("capabilitiesJson", out var caps)
            ? caps.GetString()
            : null;

        return DriverCapabilityDeclaration.TryRead(capabilitiesJson);
    }

    /// <summary>
    /// Records one body per agent id, so an assertion can ask "what was this row told about?".
    /// </summary>
    private sealed class Mesh : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Content == null) return Ok();

            // Read once: the content stream is not replayable, and the register path needs both the
            // recorded body and the id it carries.
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            // The upsert path addresses the row in the URL; registration carries it in the body.
            // Keying both by id keeps each assertion a single lookup.
            var key = id ?? (path.Contains("/api/agents/") ? path.Split('/')[^1] : path);
            _bodies[key] = body;

            return path.EndsWith("/register", StringComparison.OrdinalIgnoreCase)
                ? Json(new { token = $"tok-{key}" })
                : Ok();
        }

        public string LastBodyFor(string agentId)
            => _bodies.TryGetValue(agentId, out var body)
                ? body
                : throw new InvalidOperationException(
                    $"no request was recorded for '{agentId}'; saw [{string.Join(", ", _bodies.Keys)}]");

        private static HttpResponseMessage Ok() => Json(new { ok = true });

        private static HttpResponseMessage Json(object payload)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
    }
}
