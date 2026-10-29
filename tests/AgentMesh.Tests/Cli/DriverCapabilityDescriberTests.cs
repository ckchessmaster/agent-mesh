using System.Diagnostics;
using AgentMesh.Api.Services;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Cli;

/// <summary>
/// The runner side of option C: reading a driver's real capabilities from the driver itself and
/// pushing them at registration.
/// </summary>
/// <remarks>
/// Both the subprocess and the HTTP read are injected, so nothing here spawns <c>agy</c> or opens a
/// socket. That is not merely for speed: CI runners and the API container have no driver binaries
/// installed, so a test that shelled out would fail on exactly the machines where the production
/// code cannot work either - and would then be deleted rather than fixed.
/// <para>
/// Shares the <c>DriverCapabilityDescriberSeams</c> collection with
/// <c>MeshClientDeclarationIsolationTests</c>, which swaps the same static seams. xUnit parallelizes
/// across classes, and a constructor captures "whatever is current" as the value to restore, so two
/// classes doing that concurrently cross-restore each other's fakes: one class's Dispose puts the
/// real seams back mid-flight through the other's tests. With agy off PATH - CI's image - the real
/// resolver then returns null and a declaration silently comes back empty.
/// </para>
/// </remarks>
[Collection("DriverCapabilityDescriberSeams")]
public sealed class DriverCapabilityDescriberTests : IDisposable
{
    private const string SampleAgyModels = """
        Fetching available models...
        gemini-4-argon-low      Gemini 4 Argon (low)
        gemini-4-argon-high     Gemini 4 Argon (high)
        claude-sonnet-5-5       Claude Sonnet 5.5
        """;

    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>> _originalAgyRunner;
    private readonly Func<string, string?, CancellationToken, Task<(int StatusCode, string Body, string? Error)>> _originalLister;
    private readonly Func<string, string?> _originalResolver;

    public DriverCapabilityDescriberTests()
    {
        // The seams are static fields, so restore them or a test that swaps one leaks into the
        // next and the failure shows up in an unrelated file.
        _originalAgyRunner = DriverCapabilityDescriber.AgyModelsRunner;
        _originalLister = DriverCapabilityDescriber.ModelsEndpointReader;
        _originalResolver = DriverCapabilityDescriber.AgyExecutableResolver;
    }

    public void Dispose()
    {
        DriverCapabilityDescriber.AgyModelsRunner = _originalAgyRunner;
        DriverCapabilityDescriber.ModelsEndpointReader = _originalLister;
        DriverCapabilityDescriber.AgyExecutableResolver = _originalResolver;
    }

    /// <summary>
    /// Pretends agy is installed. Without this the real resolver runs first and short-circuits to
    /// "not installed" on any machine without the binary - which is CI and the API container - so
    /// the injected runner below would never be reached and the test would fail for a reason that
    /// has nothing to do with the code under test.
    /// </summary>
    private static void WithAgyInstalled()
        => DriverCapabilityDescriber.AgyExecutableResolver = _ => "/usr/local/bin/agy";

    private static AgentPresetConfig Preset(
        string adapter = "agy",
        string? endpoint = null,
        string? command = null,
        string? apiKey = null,
        string? defaultModel = null,
        List<string>? capabilities = null)
        => new()
        {
            Id = adapter,
            Name = adapter,
            AdapterType = adapter,
            Endpoint = endpoint,
            Command = command,
            ApiKey = apiKey,
            DefaultModel = defaultModel,
            Capabilities = capabilities ?? new List<string>()
        };

    private static DriverCapabilityDeclaration Declaration(
        string adapter = "agy",
        params string[] modelIds)
        => new()
        {
            Adapter = adapter,
            Models = modelIds
                .Select(id => new DriverCapabilityDeclaration.DeclaredModel { Id = id })
                .ToList(),
            Source = "agy-models",
            ObservedAt = DateTimeOffset.UtcNow
        };

    // ---------------------------------------------------------------------------------------
    // agy
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Agy_DeclaresWhatTheBinaryActuallyListed()
    {
        DriverCapabilityDescriber.AgyModelsRunner = (_, _, _, _) =>
            Task.FromResult((0, SampleAgyModels, string.Empty));
        WithAgyInstalled();

        var declared = await DriverCapabilityDescriber.DescribeAgyAsync(Preset());

        declared!.Adapter.Should().Be(DriverAdapters.Agy);
        declared.Models.Select(m => m.Id).Should().Equal("gemini-4-argon", "claude-sonnet-5-5");
        declared.Models[0].Efforts.Should().Equal("low", "high");
        declared.Source.Should().Be("agy-models");
        declared.UnavailableReason.Should().BeNull();
    }

