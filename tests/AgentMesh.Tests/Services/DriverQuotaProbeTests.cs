using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The drivers tab used to render a hardcoded quota panel (88% remaining, "Weekly Limit
/// Remaining", a fixed reset date) whenever the provider probe could not run. These tests pin
/// the opposite contract: a reading only exists if the provider actually said so.
/// </summary>
public class DriverQuotaProbeTests
{
    private static DriverQuotaProbeRequest AgyRequest => new(
        DriverId: "agy",
        RuntimeType: RuntimeType.CliAgy,
        Adapter: "agy",
        Endpoint: null);

    [Fact]
    public void Parse_ReturnsNull_ForNonJsonOutput()
    {
        // What an unauthenticated `agy` actually prints on stdout.
        var loginPrompt = "Authentication required. Please visit the URL to log in:\n  https://accounts.google.com/o/oauth2/auth?...";

        AgyDriverQuotaProbe.Parse(loginPrompt).Should().BeNull(
            because: "a login prompt is not a quota reading, and rendering it as one produced fake meters");
    }

    [Fact]
    public void Parse_ReturnsNull_ForJsonWithoutAnyQuotaWindow()
    {
        var json = """
        {
          "command": { "name": "usage", "status": "ok" },
          "rootQuotaEnabled": true
        }
        """;

        AgyDriverQuotaProbe.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Parse_DoesNotInventAFullBar_WhenRemainingFractionIsMissing()
    {
        // The legacy parser defaulted a missing `remaining_fraction` to 1.0, which rendered as
        // "100% available" for a window the provider never quantified.
        var json = """
        {
          "command": { "data": { "buckets": [ { "id": "weekly", "reset_time": "2026-11-03T00:00:00Z" } ] } }
        }
        """;

        AgyDriverQuotaProbe.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Parse_ReadsRealWindows_FromWrappedCliEnvelope()
    {
        var json = """
        {
          "command": {
            "name": "usage",
            "status": "success",
            "data": {
              "plan": "Antigravity Pro",
              "groups": [
                {
                  "name": "Gemini",
                  "description": "Google models",
                  "buckets": [
                    { "id": "gemini-weekly", "name": "Weekly Limit", "window": "weekly",
                      "remaining_fraction": 0.62, "reset_time": "2026-11-03T00:00:00Z" },
                    { "id": "gemini-5h", "name": "5 Hour Limit", "window": "5h",
                      "remaining_fraction": 0.91, "reset_time": "2026-10-28T09:00:00Z" }
                  ]
                }
              ]
            }
          }
        }
        """;

        var reading = AgyDriverQuotaProbe.Parse(json);

        reading.Should().NotBeNull();
        reading!.Provider.Should().Be("Google Antigravity");
        reading.PlanName.Should().Be("Antigravity Pro");
        reading.Groups.Should().HaveCount(1);
        reading.Groups[0].Name.Should().Be("Gemini");
        reading.Groups[0].Buckets.Should().HaveCount(2);
        reading.Groups[0].Buckets[0].RemainingFraction.Should().BeApproximately(0.62, 0.0001);
        reading.Groups[0].Buckets[0].Window.Should().Be("weekly");
        reading.Groups[0].Buckets[0].ResetsAt.Should().NotBeNull();
    }

    [Fact]
    public void Parse_PrefersTheWeeklyWindow_ForTheHeadlineMeter()
    {
        // The weekly window is the one users actually run out of; the scarcest window is the
        // fallback so the headline never over-reports headroom.
        var weekly = """
        { "buckets": [
            { "id": "5h", "window": "5h", "remaining_fraction": 0.95 },
            { "id": "weekly", "window": "weekly", "remaining_fraction": 0.20 }
        ] }
        """;

        AgyDriverQuotaProbe.Parse(weekly)!.PrimaryRemainingFraction.Should().BeApproximately(0.20, 0.0001);

        var noWeekly = """
        { "buckets": [
            { "id": "a", "remaining_fraction": 0.80 },
            { "id": "b", "remaining_fraction": 0.35 }
        ] }
        """;

        AgyDriverQuotaProbe.Parse(noWeekly)!.PrimaryRemainingFraction.Should().BeApproximately(0.35, 0.0001);
    }

    [Fact]
    public void Parse_NormalisesPercentagesAndClampsOutOfRangeValues()
    {
        var percent = """{ "buckets": [ { "id": "w", "remaining_fraction": 62.5 } ] }""";
        AgyDriverQuotaProbe.Parse(percent)!.Groups[0].Buckets[0].RemainingFraction
            .Should().BeApproximately(0.625, 0.0001);

        var over = """{ "buckets": [ { "id": "w", "remaining_fraction": 140 } ] }""";
        AgyDriverQuotaProbe.Parse(over)!.Groups[0].Buckets[0].RemainingFraction
            .Should().BeApproximately(1.0, 0.0001);

        var negative = """{ "buckets": [ { "id": "w", "remaining_fraction": -3 } ] }""";
        AgyDriverQuotaProbe.Parse(negative)!.Groups[0].Buckets[0].RemainingFraction
            .Should().BeApproximately(0.0, 0.0001);
    }

    [Fact]
    public void Parse_AcceptsUnixEpochAndIsoResetTimes()
    {
        var iso = """{ "buckets": [ { "id": "w", "remaining_fraction": 0.5, "reset_time": "2026-11-03T00:00:00Z" } ] }""";
        AgyDriverQuotaProbe.Parse(iso)!.Groups[0].Buckets[0].ResetsAt!.Value.UtcDateTime
            .Should().Be(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc));

        var epoch = """{ "buckets": [ { "id": "w", "remaining_fraction": 0.5, "reset_time": 1793664000 } ] }""";
        AgyDriverQuotaProbe.Parse(epoch)!.Groups[0].Buckets[0].ResetsAt!.Value.ToUnixTimeSeconds()
            .Should().Be(1793664000);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsNull_WhenExecutableIsNotInstalled()
    {
        // This is the exact condition that used to fall through to the hardcoded panel.
        var probed = false;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromSeconds(60),
            () => null,
            (_, _, _, _) =>
            {
                probed = true;
                return Task.FromResult<string?>("SHOULD NOT BE USED");
            });

        var reading = await probe.ProbeAsync(AgyRequest);

        reading.Should().BeNull();
        probed.Should().BeFalse("there is no binary to run, so no process should be spawned");
    }

    [Fact]
    public async Task ProbeAsync_ReturnsParsedReading_WhenCliAnswersWithQuota()
    {
        var calls = 0;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromSeconds(60),
            () => "/usr/local/bin/agy",
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult<string?>(
                    """{ "buckets": [ { "id": "weekly", "window": "weekly", "remaining_fraction": 0.44 } ] }""");
            });

        var reading = await probe.ProbeAsync(AgyRequest);

        reading.Should().NotBeNull();
        reading!.PrimaryRemainingFraction.Should().BeApproximately(0.44, 0.0001);
        reading.Source.Should().Be("agy:/usage");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ProbeAsync_CachesWithinTtl_ButRefetchesOnForcedRefresh()
    {
        var calls = 0;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromMinutes(5),
            () => "/usr/local/bin/agy",
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult<string?>("""{ "buckets": [ { "id": "w", "remaining_fraction": 0.5 } ] }""");
            });

        await probe.ProbeAsync(AgyRequest);
        await probe.ProbeAsync(AgyRequest);
        calls.Should().Be(1, "the dashboard fans this out over every driver on load");

        await probe.ProbeAsync(AgyRequest with { ForceRefresh = true });
        calls.Should().Be(2, "an explicit user refresh must bypass the cache");
    }

    [Fact]
    public async Task ProbeAsync_CachesNegativeResults_SoAnUnauthenticatedHostIsNotReshelled()
    {
        var calls = 0;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromMinutes(5),
            () => "/usr/local/bin/agy",
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult<string?>("Authentication required. Please visit the URL to log in:");
            });

        (await probe.ProbeAsync(AgyRequest)).Should().BeNull();
        (await probe.ProbeAsync(AgyRequest)).Should().BeNull();

        calls.Should().Be(1);
    }

    [Fact]
    public async Task ProbeAsync_UsesTheDocumentedProbeArguments()
    {
        string? capturedArgs = null;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromSeconds(60),
            () => "/usr/local/bin/agy",
            (_, args, _, _) =>
            {
                capturedArgs = args;
                return Task.FromResult<string?>(null);
            });

        await probe.ProbeAsync(AgyRequest);

        capturedArgs.Should().Be("-p /usage --output-format json");
    }

    [Fact]
    public async Task ProbeAsync_SkipsDriversFromOtherProviders()
    {
        var spawned = false;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromSeconds(60),
            () => "/usr/local/bin/agy",
            (_, _, _, _) =>
            {
                spawned = true;
                return Task.FromResult<string?>(null);
            });

        // Copilot/Claude rows are created with RuntimeType.CliAgy (the schema default), so the
        // adapter is the only thing that distinguishes them from a real agy driver.
        var copilot = new DriverQuotaProbeRequest("copilot", RuntimeType.CliAgy, "copilot", null);
        var claude = new DriverQuotaProbeRequest("claude", RuntimeType.CliAgy, "claude", null);

        (await probe.ProbeAsync(copilot)).Should().BeNull();
        (await probe.ProbeAsync(claude)).Should().BeNull();
        spawned.Should().BeFalse("running `agy /usage` for a Copilot driver would report the wrong account");
    }

    [Fact]
    public async Task ProbeAsync_SkipsHttpRuntimes_ThatHaveNoLocalBinary()
    {
        var spawned = false;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromSeconds(60),
            () => "/usr/local/bin/agy",
            (_, _, _, _) =>
            {
                spawned = true;
                return Task.FromResult<string?>(null);
            });

        var ollama = new DriverQuotaProbeRequest("agy-ollama", RuntimeType.LocalOllama, null, "http://ollama:11434/v1");
        var noAdapter = new DriverQuotaProbeRequest("agy", RuntimeType.OpenAiCompatible, null, null);

        (await probe.ProbeAsync(ollama)).Should().BeNull();
        (await probe.ProbeAsync(noAdapter)).Should().BeNull();
        spawned.Should().BeFalse();
    }

    [Fact]
    public async Task ProbeAsync_FallsBackToTheDriverId_WhenNoAdapterWasPersisted()
    {
        // Legacy rows carry no adapter; the id heuristic keeps quota working for them.
        // One probe instance per case: the reading cache is host-level, so reusing one would
        // short-circuit the routing decision under test.
        DriverQuotaProbeRequest Probe(string driverId) =>
            new(driverId, RuntimeType.CliAgy, null, null);

        var calls = 0;
        AgyDriverQuotaProbe NewProbe() => new(
            () => TimeSpan.FromMinutes(5),
            () => "/usr/local/bin/agy",
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult<string?>(
                    """{ "buckets": [ { "id": "w", "remaining_fraction": 0.5 } ] }""");
            });

        (await NewProbe().ProbeAsync(Probe("agy"))).Should().NotBeNull();
        (await NewProbe().ProbeAsync(Probe("agy-work"))).Should().NotBeNull();
        (await NewProbe().ProbeAsync(Probe("antigravity-ci"))).Should().NotBeNull();
        (await NewProbe().ProbeAsync(Probe("magenta"))).Should().BeNull();

        calls.Should().Be(3, "only the three agy-shaped ids should have spawned the CLI");
    }

    [Fact]
    public void IsAntigravity_PrefersTheAdapterOverTheRuntimeAndId()
    {
        // A driver named "agy" that was reconfigured to speak OpenAI-compatible HTTP must not be
        // probed with the local CLI.
        AgyDriverQuotaProbe.IsAntigravity(new DriverQuotaProbeRequest(
            "agy", RuntimeType.CliAgy, "openai", "http://localhost:11434/v1")).Should().BeFalse();

        AgyDriverQuotaProbe.IsAntigravity(new DriverQuotaProbeRequest(
            "agy", RuntimeType.CliAgy, "AGY", null)).Should().BeTrue("adapter matching is case-insensitive");
    }

    [Fact]
    public async Task ProbeAsync_SharesOneReadingAcrossDrivers_OnTheSameHost()
    {
        // `agy /usage` reports the quota of the account signed in on the API host, so two agy
        // drivers legitimately share one reading. Pinning this so the cache is not "fixed" into
        // per-driver keys that would double the number of CLI spawns for identical data.
        var calls = 0;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromMinutes(5),
            () => "/usr/local/bin/agy",
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult<string?>(
                    """{ "buckets": [ { "id": "weekly", "window": "weekly", "remaining_fraction": 0.30 } ] }""");
            });

        var first = await probe.ProbeAsync(new DriverQuotaProbeRequest("agy", RuntimeType.CliAgy, "agy", null));
        var second = await probe.ProbeAsync(new DriverQuotaProbeRequest("agy-work", RuntimeType.CliAgy, "agy", null));

        first.Should().NotBeNull();
        second!.PrimaryRemainingFraction.Should().BeApproximately(first!.PrimaryRemainingFraction, 0.0001);
        calls.Should().Be(1);
    }

    [Fact]
    public void Parse_ReturnsNull_ForTheRealUnauthenticatedAgyEnvelope()
    {
        // Captured from `agy -p /usage --output-format json` on a host with no Google session
        // (the binary had to be killed on timeout; this is what it had written to stdout by then).
        // It carries a `usage` block of zeros, which must NOT be mistaken for quota data.
        var real = """
        {"conversation_id":"","status":"ERROR","response":"","error":"authentication failed or timed out","duration_seconds":0,"num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cached_tokens":0,"total_tokens":0}}
        """;

        AgyDriverQuotaProbe.Parse(real).Should().BeNull(
            because: "a zero-token error envelope is not a quota reading; rendering it produced the fake 88% panel");
    }

    [Fact]
    public void Parse_IgnoresTokenUsage_WhenNoQuotaWindowsArePresent()
    {
        // `usage` is per-turn token accounting, not rate-limit headroom. Conflating the two would
        // draw a meter from data that says nothing about remaining quota.
        var json = """
        { "command": { "data": { "usage": { "input_tokens": 1200, "output_tokens": 300, "total_tokens": 1500 } } } }
        """;

        AgyDriverQuotaProbe.Parse(json).Should().BeNull();
    }

    [Fact]
    public async Task ProbeAsync_ReturnsNull_ForTheUnauthenticatedEnvelope()
    {
        // End-to-end over the real payload shape: binary present, not signed in -> no reading.
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromSeconds(60),
            () => "/usr/local/bin/agy",
            (_, _, _, _) => Task.FromResult<string?>(
                """{"status":"ERROR","error":"authentication failed or timed out","usage":{"total_tokens":0}}"""));

        (await probe.ProbeAsync(AgyRequest)).Should().BeNull();
    }

    [Fact]
    public async Task ProbeAsync_CachesNegativeResultsLongerThanPositiveOnes()
    {
        // The unauthenticated CLI blocks for up to 60s before being killed, so a miss must be
        // remembered longer than a hit or every poll pays for another killed process.
        var calls = 0;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.Zero,            // positive results never cached
            () => TimeSpan.FromMinutes(5),  // negative results cached for 5 minutes
            () => "/usr/local/bin/agy",
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult<string?>("authentication failed or timed out");
            });

        (await probe.ProbeAsync(AgyRequest)).Should().BeNull();
        (await probe.ProbeAsync(AgyRequest)).Should().BeNull();
        (await probe.ProbeAsync(AgyRequest)).Should().BeNull();

        calls.Should().Be(1);
    }

    [Fact]
    public async Task ProbeAsync_KeepsWorkingAfterATransientRunnerFailure()
    {
        var calls = 0;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.Zero,
            () => TimeSpan.Zero,
            () => "/usr/local/bin/agy",
            (_, _, _, _) =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("spawn failed");
                return Task.FromResult<string?>("""{ "buckets": [ { "id": "w", "remaining_fraction": 0.7 } ] }""");
            });

        (await probe.ProbeAsync(AgyRequest)).Should().BeNull();

        var recovered = await probe.ProbeAsync(AgyRequest);
        recovered.Should().NotBeNull();
        recovered!.PrimaryRemainingFraction.Should().BeApproximately(0.70, 0.0001);
    }

    [Fact]
    public void Parse_ReadsTheLegacyNamedSurfaceShape()
    {
        // The parser that existed before this change read `planQuota` / `currentQuota` /
        // `modelQuotas` and camelCase fields. Those field names are the only in-repo evidence of
        // the real payload, so the new parser must keep accepting them - while still returning
        // null when they are absent.
        var json = """
        {
          "rootQuotaEnabled": true,
          "planQuota": {
            "buckets": [
              { "id": "1p-weekly", "name": "Weekly Limit Remaining", "window": "weekly",
                "remaining_fraction": 0.88, "reset_time": "2026-11-03T00:00:00Z" }
            ]
          },
          "currentQuota": {
            "buckets": [
              { "id": "1p-5h", "name": "5 Hour Limit Remaining", "window": "5h",
                "remaining_fraction": 0.41, "reset_time": "2026-10-28T09:00:00Z" }
            ]
          },
          "modelQuotas": {
            "gemini-4-pro": { "remaining_fraction": 0.72, "reset_time": "2026-11-01T00:00:00Z" }
          }
        }
        """;

        var reading = AgyDriverQuotaProbe.Parse(json);

        reading.Should().NotBeNull();
        reading!.Groups.Select(g => g.Name).Should().Contain(new[] { "Plan Quota", "Current Quota", "Per Model" });

        var plan = reading.Groups.First(g => g.Name == "Plan Quota");
        plan.Buckets[0].Name.Should().Be("Weekly Limit Remaining");
        plan.Buckets[0].RemainingFraction.Should().BeApproximately(0.88, 0.0001);

        // The weekly window wins the headline meter even though Current Quota is scarcer.
        reading.PrimaryRemainingFraction.Should().BeApproximately(0.88, 0.0001);
    }

    [Fact]
    public void Parse_ReadsCamelCaseFields()
    {
        var json = """
        { "buckets": [ { "id": "weekly", "remainingFraction": 0.33, "resetTime": "2026-11-03T00:00:00Z" } ] }
        """;

        var reading = AgyDriverQuotaProbe.Parse(json);
        reading!.Groups[0].Buckets[0].RemainingFraction.Should().BeApproximately(0.33, 0.0001);
    }

    [Fact]
    public void Parse_HonoursRootQuotaDisabled_AsNoData()
    {
        var json = """
        { "rootQuotaEnabled": false, "buckets": [ { "id": "w", "remaining_fraction": 0.5 } ] }
        """;

        AgyDriverQuotaProbe.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Parse_ReadsAGroupThatIsItselfTheWindow()
    {
        var json = """
        { "groups": [ { "name": "Gemini", "remaining_fraction": 0.25, "reset_time": "2026-11-03T00:00:00Z" } ] }
        """;

        var reading = AgyDriverQuotaProbe.Parse(json);
        reading!.Groups.Should().HaveCount(1);
        reading.Groups[0].Name.Should().Be("Gemini");
        reading.PrimaryRemainingFraction.Should().BeApproximately(0.25, 0.0001);
    }

    [Fact]
    public void Parse_DropsGroupsWhoseBucketsAllLackARemainingFraction()
    {
        // A provider that returns window metadata but no numbers must not render as "100% left".
        var json = """
        { "groups": [ { "name": "Gemini", "buckets": [ { "id": "w", "reset_time": "2026-11-03T00:00:00Z" } ] } ] }
        """;

        AgyDriverQuotaProbe.Parse(json).Should().BeNull();
    }

    [Fact]
    public async Task NullProbe_AlwaysReturnsNull()
    {
        var request = new DriverQuotaProbeRequest("agy", RuntimeType.CliAgy, "agy", null);

        (await NullDriverQuotaProbe.Instance.ProbeAsync(request)).Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // Thundering herd on the cold cache.
    //
    // The dashboard fans GET /api/agents/{id}/usage over every driver on load, and every agy
    // driver on a host resolves to the *same* executable and therefore the same reading. Without
    // single-flight, N drivers with a cold cache spawn N concurrent `agy` processes. That is
    // expensive when it works, and pathological when it does not: the real binary blocks for up
    // to 60s waiting for an OAuth code when unauthenticated (verified against the installed CLI),
    // so an unauthenticated host with several agy drivers stalls N probe slots at once.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_ShellOutsExactlyOnce_PerConcurrentFanout_OnAColdCache()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromSeconds(60),
            () => "/usr/local/bin/agy",
            async (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                await release.Task;
                return """{ "buckets": [ { "id": "weekly", "window": "weekly", "remaining_fraction": 0.44 } ] }""";
            });

        var requests = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => probe.ProbeAsync(AgyRequest)))
            .ToArray();

        await WaitUntil(() => Volatile.Read(ref calls) >= 1);
        await Task.Delay(150); // let the remaining callers pile onto the same flight

        release.SetResult();
        var readings = await Task.WhenAll(requests);

        Volatile.Read(ref calls).Should().Be(1,
            because: "concurrent callers share one host-level reading, so they need one CLI process");
        readings.Should().OnlyContain(r => r != null);
        readings[0]!.PrimaryRemainingFraction.Should().BeApproximately(0.44, 0.0001);
    }

    [Fact]
    public async Task ProbeAsync_DoesNotLetOneAbortedRequestCancel_AnotherDriversReading()
    {
        // The flight is shared, so it must not be bound to whichever request happened to start it:
        // a browser cancelling one driver's card used to take the reading down for every other
        // driver waiting on it.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromSeconds(60),
            () => "/usr/local/bin/agy",
            async (_, _, _, ct) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
                return """{ "buckets": [ { "id": "weekly", "window": "weekly", "remaining_fraction": 0.62 } ] }""";
            });

        using var aborted = new CancellationTokenSource();
        var first = probe.ProbeAsync(AgyRequest, aborted.Token);
        await started.Task;

        var second = probe.ProbeAsync(AgyRequest, CancellationToken.None);
        aborted.Cancel();

        release.SetResult();

        (await second).Should().NotBeNull("a shared reading must survive the first caller disconnecting");
        (await first).Should().NotBeNull();
        Volatile.Read(ref calls).Should().Be(1);
    }

    [Fact]
    public async Task ProbeAsync_ClearsTheInFlightEntry_AfterCompletion_SoLaterProbesCanRunAgain()
    {
        // A leaked in-flight entry would pin the probe to a completed task forever, so the panel
        // would stop ageing and never re-read. TTL 0 makes every call want a fresh reading.
        var calls = 0;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.Zero,
            () => TimeSpan.Zero,
            () => "/usr/local/bin/agy",
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult<string?>(
                    """{ "buckets": [ { "id": "w", "remaining_fraction": 0.5 } ] }""");
            });

        (await probe.ProbeAsync(AgyRequest)).Should().NotBeNull();
        (await probe.ProbeAsync(AgyRequest)).Should().NotBeNull();
        (await probe.ProbeAsync(AgyRequest)).Should().NotBeNull();

        calls.Should().Be(3);
    }

    [Fact]
    public async Task ProbeAsync_RecoversAfterAFlightFails_RatherThanPoisoningItself()
    {
        // Lazy<Task> caches faults by default. If a failed flight were handed to every later
        // caller, one bad CLI invocation would blank the panel until the process restarted.
        var calls = 0;
        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.Zero,
            () => TimeSpan.Zero,
            () => "/usr/local/bin/agy",
            (_, _, _, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    return Task.FromException<string?>(new InvalidOperationException("cli exploded"));
                }

                return Task.FromResult<string?>(
                    """{ "buckets": [ { "id": "w", "remaining_fraction": 0.7 } ] }""");
            });

        (await probe.ProbeAsync(AgyRequest)).Should().BeNull();

        var recovered = await probe.ProbeAsync(AgyRequest with { ForceRefresh = true });
        recovered.Should().NotBeNull();
        recovered!.PrimaryRemainingFraction.Should().BeApproximately(0.7, 0.0001);
    }

    [Fact]
    public async Task ProbeAsync_CancellingAFollower_LeavesTheSharedFlightIntact()
    {
        // The mirror image of the abort test: a caller hanging up must detach itself without
        // taking the reading down for the drivers still waiting on the same flight.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromSeconds(60),
            () => "/usr/local/bin/agy",
            async (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task;
                return """{ "buckets": [ { "id": "weekly", "window": "weekly", "remaining_fraction": 0.62 } ] }""";
            });

        using var abandoning = new CancellationTokenSource();

        var leader = probe.ProbeAsync(AgyRequest);
        await started.Task;

        var quitter = probe.ProbeAsync(AgyRequest, abandoning.Token);
        var survivor = probe.ProbeAsync(AgyRequest);

        await WaitUntil(() => !quitter.IsCompleted && !survivor.IsCompleted);
        abandoning.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quitter);

        release.SetResult();

        (await leader)!.PrimaryRemainingFraction.Should().BeApproximately(0.62, 0.0001);
        (await survivor)!.PrimaryRemainingFraction.Should().BeApproximately(0.62, 0.0001);
        Volatile.Read(ref calls).Should().Be(1);
    }

    [Fact]
    public async Task ProbeAsync_HandsFollowersTheSameReading_TheLeaderProduced()
    {
        // Sharing one flight means sharing its outcome. Followers must observe the leader's value
        // (including "null"), never re-run the CLI and never see the leader's exception.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var probe = new AgyDriverQuotaProbe(
            () => TimeSpan.FromSeconds(60),
            () => "/usr/local/bin/agy",
            async (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("cli exploded");
            });

        var leader = probe.ProbeAsync(AgyRequest);
        await started.Task;

        var followers = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => probe.ProbeAsync(AgyRequest)))
            .ToArray();

        await WaitUntil(() => followers.All(f => !f.IsCompleted));
        release.SetResult();

        var results = await Task.WhenAll(new[] { leader }.Concat(followers));

        results.Should().OnlyContain(r => r == null, because: "a failed read is reported, not thrown");
        Volatile.Read(ref calls).Should().Be(1);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the allotted time.");
            }

            await Task.Delay(10);
        }
    }
}
