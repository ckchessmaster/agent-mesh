using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using FluentAssertions;

namespace AgentMesh.Tests.Services;

/// <summary>
/// Capability resolution per driver: what gets probed, what gets cached, and what must never be
/// invented when a provider cannot be read.
/// </summary>
public class DriverCapabilityProbeTests
{
    private const string SampleAgyModels = """
        Fetching available models...
        gemini-4-argon-low      Gemini 4 Argon (low)
        gemini-4-argon-medium   Gemini 4 Argon (medium)
        gemini-4-argon-high     Gemini 4 Argon (high)
        gemini-4-argon-xhigh    Gemini 4 Argon (xhigh)
        gemini-4-argon-max      Gemini 4 Argon (max)
        claude-sonnet-5-5       Claude Sonnet 5.5
        """;

    private static DriverCapabilityProbe Probe(
        Func<string?>? executableResolver = null,
        Func<string, string, TimeSpan, CancellationToken, Task<string?>>? cliRunner = null,
        Func<string, string?, CancellationToken, Task<IReadOnlyList<string>?>>? lister = null,
        TimeSpan? ttl = null,
        TimeSpan? negativeTtl = null,
        int maxConcurrent = 2)
        => new(
            () => ttl ?? TimeSpan.FromMinutes(10),
            () => negativeTtl ?? TimeSpan.FromMinutes(2),
            executableResolver ?? (() => "/usr/local/bin/agy"),
            cliRunner ?? ((_, _, _, _) => Task.FromResult<string?>(SampleAgyModels)),
            lister,
            maxConcurrent);

    private static DriverCapabilityProbeRequest Agy(string id = "agy", bool refresh = false)
        => new(DriverId: id, RuntimeType: RuntimeType.CliAgy, Adapter: "agy", Endpoint: null, ForceRefresh: refresh);

    private static DriverCapabilityProbeRequest OpenAi(
        string id = "openai",
        string? endpoint = "http://localhost:11434/v1",
        IReadOnlyList<string>? models = null,
        string? defaultModel = null,
        bool refresh = false)
        => new(DriverId: id, RuntimeType: RuntimeType.OpenAiCompatible, Adapter: "openai",
            Endpoint: endpoint, OperatorModels: models, DefaultModel: defaultModel, ForceRefresh: refresh);

    private static DriverCapabilityProbeRequest Claude(string id = "claude")
        => new(DriverId: id, RuntimeType: RuntimeType.CliAgy, Adapter: "claude", Endpoint: null);

    [Fact]
    public async Task Agy_ReturnsProbedModels_WhenCliAnswers()
    {
        var reading = await Probe().ProbeAsync(Agy());

        reading.Should().NotBeNull();
        reading!.ModelSource.Should().Be(CapabilitySourceKind.Probed);
        reading.Models.Select(m => m.Id).Should().Equal("gemini-4-argon", "claude-sonnet-5-5");
    }

    [Fact]
    public async Task Agy_EffortVariantsAreGroupedIntoOneModelWithTheirEfforts()
    {
        var reading = await Probe().ProbeAsync(Agy());

        var argon = reading!.Models.Single(m => m.Id == "gemini-4-argon");
        argon.Name.Should().Be("Gemini 4 Argon");
        argon.Efforts.Should().Equal("low", "medium", "high", "xhigh", "max");
    }

    [Fact]
    public async Task Agy_ModelWithoutEffortSuffix_HasNoEfforts()
    {
        var reading = await Probe().ProbeAsync(Agy());

        reading!.Models.Single(m => m.Id == "claude-sonnet-5-5").Efforts.Should().BeEmpty();
    }

    [Fact]
    public async Task Agy_DeclaresNoContextSelection()
    {
        // agy --help exposes --model/--effort/--mode and no context-size flag, and AgyAdapter
        // forwards none. The picker used to offer 128k..2m on this driver and silently did nothing.
        var reading = await Probe().ProbeAsync(Agy());

        reading!.SupportsContextSelection.Should().BeFalse();
        reading.Models.Should().OnlyContain(m => m.ContextSizes == null);
    }