    [Fact]
    public async Task Agy_NonZeroExitIsReportedNotPaperedOver()
    {
        // Verified against the real binary: a signed-out `agy models` exits 1 and writes the reason
        // to stderr. The API's probe discards stderr and returns null on any non-zero exit, so it
        // can only guess "probably not signed in". The runner has the actual sentence.
        DriverCapabilityDescriber.AgyModelsRunner = (_, _, _, _) =>
            Task.FromResult((1, string.Empty, "Error: Please sign in to view available models."));
        WithAgyInstalled();

        var declared = await DriverCapabilityDescriber.DescribeAgyAsync(Preset());

        declared!.Models.Should().BeEmpty();
        declared.Source.Should().BeNull();
        declared.UnavailableReason.Should().Contain("Please sign in");
    }

    [Fact]
    public async Task Agy_StripAnsiKeepsEscapeCodesOutOfTheReason()
    {
        // The real binary prefixes its error with ESC[K. string.Trim() does not strip ESC (it is
        // not whitespace), so the raw control character would otherwise reach a picker header.
        DriverCapabilityDescriber.AgyModelsRunner = (_, _, _, _) =>
            Task.FromResult((1, string.Empty, "\u001b[KError: Not authenticated."));
        WithAgyInstalled();

        var declared = await DriverCapabilityDescriber.DescribeAgyAsync(Preset());

        // The reason is wrapped in the standard "agy reported no models (...)" sentence, so the
        // assertion is that the captured line survived intact and control-character-free.
        declared!.UnavailableReason.Should().Contain("Error: Not authenticated.");
        declared.UnavailableReason.Should().NotContain("\u001b");
    }

