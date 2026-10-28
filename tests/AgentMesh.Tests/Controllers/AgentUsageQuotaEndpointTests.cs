using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// Pins the HTTP contract of GET /api/agents/{id}/usage (and its /api/drivers alias), which is
/// what the drivers tab actually calls.
///
/// The probe-level tests assert that <see cref="IDriverQuotaProbe"/> never invents a reading.
/// These assert the *other* half: that the controller does not re-invent one either. Before the
/// fix the controller fell through to a hardcoded `defaultAgyQuota` panel (88% remaining, "Weekly
/// Limit Remaining", a fabricated reset date) and returned a second invented panel for Copilot
/// unconditionally. A regression that reintroduces a fallback at the controller seam - while
/// leaving the probe honest - would still put fake numbers on the drivers tab, and only these
/// tests catch it.
/// </summary>
public class AgentUsageQuotaEndpointTests
{
    [Fact]
    public async Task GetAgentUsage_ReturnsNullQuota_WhenProbeHasNoReading()
    {
        // Arrange: an agy driver on a host where the provider cannot be read (not installed, or
        // not signed in). This is the exact situation that used to render the fake 88% panel.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "agy-core",
            Name = "Agy Core",
            TrustTier = TrustTier.SeniorManager,
            RuntimeType = RuntimeType.CliAgy,
            CapabilitiesJson = """{"adapter":"agy","capabilities":["chat"]}"""
        });
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(reading: null);
        var controller = new AgentsController(db, new ApiKeyService(), null, probe);

        // Act
        var usage = await AssertUsageAsync(controller, "agy-core");

        // Assert
        usage.ProviderQuota.Should().BeNull(
            because: "the endpoint must surface 'no reading' as null so the UI says quota n/a; " +
                     "substituting a plausible default is the bug this pins shut");
    }

    [Fact]
    public async Task GetAgentUsage_SurfacesTheProvidersReadingVerbatim()
    {
        // Arrange: the provider DID answer. The reading must reach the client unaltered, with its
        // provenance (source + observedAt) intact, so the UI can age it instead of implying liveness.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "agy-core",
            Name = "Agy Core",
            TrustTier = TrustTier.SeniorManager,
            RuntimeType = RuntimeType.CliAgy,
            CapabilitiesJson = """{"adapter":"agy","capabilities":["chat"]}"""
        });
        await db.SaveChangesAsync();

        var observedAt = new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);
        var reading = new DriverQuotaReading(
            Provider: "Google Antigravity",
            PlanName: "Standard",
            Source: "agy:/usage",
            ObservedAt: observedAt,
            PrimaryRemainingFraction: 0.42,
            Groups: new[]
            {
                new DriverQuotaGroup(
                    "Gemini Models",
                    "Gemini Flash & Pro",
                    new[]
                    {
                        new DriverQuotaBucket(
                            Id: "gemini-weekly",
                            Name: "Weekly Limit Remaining",
                            Description: "Refreshes weekly",
                            Window: "weekly",
                            RemainingFraction: 0.42,
                            ResetsAt: new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero))
                    })
            });

        var controller = new AgentsController(db, new ApiKeyService(), null, new RecordingProbe(reading));

        // Act
        var usage = await AssertUsageAsync(controller, "agy-core");

        // Assert
        usage.ProviderQuota.Should().NotBeNull();
        var quota = usage.ProviderQuota!;
        quota.Provider.Should().Be("Google Antigravity");
        quota.PlanName.Should().Be("Standard");
        quota.Source.Should().Be("agy:/usage");
        quota.ObservedAt.Should().Be(observedAt);
        quota.PrimaryRemainingFraction.Should().BeApproximately(0.42, 0.0001);

        var bucket = quota.Groups.Single().Buckets.Single();
        bucket.Id.Should().Be("gemini-weekly");
        bucket.RemainingFraction.Should().BeApproximately(0.42, 0.0001);
        bucket.ResetTime.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAgentUsage_RequestsAForceRefresh_OnlyWhenTheClientAskedForOne()
    {
        // The dashboard's refresh button is an explicit user action and must bypass the probe's
        // TTL cache; the background fan-out on page load must not, or every mount would shell out
        // to the CLI once per driver.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "agy-core",
            Name = "Agy Core",
            TrustTier = TrustTier.SeniorManager,
            RuntimeType = RuntimeType.CliAgy,
            CapabilitiesJson = """{"adapter":"agy"}"""
        });
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(reading: null);
        var controller = new AgentsController(db, new ApiKeyService(), null, probe);

        await controller.GetAgentUsage("agy-core");
        probe.Requests.Single().ForceRefresh.Should().BeFalse("a page-load fan-out must hit the cache");

        await controller.GetAgentUsage("agy-core", refresh: true);
        probe.Requests.Last().ForceRefresh.Should().BeTrue("an explicit refresh must bypass the cache");
    }

    [Fact]
    public async Task GetAgentUsage_PassesThePersistedAdapterSoTheRightProviderIsProbed()
    {
        // RuntimeType.CliAgy is the schema default for copilot and claude rows too, so the adapter
        // recorded in the capabilities blob is the only trustworthy discriminator. If the request
        // stopped carrying it, an agy probe could be run against a Copilot driver and report the
        // wrong account's quota - real-looking, but not that driver's data.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "copilot",
            Name = "GitHub Copilot CLI",
            TrustTier = TrustTier.SeniorManager,
            RuntimeType = RuntimeType.CliAgy,
            CapabilitiesJson = """{"adapter":"copilot","capabilities":["chat"]}"""
        });
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(reading: null);
        var controller = new AgentsController(db, new ApiKeyService(), null, probe);

        await AssertUsageAsync(controller, "copilot");

        var request = probe.Requests.Single();
        request.Adapter.Should().Be("copilot");
        request.RuntimeType.Should().Be(RuntimeType.CliAgy);
    }

    [Fact]
    public async Task GetAgentUsage_ReportsLifetimeTokensAsZero_WhenNoSessionRecordedAny()
    {
        // Token totals come from this mesh's own session records, never from a provider guess.
        // A driver with no sessions must read as zero, not as a plausible non-zero baseline.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "agy-silent",
            Name = "Agy Silent",
            TrustTier = TrustTier.SeniorManager,
            RuntimeType = RuntimeType.CliAgy,
            CapabilitiesJson = """{"adapter":"agy"}"""
        });
        await db.SaveChangesAsync();

        var controller = new AgentsController(db, new ApiKeyService(), null, new RecordingProbe(null));

        var usage = await AssertUsageAsync(controller, "agy-silent");

        usage.SessionCount.Should().Be(0);
        usage.LifetimeTokens.TotalTokens.Should().Be(0);
        usage.LifetimeTokens.InputTokens.Should().Be(0);
        usage.LifetimeTokens.OutputTokens.Should().Be(0);
        usage.LifetimeTokens.ThinkingTokens.Should().Be(0);
        usage.ProviderQuota.Should().BeNull();
    }

    private static async Task<AgentUsageResponse> AssertUsageAsync(AgentsController controller, string id)
    {
        var result = await controller.GetAgentUsage(id);
        return result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<AgentUsageResponse>().Subject;
    }

    /// <summary>
    /// Stands in for the real probe so the test controls whether the provider answered, and records
    /// what the controller asked for.
    /// </summary>
    private sealed class RecordingProbe(DriverQuotaReading? reading) : IDriverQuotaProbe
    {
        private readonly List<DriverQuotaProbeRequest> _requests = new();

        public IReadOnlyList<DriverQuotaProbeRequest> Requests => _requests;

        public Task<DriverQuotaReading?> ProbeAsync(DriverQuotaProbeRequest request, CancellationToken ct = default)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return Task.FromResult(reading);
        }
    }
}