    [Fact]
    public async Task Agy_SignedOutHost_ReturnsUnavailableWithNoModels()
    {
        // Reproduced against the installed binary: `agy models` prints
        // "Error: Please sign in to view available models." and exits 1, which the runner
        // collapses to null. GetDefaultAgyCatalog() used to answer that with five invented models.
        var reading = await Probe(cliRunner: (_, _, _, _) => Task.FromResult<string?>(null)).ProbeAsync(Agy());

        reading!.ModelSource.Should().Be(CapabilitySourceKind.Unavailable);
        reading.Models.Should().BeEmpty();
        reading.UnavailableReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Agy_MissingBinary_ReturnsUnavailable()
    {
        var reading = await Probe(executableResolver: () => null).ProbeAsync(Agy());

        reading!.ModelSource.Should().Be(CapabilitySourceKind.Unavailable);
        reading.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task Agy_UnparseableOutput_IsReportedAsUnavailableNotAsAnEmptyCatalogue()
    {
        // "The CLI answered in a shape we could not read" and "this account has no models" are
        // different facts; collapsing them would let a format change masquerade as an empty account.
        var reading = await Probe(cliRunner: (_, _, _, _) => Task.FromResult<string?>("some prose only"))
            .ProbeAsync(Agy());

        reading!.ModelSource.Should().Be(CapabilitySourceKind.Unavailable);
        reading.UnavailableReason.Should().Contain("parse");
    }

    [Fact]
    public async Task Agy_NegativeResultIsCached_SignedOutHostIsNotReShelledOnEveryPoll()
    {
        var calls = 0;
        var probe = Probe(cliRunner: (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(null);
        }, negativeTtl: TimeSpan.FromMinutes(2));

        await probe.ProbeAsync(Agy("driver-a"));
        await probe.ProbeAsync(Agy("driver-b"));
        await probe.ProbeAsync(Agy("driver-c"));

        calls.Should().Be(1, "the reading is host-level, so every agy driver shares one answer");
    }

    [Fact]
    public async Task Agy_AllDriversShareOneCacheKey_BecauseTheReadingIsHostLevel()
    {
        var calls = 0;
        var probe = Probe(cliRunner: (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(SampleAgyModels);
        });

        var readings = await Task.WhenAll(
            probe.ProbeAsync(Agy("driver-a")),
            probe.ProbeAsync(Agy("driver-b")),
            probe.ProbeAsync(Agy("driver-c")));

        calls.Should().Be(1);
        readings.Select(r => r!.ModelSource).Should().AllBeEquivalentTo(CapabilitySourceKind.Probed);
    }

    [Fact]
    public async Task ConcurrentRequestsForTheSameDriver_ShellOutOnce()
    {
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var probe = Probe(cliRunner: (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        });

        var first = probe.ProbeAsync(Agy("driver-a"));
        var second = probe.ProbeAsync(Agy("driver-b"));

        await Task.Delay(50);
        gate.SetResult(SampleAgyModels);

        var results = await Task.WhenAll(first, second);

        calls.Should().Be(1, "single-flight: the dashboard fans out over every driver on load");
        results.Should().AllSatisfy(r => r!.ModelSource.Should().Be(CapabilitySourceKind.Probed));
    }

    [Fact]
    public async Task AFollowerDisconnecting_DoesNotCancelTheSharedFlight()
    {
        // The flight is shared, so it must not be bound to whichever request happened to start it,
        // and a caller waiting on it must not be able to take the reading down for everyone else.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var probe = Probe(cliRunner: (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            return finish.Task;
        });

        var leader = probe.ProbeAsync(Agy("driver-a"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var followerCts = new CancellationTokenSource();
        var follower = probe.ProbeAsync(Agy("driver-b"), followerCts.Token);
        await Task.Delay(50);

        followerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower);

        finish.SetResult(SampleAgyModels);
        var reading = await leader;

        reading!.ModelSource.Should().Be(CapabilitySourceKind.Probed,
            "one browser going away must not strand the drivers that stayed");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task TheLeaderIsBoundedByTheProbeTimeout_NotByTheCallerToken()
    {
        // The leader runs the CLI with CancellationToken.None on purpose: cancelling it would
        // cancel the shared reading for every other driver waiting on it. The bound instead comes
        // from the timeout the probe hands the runner, which is what stops a stalled `agy` OAuth
        // prompt from pinning a request thread.
        var observedTimeout = TimeSpan.Zero;
        var probe = Probe(cliRunner: (_, _, timeout, _) =>
        {
            observedTimeout = timeout;
            return Task.FromResult<string?>(SampleAgyModels);
        });

        using var caller = new CancellationTokenSource();
        caller.Cancel();

        // The caller is already gone, yet the read still has to complete for the drivers that
        // joined it. Nothing here should throw.
        var reading = await probe.ProbeAsync(Agy("driver-a"), caller.Token);

        reading!.ModelSource.Should().Be(CapabilitySourceKind.Probed);
        observedTimeout.Should().Be(DriverCapabilityProbe.ProbeTimeout,
            "the timeout, not the request, is what bounds a hung CLI");
    }

    [Fact]
    public async Task AFailingProbe_DoesNotLeaveLaterCallersStranded()
    {
        // The invariant is that a flight always completes. A faulted leader task would be handed to
        // every later caller forever, and a leaked in-flight entry would pin the key to a dead task
        // so the picker stopped ageing.
        var calls = 0;
        var probe = Probe(
            cliRunner: (_, _, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
                return Task.FromResult<string?>(SampleAgyModels);
            },
            negativeTtl: TimeSpan.FromMilliseconds(-1)); // negative entries expire immediately

        var first = await probe.ProbeAsync(Agy("driver-a"));
        var second = await probe.ProbeAsync(Agy("driver-b"));

        first!.ModelSource.Should().Be(CapabilitySourceKind.Unavailable);
        second!.ModelSource.Should().Be(CapabilitySourceKind.Probed,
            "the failure must be remembered for the negative TTL, not forever");
    }

    [Fact]
    public async Task SignedOutHost_KeepsTheNegativeTtlShortSoSigningInTakesEffectSoon()
    {
        // The message the UI shows says "driver not signed in". If that reading were cached for the
        // positive TTL, the picker would stay empty for minutes after the operator signed in.
        var calls = 0;
        var probe = Probe(
            cliRunner: (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<string?>(calls == 1 ? null : SampleAgyModels);
            },
            ttl: TimeSpan.FromMinutes(10),
            negativeTtl: TimeSpan.FromMilliseconds(-1));

        var signedOut = await probe.ProbeAsync(Agy("driver-a"));
        var afterSignIn = await probe.ProbeAsync(Agy("driver-a") );

        signedOut!.ModelSource.Should().Be(CapabilitySourceKind.Unavailable);
        afterSignIn!.ModelSource.Should().Be(CapabilitySourceKind.Probed);
    }

    [Fact]
    public async Task OpenAi_TwoDriversOnDifferentServers_GetDifferentCatalogues()
    {
        // The bug this whole change removes: one hardcoded table per adapter, so two
        // OpenAI-compatible drivers were handed the same six invented models.
        var probe = Probe(lister: (endpoint, _, _) =>
            Task.FromResult<IReadOnlyList<string>>(
                endpoint.Contains("11434")
                    ? new[] { "llama3.3" }
                    : new[] { "qwen3.8-flash-next", "deepseek-r1" }));

        var ollama = await probe.ProbeAsync(OpenAi("ollama", "http://localhost:11434/v1"));
        var other = await probe.ProbeAsync(OpenAi("other", "http://10.0.0.9:8000/v1"));

        ollama!.Models.Select(m => m.Id).Should().Equal("llama3.3");
        other!.Models.Select(m => m.Id).Should().Equal("qwen3.8-flash-next", "deepseek-r1");
    }

    [Fact]
    public async Task OpenAi_MissingEndpoint_IsUnavailableNotAPreset()
    {
        // No URL means we would be probing a server we cannot name. The bundled list used to stand
        // in here and looked exactly like a real answer.
        var reading = await Probe(lister: (_, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "x" }))
            .ProbeAsync(OpenAi(endpoint: null));

        reading!.ModelSource.Should().Be(CapabilitySourceKind.Unavailable);
        reading.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAi_UnreachableServer_IsUnavailable()
    {
        var reading = await Probe(lister: (_, _, _) => Task.FromResult<IReadOnlyList<string>?>(null))
            .ProbeAsync(OpenAi());

        reading!.ModelSource.Should().Be(CapabilitySourceKind.Unavailable);
        reading.UnavailableReason.Should().Contain("localhost:11434");
    }

    [Fact]
    public async Task OpenAi_EmptyModelListIsAnAnswerNotAFailureToPaperOver()
    {
        // A server that answers `{"data":[]}` really has no models. Reporting "unavailable" would
        // send the operator to check a network path that is fine.
        var reading = await Probe(lister: (_, _, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()))
            .ProbeAsync(OpenAi());

        reading!.ModelSource.Should().Be(CapabilitySourceKind.Unavailable);
        reading.UnavailableReason.Should().Contain("no models");
    }

    [Fact]
    public async Task OpenAi_ContextSizesNeverExceedWhatTheRuntimeWillHonour()
    {
        // The picker used to advertise 2m for every model while ResolveModelContextLimit clamped
        // the run to 128k. Sizes are now derived from the same table the adapter applies.
        var reading = await Probe(lister: (_, _, _) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "gpt-4o", "qwen3.8-flash-next" }))
            .ProbeAsync(OpenAi());

        var gpt = reading!.Models.Single(m => m.Id == "gpt-4o");
        gpt.ContextSizes.Should().Contain("128k");
        gpt.ContextSizes.Should().NotContain("200k", "gpt-4o resolves to a 128k window");
        gpt.ContextSizes.Should().NotContain("2m");

        var qwen = reading.Models.Single(m => m.Id == "qwen3.8-flash-next");
        qwen.ContextSizes.Should().Contain("200k", "qwen3 resolves to 512k");
        qwen.ContextSizes.Should().NotContain("1m", "1m exceeds the 512k window the run gets");
    }