    [Fact]
    public async Task Agy_ZeroExitWithNoModelsIsStillUnavailable()
    {
        // agy prints its banner to stdout and can exit 0 having listed nothing. Treating that as
        // "this account has no models" would be a lie; it is a read that produced nothing.
        var ran = false;
        DriverCapabilityDescriber.AgyModelsRunner = (_, _, _, _) =>
        {
            ran = true;
            return Task.FromResult((0, "Fetching available models...", string.Empty));
        };
        WithAgyInstalled();

        var declared = await DriverCapabilityDescriber.DescribeAgyAsync(Preset());

        // Without this the test also passes when the executable resolver short-circuits to "not
        // installed", which satisfies both assertions below for entirely the wrong reason.
        ran.Should().BeTrue("the point of this test is what a zero-exit empty read produces");
        declared!.Models.Should().BeEmpty();
        declared.UnavailableReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Agy_MissingBinaryIsReportedWithoutSpawningAnything()
    {
        // No seam override: this exercises the real resolver against a command that cannot exist.
        // The assertion is that we get a reason rather than an exception.
        var declared = await DriverCapabilityDescriber.DescribeAgyAsync(
            Preset(command: "definitely-not-installed-xyzzy"));

        declared!.Models.Should().BeEmpty();
        declared.UnavailableReason.Should().Contain("not installed");
    }

    [Fact]
    public async Task Agy_NeverDeclaresContextSelection()
    {
        // agy has no context-size flag and AgyAdapter forwards none. Declaring support would put a
        // picker back on screen that changes nothing - the bug that pinned a stale "500k" to agy.
        DriverCapabilityDescriber.AgyModelsRunner = (_, _, _, _) =>
            Task.FromResult((0, SampleAgyModels, string.Empty));
        WithAgyInstalled();

        var declared = await DriverCapabilityDescriber.DescribeAgyAsync(Preset());

        // Assert the successful read first. The unavailable path also leaves this flag false, so
        // without it the test would pass on a machine with no agy at all.
        declared!.Models.Should().NotBeEmpty();
        declared.SupportsContextSelection.Should().BeFalse();
    }

    [Fact]
    public void Timeouts_AreFarBelowPromptExecution()
    {
        // These run on the registration path. The 180s default used for real prompt execution would
        // hold the runner offline for three minutes over a capability picker.
        DriverCapabilityDescriber.AgyTimeout.Should().BeLessThan(TimeSpan.FromSeconds(30));
        DriverCapabilityDescriber.EndpointTimeout.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RunAgy_HungProcessIsKilledAndReportedNotThrown()
    {
        // Exercises the real RunAgyAsync, because the kill-on-timeout path is what decides whether
        // a hung `agy models` becomes a hung runner and it is unreachable through the seam.
        // `sleep 30` with a 300ms ceiling: without the kill this test would take 30s.
        //
        // /bin/sleep and /bin/sh are a deliberate POSIX assumption; the runner ships as
        // dist/cli/linux-x64 and CI is ubuntu, and the alternative is a per-OS fixture that never
        // runs on the platform the product actually ships on.
        var started = DateTimeOffset.UtcNow;

        var (exitCode, _, stdErr) = await DriverCapabilityDescriber.RunAgyAsync(
            "/bin/sleep", new[] { "30" }, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        (DateTimeOffset.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the timeout must actually terminate the process");
        exitCode.Should().Be(-1);
        stdErr.Should().Contain("Timed out");
    }

    [Fact]
    public async Task RunAgy_DrainsBothStreamsConcurrently()
    {
        // A process whose stdout pipe fills blocks on write. Reading only stdout - or reading the
        // streams after waiting for exit - deadlocks until the timeout and reports a perfectly
        // healthy install as unreadable.
        //
        // The volume is generated by the child, not passed as an argument: 200KB on a command line
        // exceeds ARG_MAX and fails to start the process at all, which is a different bug than the
        // one under test. 200KB per stream clears the 64KB Linux pipe buffer on both.
        const int payload = 200_000;

        var (exitCode, stdOut, stdErr) = await DriverCapabilityDescriber.RunAgyAsync(
            "/bin/sh",
            new[]
            {
                "-c",
                $"head -c {payload} /dev/zero | tr '\\0' 'x'; head -c {payload} /dev/zero | tr '\\0' 'y' 1>&2"
            },
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        exitCode.Should().Be(0);

        // Asserted through scalars rather than the strings themselves: a failure here would
        // otherwise dump 400KB of 'x' into the test log, which buries the actual diff.
        stdOut.Length.Should().Be(payload);
        stdErr.Length.Should().Be(payload);
        stdOut.All(c => c == 'x').Should().BeTrue("stdout must be the child's stdout payload");
        stdErr.All(c => c == 'y').Should().BeTrue("stderr must be the child's stderr payload");
    }

    // ---------------------------------------------------------------------------------------
    // OpenAI-compatible
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task OpenAi_DeclaresWhatTheServerServes()
    {
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((200, """{"data":[{"id":"llama-3.3:latest"}]}""", (string?)null));

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai", "http://localhost:11434/v1"), "key", default);

        declared!.Models.Select(m => m.Id).Should().Equal("llama-3.3:latest");
        declared.Source.Should().Be("openai-models-endpoint");
        // /models reports no effort support and the adapter forwards none, so an empty list here is
        // the honest answer rather than a plausible one.
        declared.Models[0].Efforts.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAi_UsesTheSameCredentialRuleAsARun()
    {
        // If the declaration read and the run resolved keys differently, a picker could list models
        // visible to an ambient OPENAI_API_KEY that the preset's own key would be refused.
        string? seenKey = null;
        DriverCapabilityDescriber.ModelsEndpointReader = (_, key, _) =>
        {
            seenKey = key;
            return Task.FromResult((200, """{"data":[{"id":"m-1"}]}""", (string?)null));
        };

        var preset = Preset("openai", "http://h/v1", apiKey: "preset-key");

        await DriverCapabilityDescriber.DescribeOpenAiAsync(
            preset, OpenAiAdapter.ResolveApiKey(preset), default);

        seenKey.Should().Be("preset-key");
    }

    [Fact]
    public async Task OpenAi_RejectedKeyIsReportedAsAuthenticationNotAsNoModels()
    {
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((401, "{}", (string?)null));

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai", "http://h/v1"), "bad", default);

        declared!.Models.Should().BeEmpty();
        declared.UnavailableReason.Should().Contain("401");
    }

    [Fact]
    public async Task OpenAi_MissingEndpointIsReportedWithoutAnyRequest()
    {
        var called = false;
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
        {
            called = true;
            return Task.FromResult((200, "{}", (string?)null));
        };

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai"), "key", default);

        declared!.UnavailableReason.Should().Contain("no endpoint");
        called.Should().BeFalse();
    }

    [Fact]
    public async Task OpenAi_HtmlBodyIsNotTreatedAsAnEmptyCatalogue()
    {
        // A captive portal or login page returns 200 with HTML. The reason has to distinguish
        // "server has no models" from "you are not talking to a model server".
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((200, "<html><body>Sign in</body></html>", (string?)null));

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai", "http://h/v1"), "key", default);

        declared!.Models.Should().BeEmpty();
        declared.UnavailableReason.Should().Contain("no models");
    }

    [Fact]
    public async Task OpenAi_UnreachableHostDegradesToAReasonNotAnException()
    {
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((0, string.Empty, (string?)"Could not reach http://h/v1: refused"));

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai", "http://h/v1"), "key", default);

        declared!.Models.Should().BeEmpty();
        declared.UnavailableReason.Should().Contain("refused");
    }

    [Fact]
    public async Task OpenAi_AllowsContextSelectionForServersThatHonourIt()
    {
        // Unlike agy, the OpenAI adapter does forward a context budget, so the picker is real here.
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((200, """{"data":[{"id":"gpt-5.1-codex"}]}""", (string?)null));

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai", "http://h/v1"), "key", default);

        declared!.SupportsContextSelection.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Endpoint binding (review finding 3, runner side).
    //
    // The API rejects a declaration whose endpoint no longer matches the row. These pin the other
    // half: that a runner actually sends the value that check compares. Without them the guard is
    // dead code in production while still passing its own tests off hand-built declarations.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task OpenAi_ASuccessfulReadIsBoundToTheEndpointItWasReadFrom()
    {
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((200, """{"data":[{"id":"llama-3.3:latest"}]}""", (string?)null));

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai", "http://localhost:11434/v1"), "key", default);

        declared!.Endpoint.Should().Be("http://localhost:11434/v1");
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(500, "{}")]
    [InlineData(0, "")]
    [InlineData(200, "<html><body>Sign in</body></html>")]
    public async Task OpenAi_AFailedReadIsBoundTooBecauseThatIsWhereRepointingShowsUp(
        int status, string body)
    {
        // The empty catalogue a repointed driver produces is a failure, so binding only the success
        // path would leave the exact case finding 3 describes unguarded: the row now points at
        // server B while the stored reason still complains about server A.
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((status, body, (string?)null));

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai", "http://old-server/v1"), "key", default);

        declared!.Models.Should().BeEmpty();
        declared.Endpoint.Should().Be("http://old-server/v1",
            "an unbound failure reason is the one the API cannot invalidate");
    }

    [Fact]
    public async Task OpenAi_NoEndpointMeansNoBindingRatherThanAnEmptyOne()
    {
        // "There was no endpoint" and "the endpoint does not match" are different statements. An
        // empty string would normalize to null on the API side anyway, but writing it would make
        // the wire format ambiguous about which one the runner meant.
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((200, """{"data":[{"id":"m"}]}""", (string?)null));

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai"), "key", default);

        declared!.Endpoint.Should().BeNull();
    }

    [Fact]
    public async Task Agy_DeclaresNoEndpointBecauseItsReadIsHostLevel()
    {
        // agy reads a local binary. Inventing an endpoint for it would let the API's binding check
        // reject the very driver this feature was built for.
        WithAgyInstalled();
        DriverCapabilityDescriber.AgyModelsRunner = (_, _, _, _) =>
            Task.FromResult((0, SampleAgyModels, string.Empty));

        var declared = await DriverCapabilityDescriber.DescribeAgyAsync(Preset());

        declared!.Endpoint.Should().BeNull();
    }

    [Fact]
    public async Task TheEndpointSurvivesTheRoundTripThroughTheRegistrationBlob()
    {
        // The API reads the value back out of a stored blob, not off the runner's object. If
        // serialization dropped it - a null-ignoring writer, a name mismatch - the guard would
        // silently never fire and every test above would still pass.
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((401, "{}", (string?)null));

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai", "http://old-server/v1"), "bad", default);

        var blob = MeshClient.SerializePresetCapabilities(
            Preset("openai", "http://old-server/v1"), declared);

        var readBack = DriverCapabilityDeclaration.TryRead(blob);

        readBack!.Endpoint.Should().Be("http://old-server/v1");
        readBack.UnavailableReason.Should().Contain("401");
    }

    [Fact]
    public async Task TheApiAcceptsABindingWrittenByTheRunnerForACosmeticallyDifferentRow()
    {
        // The cross-boundary contract in one test: the value is produced by the real describer from
        // what an operator typed, serialized the way a registration serializes it, stored, read
        // back by the API's parser, then judged by the API's own trust guard.
        //
        // Each half is tested separately elsewhere, which is exactly how a mismatch could survive:
        // the runner writes "  http://LLM:8000/v1/  " and the row says "http://llm:8000/v1/models";
        // both are correct about one server, and only the two halves disagreeing shows up here.
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((200, """{"data":[{"id":"llama-3.3:latest"}]}""", (string?)null));

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai", "  http://LLM:8000/v1/  "), "key", default);

        var stored = MeshClient.SerializePresetCapabilities(
            Preset("openai", "  http://LLM:8000/v1/  "), declared);

        var readBack = DriverCapabilityDeclaration.TryRead(stored);
        readBack.Should().NotBeNull();

        var row = new DriverCapabilityProbeRequest(
            DriverId: "openai-driver",
            RuntimeType: RuntimeType.OpenAiCompatible,
            Adapter: "openai",
            Endpoint: "http://llm:8000/v1/models",
            Declared: readBack);

        DriverCapabilityProbe.TryTrustedDeclaration(
            row, DateTimeOffset.UtcNow, out var trusted, out var adapter)
            .Should().BeTrue("one server written two ways is still one server");
        trusted.Endpoint.Should().Be("http://LLM:8000/v1/",
            "the describer trims surrounding whitespace but does not otherwise rewrite what the " +
            "operator typed; the guard compares normalized forms rather than mutating the report");
        adapter.Should().Be(DriverAdapters.OpenAi);
    }

    [Fact]
    public async Task TheApiRejectsABindingWrittenByTheRunnerOnceTheRowIsRepointed()
    {
        // The other half of the contract, and the actual defect: the same stored declaration must
        // stop being trusted the moment the row points at a different server. Had the runner written
        // no endpoint, this would return true and the stale catalogue would win - which is why the
        // binding has to be produced on the runner, not inferred later.
        DriverCapabilityDescriber.ModelsEndpointReader = (_, _, _) =>
            Task.FromResult((200, """{"data":[{"id":"model-from-old-server"}]}""", (string?)null));

        var declared = await DriverCapabilityDescriber.DescribeOpenAiAsync(
            Preset("openai", "http://old-server/v1"), "key", default);

        var stored = MeshClient.SerializePresetCapabilities(
            Preset("openai", "http://old-server/v1"), declared);

        var row = new DriverCapabilityProbeRequest(
            DriverId: "openai-driver",
            RuntimeType: RuntimeType.OpenAiCompatible,
            Adapter: "openai",
            Endpoint: "http://new-server/v1",
            Declared: DriverCapabilityDeclaration.TryRead(stored));

        DriverCapabilityProbe.TryTrustedDeclaration(
            row, DateTimeOffset.UtcNow, out _, out _)
            .Should().BeFalse("the row no longer points at the server this catalogue came from");
    }

    // ---------------------------------------------------------------------------------------
    // The blob the runner pushes
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Serialize_WithoutADeclarationIsUnchangedForBareArrayDrivers()
    {
        // Strictly additive: a runner with nothing to declare writes exactly what it wrote before
        // option C, so this cannot change behaviour for any driver that is not opted in.
        var json = MeshClient.SerializePresetCapabilities(
            Preset(capabilities: new List<string> { "chat", "tools" }), declared: null);

        json.Should().Be("""["chat","tools"]""");
    }

    [Fact]
    public void Serialize_WithADeclarationUpgradesAgyToTheObjectFormReadersAlreadyHandle()
    {
        var json = MeshClient.SerializePresetCapabilities(
            Preset(capabilities: new List<string> { "chat" }),
            Declaration("agy", "m-1"));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
        // The list is relocated under `capabilities`, the same normalization
        // DriverCapabilities.AsObject performs, so no reader sees a shape it cannot already parse.
        doc.RootElement.GetProperty("capabilities")[0].GetString().Should().Be("chat");
        DriverCapabilityDeclaration.TryRead(json)!.Models.Select(m => m.Id).Should().Equal("m-1");
    }

    [Fact]
    public void Serialize_OpenAiKeepsEveryExistingKeyAndAddsDeclared()
    {
        // The dashboard's driver form and AgentKind both read these keys off the object form.
        var json = MeshClient.SerializePresetCapabilities(
            Preset("openai", "http://h/v1", apiKey: "k", defaultModel: "m"),
            Declaration("openai", "served-model"));

        var view = DriverCapabilities.Parse(json);
        view.Adapter.Should().Be("openai");
        view.Endpoint.Should().Be("http://h/v1");
        view.DefaultModel.Should().Be("m");
        view.Declared!.Models.Select(m => m.Id).Should().Equal("served-model");
    }

    [Fact]
    public void Serialize_DeclarationIsNestedNotFlattenedIntoTheOperatorAllowList()
    {
        // `models` already means "an operator typed these", and the API merges it over resolved
        // results as an override. Flattening a machine's self-report into it would have made the
        // runner's list authoritative over the provider's.
        var view = DriverCapabilities.Parse(MeshClient.SerializePresetCapabilities(
            Preset("openai", "http://h/v1"), Declaration("openai", "served-model")));

        view.Models.Should().BeEmpty();
        view.Declared!.Models.Should().HaveCount(1);
    }

    [Fact]
    public void Serialize_OpenAiWithoutADeclarationIsUnchanged()
    {
        var json = MeshClient.SerializePresetCapabilities(
            Preset("openai", "http://h/v1", defaultModel: "m"), declared: null);

        DriverCapabilities.Parse(json).Declared.Should().BeNull();
        json.Should().NotContain("declared");
    }

    [Fact]
    public void Serialize_AgyBlobUpgradeKeepsTheCapabilityListWhereReadersLook()
    {
        // The agy blob changes from a bare array to an object once a runner declares anything.
        // Both AgentKind.ParseExistingAsObject and DriverCapabilities.AsObject already normalize a
        // bare array to {capabilities: [...]}, so the upgrade lands on the shape readers already
        // produce internally - but that is an inference about two other files, so it is pinned here
        // rather than trusted.
        var bare = MeshClient.SerializePresetCapabilities(
            Preset(capabilities: new List<string> { "chat", "tools" }), declared: null);
        var declared = MeshClient.SerializePresetCapabilities(
            Preset(capabilities: new List<string> { "chat", "tools" }), Declaration("agy", "m-1"));

        DriverCapabilities.Parse(bare).Capabilities.Should().Equal("chat", "tools");
        DriverCapabilities.Parse(declared).Capabilities.Should().Equal("chat", "tools");
    }

    [Fact]
    public void Serialize_PersonaRunnerMarkerSurvivesTheAgyBlobUpgrade()
    {
        // Items 4/5 classify a persona-runner identity from this blob. HasRunnerMarker only reads
        // the OBJECT form (a bare array root returns false), and BuildRunnerIdentityCapabilities
        // normalizes whatever it is given into that form - so the question is whether the upgrade
        // from bare array to object changes what the marker patch produces, or drops the list.
        var bare = MeshClient.SerializePresetCapabilities(
            Preset(capabilities: new List<string> { "chat" }), declared: null);
        var declared = MeshClient.SerializePresetCapabilities(
            Preset(capabilities: new List<string> { "chat" }), Declaration("agy", "m-1"));

        var barePatched = AgentKind.BuildRunnerIdentityCapabilities("persona-1", "agy", bare);
        var declaredPatched = AgentKind.BuildRunnerIdentityCapabilities("persona-1", "agy", declared);

        AgentKind.HasRunnerMarker(barePatched).Should().BeTrue();
        AgentKind.HasRunnerMarker(declaredPatched).Should().BeTrue();

        // The capability strings survive the marker patch in both shapes.
        DriverCapabilities.Parse(barePatched).Capabilities.Should().Equal("chat");
        DriverCapabilities.Parse(declaredPatched).Capabilities.Should().Equal("chat");

        // And the declaration is not collateral damage of the marker rewrite.
        DriverCapabilityDeclaration.TryRead(barePatched).Should().BeNull();
        DriverCapabilityDeclaration.TryRead(declaredPatched)!.Models.Select(m => m.Id)
            .Should().Equal("m-1");
    }

    [Fact]
    public void Describers_OnlyAgyAndOpenAiOptIn()
    {
        // Adapters with no discovery surface must not be pushed into inventing an answer to satisfy
        // an interface. They declare nothing and the API keeps doing what it does today for them.
        AgentAdapterFactory.Create(Preset("agy")).Should().BeAssignableTo<IDescribesOwnCapabilities>();
        AgentAdapterFactory.Create(Preset("openai", "http://h/v1")).Should().BeAssignableTo<IDescribesOwnCapabilities>();
        AgentAdapterFactory.Create(Preset("claude")).Should().NotBeAssignableTo<IDescribesOwnCapabilities>();
        AgentAdapterFactory.Create(Preset("copilot")).Should().NotBeAssignableTo<IDescribesOwnCapabilities>();
        AgentAdapterFactory.Create(Preset("generic")).Should().NotBeAssignableTo<IDescribesOwnCapabilities>();
    }
}
