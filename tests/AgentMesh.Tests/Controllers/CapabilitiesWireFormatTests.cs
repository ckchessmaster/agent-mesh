using System.Net;
using System.Text.Json;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Services;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StackExchange.Redis;
using Temporalio.Client;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// The capability payload as it actually goes over the wire.
/// </summary>
/// <remarks>
/// The controller-level tests call the action directly, which exercises the C# objects but not the
/// JSON the dashboard parses. Every field name in this endpoint is consumed by TypeScript
/// (<c>src/AgentMesh.Dashboard/src/types/index.ts</c>), and a rename or a casing change there fails
/// silently: the picker just renders empty and looks like a driver problem. These tests pin the
/// serialized shape so the two sides cannot drift apart unnoticed.
/// </remarks>
public class CapabilitiesWireFormatTests : IClassFixture<WebApplicationFactory<AgentMesh.Api.Program>>, IDisposable
{

    private readonly WebApplicationFactory<AgentMesh.Api.Program> _factory;
    private readonly StubProbe _probe = new();

    // Hoisted, not inlined into UseInMemoryDatabase: that lambda runs every time a context is
    // created, so a Guid.NewGuid() there gives each scope its own empty database and the seed in
    // the constructor becomes invisible to the request.
    private readonly string _dbName = "CapabilitiesWireDb_" + Guid.NewGuid();

    public CapabilitiesWireFormatTests(WebApplicationFactory<AgentMesh.Api.Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                // Swap the provider. Removing DbContextOptions<...> alone is not enough on EF Core
                // 10: AddDbContext composes IDbContextOptionsConfiguration<T> registrations, so the
                // Aspire-registered UseNpgsql configuration survives and both providers end up on one
                // DbContextOptions - which EF rejects at query time, not at registration.
                var stale = services.Where(d =>
                    d.ServiceType == typeof(DbContextOptions<AgentMeshDbContext>) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    d.ServiceType == typeof(AgentMeshDbContext) ||
                    d.ServiceType.Name.StartsWith("IDbContextOptionsConfiguration") ||
                    d.ServiceType.Namespace?.StartsWith("Npgsql") == true).ToList();
                foreach (var d in stale) services.Remove(d);

                services.AddDbContext<AgentMeshDbContext>(opts => opts.UseInMemoryDatabase(_dbName));

                var mockStorage = new Mock<IStorageService>();
                mockStorage.Setup(s => s.EnsureBucketExistsAsync(default)).Returns(Task.CompletedTask);
                services.AddSingleton(mockStorage.Object);

                var mockRedis = new Mock<IConnectionMultiplexer>();
                var mockDb = new Mock<IDatabase>();
                mockRedis.Setup(r => r.IsConnected).Returns(true);
                mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);
                mockDb.Setup(d => d.PingAsync(It.IsAny<CommandFlags>())).ReturnsAsync(TimeSpan.FromMilliseconds(2));
                services.AddSingleton(mockRedis.Object);

                var mockTemporal = new Mock<ITemporalClient>();
                services.AddSingleton(mockTemporal.Object);