    [Fact]
    public async Task OpenAi_ProbedModelsCarryNoEffortLadder()
    {
        // An OpenAI /models response has no effort surface. The old table invented low/medium/high
        // for models whose servers never mentioned one.
        var reading = await Probe(lister: (_, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "llama3.3" }))
            .ProbeAsync(OpenAi());

        reading!.Models.Single().Efforts.Should().BeEmpty();
        reading.EffortSource.Should().BeNull();
    }

    [Fact]
    public async Task OperatorModels_AreMergedAndMarkedAsAnOverride()
    {
        var reading = await Probe(lister: (_, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "llama3.3" }))
            .ProbeAsync(OpenAi(models: new[] { "my-fine-tune" }, defaultModel: "my-default"));

        reading!.ModelSource.Should().Be(CapabilitySourceKind.UserOverride);
        reading.Models.Select(m => m.Id).Should().Equal("my-default", "my-fine-tune", "llama3.3");
    }

    [Fact]
    public async Task OperatorModels_OnAnUnreadableDriver_StillSurfaceTheReason()
    {
        var reading = await Probe(cliRunner: (_, _, _, _) => Task.FromResult<string?>(null))
            .ProbeAsync(Agy() with { OperatorModels = new[] { "hand-typed-model" } });

        reading!.ModelSource.Should().Be(CapabilitySourceKind.UserOverride);
        reading.Models.Select(m => m.Id).Should().Equal("hand-typed-model");
    }

    [Fact]
    public async Task Claude_HasAStaticPresetMatchingTheRealCliContract()
    {
        var reading = await Probe().ProbeAsync(Claude());

        reading!.ModelSource.Should().Be(CapabilitySourceKind.StaticPreset,
            "claude has no discovery command, so its list is a bundled claim about the CLI, not a probe");
        reading.Models.Select(m => m.Id).Should().Contain("sonnet", "opus", "haiku");
        reading.Models.Should().OnlyContain(m => m.Efforts.Contains("xhigh") && m.Efforts.Contains("max"));
    }

    [Fact]
    public async Task Claude_ContextSizesStayInsideWhatAutocompactAccepts()
    {
        // claude --help: --autocompact <auto|tokens>  (auto, or 100k-1M tokens)
        var reading = await Probe().ProbeAsync(Claude());

        var sizes = reading!.Models.SelectMany(m => m.ContextSizes!).Distinct().ToList();
        sizes.Should().Contain("auto");
        sizes.Should().Contain("100k");
        sizes.Should().Contain("1m");
        sizes.Should().NotContain("2m", "the binary rejects anything above 1M");
        sizes.Should().NotContain("50k", "the binary rejects anything below 100k");
    }

    [Fact]
    public async Task Generic_HasNoOpinionToOffer()
    {
        var reading = await Probe().ProbeAsync(new DriverCapabilityProbeRequest(
            DriverId: "custom", RuntimeType: RuntimeType.McpCloud, Adapter: "generic", Endpoint: null));

        reading.Should().BeNull("a custom command has no discovery surface and no bundled list");
    }

    [Fact]
    public async Task ForceRefresh_BypassesTheCache()
    {
        var calls = 0;
        var probe = Probe(cliRunner: (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(SampleAgyModels);
        });

        await probe.ProbeAsync(Agy("driver-a"));
        await probe.ProbeAsync(Agy("driver-a") with { ForceRefresh = true });

        calls.Should().Be(2);
    }

    [Fact]
    public async Task ExpiredPositiveTtl_TriggersAReprobe()
    {
        var calls = 0;
        var probe = Probe(
            cliRunner: (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<string?>(SampleAgyModels);
            },
            ttl: TimeSpan.FromMilliseconds(-1)); // always expired

        await probe.ProbeAsync(Agy("driver-a"));
        await probe.ProbeAsync(Agy("driver-b"));

        calls.Should().Be(2);
    }

    [Fact]
    public async Task SaturatedProbeGate_ServesStaleRatherThanSpawningMoreProcesses()
    {
        // The endpoint is [AllowAnonymous]; without a bound, agentId permutations become a
        // subprocess-spawning primitive. A saturated gate must degrade, not queue.
        //
        // Distinct cache keys are required to exercise the gate at all: two requests for the same
        // key are absorbed by single-flight before they ever reach it.
        var gate = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maxObserved = 0;
        var probe = Probe(
            lister: (_, _, _) =>
            {
                var now = Interlocked.Increment(ref running);
                Max(ref maxObserved, now);
                return gate.Task;
            },
            maxConcurrent: 1);

        var first = probe.ProbeAsync(OpenAi("a", "http://host-a.invalid/v1"));
        await Task.Delay(100);

        var second = probe.ProbeAsync(OpenAi("b", "http://host-b.invalid/v1"));
        await Task.Delay(3_000);

        Volatile.Read(ref maxObserved).Should().Be(1, "at most one live probe per host");

        gate.SetResult(new[] { "llama3.3" });
        var winner = await first;
        var degraded = await second;

        winner!.ModelSource.Should().Be(CapabilitySourceKind.Probed);

        // The degraded answer must explain itself. Null here would be rendered as "this driver type
        // has no discovery surface", which is a different and wrong reason.
        degraded.Should().NotBeNull();
        degraded!.ModelSource.Should().Be(CapabilitySourceKind.Unavailable);
        degraded.UnavailableReason.Should().Contain("already running");
    }

    private static void Max(ref int target, int candidate)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (candidate <= current) return;
        }
        while (Interlocked.CompareExchange(ref target, candidate, current) != current);
    }

    [Fact]
    public void NormalizeAdapter_UsesTheRecordedAdapterNotTheRuntimeEnum()
    {
        // RuntimeType.CliAgy is the schema default for every CLI driver, so a claude driver row
        // created before the adapter field existed would otherwise be probed as agy.
        DriverCapabilityProbe.NormalizeAdapter(new DriverCapabilityProbeRequest(
            DriverId: "claude", RuntimeType: RuntimeType.CliAgy, Adapter: "claude", Endpoint: null))
            .Should().Be("claude");
    }

    [Fact]
    public void NormalizeAdapter_EndpointImpliesOpenAiRegardlessOfRuntimeEnum()
    {
        DriverCapabilityProbe.NormalizeAdapter(new DriverCapabilityProbeRequest(
            DriverId: "x", RuntimeType: RuntimeType.CliAgy, Adapter: null, Endpoint: "http://h:1234/v1"))
            .Should().Be("openai");
    }

    [Fact]
    public void BuildCacheKey_StripsTrailingSlashAndModelsSuffix()
    {
        // Operators paste either the base URL or the full /models path; both must share one entry
        // or a single server gets probed once per spelling.
        var a = DriverCapabilityProbe.BuildCacheKey("openai", OpenAi("a", "http://host:1/v1/"));
        var b = DriverCapabilityProbe.BuildCacheKey("openai", OpenAi("b", "http://host:1/v1"));
        var c = DriverCapabilityProbe.BuildCacheKey("openai", OpenAi("c", "http://host:1/v1/models"));

        a.Should().Be(b).And.Be(c);
    }

    [Fact]
    public void BuildCacheKey_AgyIsHostLevel()
    {
        DriverCapabilityProbe.BuildCacheKey("agy", Agy("driver-a"))
            .Should().Be(DriverCapabilityProbe.BuildCacheKey("agy", Agy("driver-b")));
    }

    [Fact]
    public void ParseAgyModels_IgnoresSpinnerAndBannerLines()
    {
        var raw = "⠋ Fetching available models...\n\nAvailable models:\ngemini-4-argon-low  Gemini 4 Argon (low)\n";

        var models = DriverCapabilityProbe.ParseAgyModels(raw);

        models.Should().ContainSingle().Which.Id.Should().Be("gemini-4-argon");
    }

    [Fact]
    public void ParseAgyModels_NonEffortSuffixesAreNotStripped()
    {
        // Stripping "-nano" would invent a base model the CLI never offered.
        var models = DriverCapabilityProbe.ParseAgyModels("gemini-4-flash-nano  Gemini 4 Flash Nano\n");

        models.Should().ContainSingle().Which.Id.Should().Be("gemini-4-flash-nano");
        models.Single().Efforts.Should().BeEmpty();
    }

    [Fact]
    public void ParseAgyModels_DropsEffortsTheAdapterWouldNotForward()
    {
        // Two invariants at once. An unknown suffix must not become an advertised effort, because
        // AgyAdapter drops anything outside the documented --effort set. And it must not be stripped
        // off the id either: `gemini-x-ultra` is a real id the CLI printed, so it stays a model.
        var models = DriverCapabilityProbe.ParseAgyModels(
            "gemini-x-ultra  Gemini X (ultra)\ngemini-x-high  Gemini X (high)\n");

        var baseModel = models.Single(m => m.Id == "gemini-x");
        baseModel.Efforts.Should().Equal("high");

        var ultra = models.Single(m => m.Id == "gemini-x-ultra");
        ultra.Efforts.Should().BeEmpty("no effort was derived, so none may be offered");
    }

    [Theory]
    [InlineData("gemini-4-argon-high", true)]
    [InlineData("gpt-4o", true)]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("llama3.3", true)]
    [InlineData("o3-mini", true)]
    [InlineData("some", false)]
    [InlineData("Error:", false)]
    [InlineData("Fetching", false)]
    [InlineData("ab", false)]
    public void LooksLikeModelId_RejectsProseBecauseAFalsePositiveInventsASelectableModel(string token, bool expected)
    {
        // Asymmetric failure modes: accepting a prose line puts a fake model in the picker that the
        // user then sends to the driver; rejecting a real one yields an empty list plus a reason.
        DriverCapabilityProbe.LooksLikeModelId(token).Should().Be(expected);
    }

    [Fact]
    public void ExtractEffortSuffix_RecognizesOnlyDocumentedLevels()
    {
        DriverCapabilityProbe.ExtractEffortSuffix("m-xhigh").Should().Be("xhigh");
        DriverCapabilityProbe.ExtractEffortSuffix("m-max").Should().Be("max");
        DriverCapabilityProbe.ExtractEffortSuffix("m-medium").Should().Be("medium");
        DriverCapabilityProbe.ExtractEffortSuffix("m-beta").Should().BeNull();
    }

    [Fact]
    public async Task NullProbe_ReturnsNoReading()
    {
        var reading = await NullDriverCapabilityProbe.Instance.ProbeAsync(Agy());

        reading.Should().BeNull();
    }

}