                // Replace the real probe: it shells out to `agy` and calls out to provider servers,
                // neither of which belongs in a unit test.
                var existing = services.Where(d => d.ServiceType == typeof(IDriverCapabilityProbe)).ToList();
                foreach (var d in existing) services.Remove(d);
                services.AddSingleton<IDriverCapabilityProbe>(_probe);
            });
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
        db.Database.EnsureCreated();
        if (!db.Agents.Any(a => a.Id == "agy-core"))
        {
            db.Agents.Add(new Agent
            {
                Id = "agy-core",
                Name = "Agy Core",
                Role = "Lead",
                Status = "online",
                TrustTier = TrustTier.Intern,
                RuntimeType = RuntimeType.CliAgy,
                CapabilitiesJson = """{"adapter":"agy","capabilities":["chat"]}"""
            });
            db.SaveChanges();
        }
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Capabilities_SerializesFieldNamesTheDashboardTypesDeclare()
    {
        _probe.Reading = new DriverCapabilityReading(
            Adapter: "agy",
            Models: new[]
            {
                new DriverModelCapability("gemini-4-argon", "Gemini 4 Argon", new[] { "high", "max" }, ContextSizes: null)
            },
            ModelSource: CapabilitySourceKind.Probed,
            ObservedAt: new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero),
            SupportsContextSelection: false,
            EffortSource: "derived:agy-model-suffixes");

        using var doc = await GetAsync();

        // src/AgentMesh.Dashboard/src/types/index.ts declares exactly these keys.
        Root(doc).TryGetProperty("agentRuntimes", out _).Should().BeTrue();
        Root(doc).TryGetProperty("schemaVersion", out _).Should().BeTrue();
        Root(doc).TryGetProperty("drivers", out _).Should().BeTrue();

        var driver = Drivers(doc)[0];
        foreach (var property in new[]
                 {
                     "driverId", "name", "adapter", "models", "modelSource", "observedAt",
                     "supportsContextSelection"
                 })
        {
            driver.TryGetProperty(property, out _).Should().BeTrue(
                $"DriverCapabilities in the dashboard declares `{property}`; a rename here silently " +
                "empties the picker and looks like a driver fault");
        }

        var model = driver.GetProperty("models")[0];
        model.GetProperty("id").GetString().Should().Be("gemini-4-argon");
        model.GetProperty("efforts").EnumerateArray().Select(e => e.GetString()).Should().Equal("high", "max");
    }

    [Fact]
    public async Task Capabilities_SerializesTheSourceKindAsTheStringsTheUiSwitchesOn()
    {
        // The dashboard switches on these exact literals (capabilitySourceLabel in
        // utils/driverCapabilities.ts). Serializing the enum as an int would fall every case through
        // to the default and label everything "unavailable".
        _probe.Reading = new DriverCapabilityReading(
            Adapter: "agy",
            Models: new[] { new DriverModelCapability("m", "M", Array.Empty<string>()) },
            ModelSource: CapabilitySourceKind.Probed,
            ObservedAt: DateTimeOffset.UtcNow,
            SupportsContextSelection: false);

        using var doc = await GetAsync();

        var driver = Drivers(doc)[0];
        driver.GetProperty("modelSource").ValueKind.Should().Be(JsonValueKind.String);
        driver.GetProperty("modelSource").GetString().Should().Be("probed");

        var runtime = Root(doc).GetProperty("agentRuntimes").GetProperty("agy");
        runtime.GetProperty("modelSource").GetString().Should().Be("unavailable");
    }

    [Theory]
    [InlineData(CapabilitySourceKind.Probed, "probed")]
    [InlineData(CapabilitySourceKind.Declared, "declared")]
    [InlineData(CapabilitySourceKind.StaticPreset, "static_preset")]
    [InlineData(CapabilitySourceKind.UserOverride, "user_override")]
    [InlineData(CapabilitySourceKind.Unavailable, "unavailable")]
    public async Task Capabilities_EverySourceKindSerializesToTheLiteralTheUiSwitchesOn(
        CapabilitySourceKind kind,
        string expected)
    {
        // The previous version of this file pinned only "probed", which left the multi-word members
        // untested and broken: the DTO was produced with `ToString().ToLowerInvariant()`, so
        // StaticPreset became "staticpreset" and UserOverride became "useroverride". Neither is in
        // the UI's union, so normalizeSource fell through to 'unavailable'. The model list still
        // rendered - App.tsx branches on list length, not on source - but the caption beneath it
        // read "Model list unavailable", contradicting the picker above it. Silent, and confined to
        // the two sources that are not the happy path, which is why one pinned value passed.
        _probe.Reading = new DriverCapabilityReading(
            Adapter: "agy",
            Models: new[] { new DriverModelCapability("m", "M", Array.Empty<string>()) },
            ModelSource: kind,
            ObservedAt: DateTimeOffset.UtcNow,
            SupportsContextSelection: false);

        using var doc = await GetAsync();

        Drivers(doc)[0].GetProperty("modelSource").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task Capabilities_BaselineRuntimeCarriesAnEmptyModelArrayNotNull()
    {
        // The dashboard does `currentRuntimeCaps?.models || []`, so null is survivable, but an
        // absent property is a different shape than an empty one and the `.length` guards assume an
        // array. Pinning it keeps the baseline renderable without defensive code.
        using var doc = await GetAsync();

        var runtime = Root(doc).GetProperty("agentRuntimes").GetProperty("agy");
        runtime.GetProperty("models").ValueKind.Should().Be(JsonValueKind.Array);
        runtime.GetProperty("models").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Capabilities_SerializesSupportsContextSelectionAsABoolean()
    {
        // The dashboard reads `reading.supportsContextSelection === true`. A string or missing value
        // would evaluate false and hide the picker on drivers that do support selection - the
        // opposite failure, and equally invisible.
        _probe.Reading = new DriverCapabilityReading(
            Adapter: "claude",
            Models: new[]
            {
                new DriverModelCapability("sonnet", "Sonnet", new[] { "high" }, ContextSizes: new[] { "200k" })
            },
            ModelSource: CapabilitySourceKind.StaticPreset,
            ObservedAt: DateTimeOffset.UtcNow,
            SupportsContextSelection: true);

        using var doc = await GetAsync();

        var driver = Drivers(doc)[0];
        driver.GetProperty("supportsContextSelection").ValueKind.Should().Be(JsonValueKind.True);
    }

    [Fact]
    public async Task Capabilities_OmitsNullContextSizesRatherThanEmittingAnEmptyArray()
    {
        // null and [] mean different things here: null is "this driver cannot be told its window",
        // [] is "it can, but has no options". The dashboard hides the control only for the former,
        // so collapsing them would show an empty picker on agy.
        _probe.Reading = new DriverCapabilityReading(
            Adapter: "agy",
            Models: new[] { new DriverModelCapability("m", "M", Array.Empty<string>(), ContextSizes: null) },
            ModelSource: CapabilitySourceKind.Probed,
            ObservedAt: DateTimeOffset.UtcNow,
            SupportsContextSelection: false);

        using var doc = await GetAsync();

        var model = Drivers(doc)[0].GetProperty("models")[0];
        if (model.TryGetProperty("contextSizes", out var sizes))
        {
            sizes.ValueKind.Should().Be(JsonValueKind.Null,
                "an empty array would read as 'selectable but no options' and render an empty picker");
        }
    }

    [Fact]
    public async Task Capabilities_UnavailableReadingCarriesItsReasonOnTheWire()
    {
        // The whole point of the change: the UI shows this string instead of a fabricated list, so
        // it has to survive serialization.
        _probe.Reading = new DriverCapabilityReading(
            Adapter: "agy",
            Models: Array.Empty<DriverModelCapability>(),
            ModelSource: CapabilitySourceKind.Unavailable,
            ObservedAt: DateTimeOffset.UtcNow,
            SupportsContextSelection: false,
            UnavailableReason: "The agy CLI returned no model list; it is probably not signed in on the API host.");

        using var doc = await GetAsync();

        var driver = Drivers(doc)[0];
        driver.GetProperty("modelSource").GetString().Should().Be("unavailable");
        driver.GetProperty("models").GetArrayLength().Should().Be(0);
        driver.GetProperty("unavailableReason").GetString().Should().Contain("not signed in");
    }

    [Fact]
    public async Task Capabilities_AnonymousRequestSucceedsWithoutTouchingTheProbeWithRefresh()
    {
        // The endpoint is [AllowAnonymous] for dashboard-on-load compatibility. `?refresh=true` must
        // not turn it into a fan-out primitive for unauthenticated callers.
        _probe.Reading = null;

        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/system/capabilities?refresh=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _probe.Requests.Should().ContainSingle();
        _probe.Requests[0].ForceRefresh.Should().BeFalse();
    }

    private async Task<JsonDocument> GetAsync()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/system/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(json);
    }


    [Fact]
    public void CapabilitySourceKindValues_AreExactlyTheUnionTheDashboardDeclares()
    {
        // The test above pins each member's literal. This one closes the other direction, which is
        // how the `declared` source would have been lost: adding a CapabilitySourceKind on the
        // server without teaching normalizeSource about it makes a populated model list render as
        // "Model list unavailable", and no C# test can see that because the C# side is self-consistent.
        //
        // Reading the TypeScript from disk rather than restating it here is deliberate. A hardcoded
        // expected-set in C# is a second copy of the contract and drifts exactly like the code it
        // guards. If this test ever becomes awkward because CI does not check out the dashboard, the
        // right fix is a generated contract file, not deleting the assertion.
        var union = ReadDashboardUnion();

        var csharp = Enum.GetValues<CapabilitySourceKind>()
            .Select(v => v.ToWireValue())
            .ToList();

        csharp.Should().OnlyHaveUniqueItems("two members sharing a wire value would be indistinguishable in the UI");

        foreach (var value in csharp)
        {
            union.Should().Contain(value,
                $"normalizeSource falls through to 'unavailable' for anything outside its union, so " +
                $"'{value}' would render a real model list under a 'Model list unavailable' caption");
        }

        foreach (var value in union)
        {
            csharp.Should().Contain(value,
                $"the dashboard handles '{value}' but the API can no longer produce it - dead branch");
        }
    }

    [Fact]
    public void CapabilitySourceKindValues_RoundTripThroughTheWireMapping()
    {
        // ToWireValue is the only sanctioned conversion. Guarding against a future edit that
        // reintroduces ToString().ToLowerInvariant(), which was the original bug.
        var controller = File.ReadAllText(FindRepoFile("src/AgentMesh.Api/Controllers/SystemController.cs"));

        controller.Should().NotContain("ModelSource.ToString().ToLowerInvariant()",
            "multi-word members lowercase to staticpreset/useroverride, which are not in the UI union");
        controller.Should().Contain("ModelSource.ToWireValue()");
    }

    /// <summary>
    /// The members of the `CapabilitySource` union in utils/driverCapabilities.ts.
    /// </summary>
    private static List<string> ReadDashboardUnion()
    {
        var source = File.ReadAllText(
            FindRepoFile("src/AgentMesh.Dashboard/src/utils/driverCapabilities.ts"));

        var declaration = System.Text.RegularExpressions.Regex.Match(
            source,
            "export type CapabilitySource =[^;]+;",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        declaration.Success.Should().BeTrue(
            "the dashboard must declare the CapabilitySource union this endpoint's modelSource is matched against");

        return System.Text.RegularExpressions.Regex.Matches(declaration.Value, "'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgentMesh.slnx")))
        {
            dir = dir.Parent;
        }

        var full = Path.Combine(dir?.FullName ??
            throw new InvalidOperationException("repo root not found"), relativePath);

        return File.Exists(full) ? full : throw new FileNotFoundException($"could not locate {relativePath}", full);
    }

    private static JsonElement Root(JsonDocument doc) => doc.RootElement;

    private static JsonElement[] Drivers(JsonDocument doc)
        => Root(doc).GetProperty("drivers").EnumerateArray().ToArray();
}

/// <summary>
/// A probe that returns whatever a test asks for and records what it was asked about.
/// </summary>
public sealed class StubProbe : IDriverCapabilityProbe
{
    public DriverCapabilityReading? Reading { get; set; }

    public List<DriverCapabilityProbeRequest> Requests { get; } = new();

    public Task<DriverCapabilityReading?> ProbeAsync(
        DriverCapabilityProbeRequest request,
        CancellationToken ct = default)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(Reading);
    }
}
