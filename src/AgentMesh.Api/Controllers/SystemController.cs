using System.Security.Claims;
using System.Text.Json;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMesh.Api.Controllers;

/// <summary>
/// Reports what the runtime and each driver can actually do.
/// </summary>
/// <remarks>
/// <para>
/// The shape of this endpoint changed: <c>agentRuntimes</c> used to be the whole answer, one
/// hardcoded table per adapter, so every OpenAI-compatible driver was handed the same invented
/// six models and a signed-out <c>agy</c> host got a five-model catalogue it had never read. The
/// baseline map is still returned (the dashboard needs modes and tiers before it knows which
/// drivers exist), but each entry now carries provenance and per-driver readings hang off
/// <c>drivers</c>.
/// </para>
/// <para>
/// The endpoint stays <c>[AllowAnonymous]</c> for dashboard-on-load compatibility, matching the
/// read paths on <c>AgentsController</c>. What anonymous callers cannot do is force a live probe:
/// <c>refresh=true</c> is ignored unless the request is authenticated, because a probe means
/// spawning a subprocess or dialing an arbitrary URL from a request path.
/// </para>
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class SystemController(
    IDriverCapabilityProbe? capabilityProbe = null,
    ILogger<SystemController>? logger = null) : ControllerBase
{
    private readonly IDriverCapabilityProbe _probe = capabilityProbe ?? NullDriverCapabilityProbe.Instance;
    private readonly ILogger<SystemController> _logger = logger ?? NullLogger<SystemController>.Instance;

    [HttpGet("capabilities")]
    public async Task<IActionResult> GetCapabilities(
        [FromQuery] string? agentId = null,
        [FromQuery] bool refresh = false,
        [FromQuery] int limit = DefaultDriverLimit,
        [FromServices] AgentMeshDbContext? db = null,
        CancellationToken ct = default)
    {
        // Anonymous callers get cached readings only. Without this, `refresh=true` plus any number
        // of agentId permutations turns an allow-anonymous read endpoint into a subprocess- and
        // network-fan-out primitive.
        var effectiveRefresh = refresh && IsCallerAuthenticated();

        var runtimes = BuildBaselineRuntimes();
        List<DriverCapabilitiesDto>? drivers = null;

        if (db != null)
        {
            var boundedLimit = Math.Clamp(limit, 1, MaxDriverLimit);
            drivers = await ResolveDriversAsync(db, agentId, effectiveRefresh, boundedLimit, ct);

            // A typo'd agentId used to fall through to the baseline map, which looked like a
            // successful answer about a driver that does not exist. Only assert that when the
            // enumeration itself succeeded.
            if (drivers != null && !string.IsNullOrWhiteSpace(agentId) && drivers.Count == 0)
            {
                return NotFound(new { error = $"No driver registered with id '{agentId}'." });
            }
        }

        return Ok(new SystemCapabilitiesResponse(AgentRuntimes: runtimes)
        {
            // Tells the dashboard this build resolves per-driver capabilities, so an older API
            // that omits the field is distinguishable from a newer API with no drivers.
            SchemaVersion = CapabilitiesSchemaVersion,
            Drivers = drivers
        });
    }

    /// <summary>
    /// Resolves the capability reading for one driver.
    /// </summary>
    /// <remarks>
    /// Exposed as its own endpoint so the dashboard can lazily re-read a single driver when a
    /// session opens, instead of re-resolving every driver on the host.
    /// </remarks>
    [HttpGet("capabilities/driver/{driverId}")]
    public async Task<IActionResult> GetDriverCapabilities(
        string driverId,
        [FromQuery] bool refresh = false,
        [FromServices] AgentMeshDbContext? db = null,
        CancellationToken ct = default)
    {
        if (db == null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Database unavailable." });

        var effectiveRefresh = refresh && IsCallerAuthenticated();
        var drivers = await ResolveDriversAsync(db, driverId, effectiveRefresh, 1, ct);

        if (drivers == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Driver records could not be read right now." });
        }

        return drivers.Count == 0
            ? NotFound(new { error = $"No driver registered with id '{driverId}'." })
            : Ok(drivers[0]);
    }

    private const int CapabilitiesSchemaVersion = 2;
    private const int DefaultDriverLimit = 50;
    private const int MaxDriverLimit = 200;

    /// <summary>
    /// Probes an OpenAI-compatible server for its model list, on operator demand.
    /// </summary>
    /// <remarks>
    /// This is the interactive counterpart to the background capability probe: the operator typed
    /// an endpoint and wants to know if it answers. It is deliberately not cached and takes the
    /// endpoint from the request body rather than from a stored driver, because the point is to
    /// validate a value that has not been saved yet.
    /// </remarks>
    [HttpPost("drivers/test-openai")]
    public async Task<IActionResult> TestOpenAiServer(
        [FromBody] TestOpenAiServerRequest req,
        [FromServices] AgentMeshDbContext? db = null,
        [FromServices] IPersonaContainerManager? containerManager = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Endpoint))
        {
            return BadRequest(new TestOpenAiServerResponse(false, "Endpoint URL is required.", new()));
        }

        var effectiveApiKey = req.ApiKey;
        var effectiveSidecar = req.Sidecar;

        // "Leave unchanged" in the driver form: fall back to the stored key rather than asking the
        // operator to retype a secret they cannot see.
        if (db != null && !string.IsNullOrWhiteSpace(req.AgentId))
        {
            try
            {
                var normalizedId = req.AgentId.Trim().TrimStart('@').ToLowerInvariant();
                var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == normalizedId, ct);
                if (agent != null && !string.IsNullOrWhiteSpace(agent.CapabilitiesJson))
                {
                    using var doc = JsonDocument.Parse(agent.CapabilitiesJson);
                    if (string.IsNullOrWhiteSpace(effectiveApiKey) &&
                        doc.RootElement.TryGetProperty("apiKey", out var keyProp) &&
                        keyProp.ValueKind == JsonValueKind.String)
                    {
                        effectiveApiKey = keyProp.GetString();
                    }

                    if (effectiveSidecar != null && effectiveSidecar.Enabled &&
                        string.IsNullOrWhiteSpace(effectiveSidecar.AuthKey) &&
                        doc.RootElement.TryGetProperty("sidecar", out var scProp) &&
                        scProp.ValueKind == JsonValueKind.Object &&
                        scProp.TryGetProperty("authKey", out var scKeyProp) &&
                        scKeyProp.ValueKind == JsonValueKind.String)
                    {
                        effectiveSidecar.AuthKey = scKeyProp.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                // A malformed stored blob must not stop the operator from testing the endpoint
                // they just typed; the test simply runs without the stored secret.
                _ = ex;
            }
        }

        if (effectiveSidecar != null && effectiveSidecar.Enabled && containerManager != null)
        {
            try
            {
                var sidecarResult = await containerManager.TestDriverEndpointAsync(
                    req.Endpoint, effectiveApiKey, effectiveSidecar, ct);

                if (!sidecarResult.Success)
                {
                    return Ok(new TestOpenAiServerResponse(
                        false, sidecarResult.ErrorMessage ?? "Sidecar endpoint test failed.", new()));
                }

                var sidecarModels = sidecarResult.AvailableModels?
                    .Select(m => new OpenAiModelItemDto(m, m))
                    .ToList() ?? new List<OpenAiModelItemDto>();

                return Ok(new TestOpenAiServerResponse(true, null, sidecarModels));
            }
            catch (Exception ex)
            {
                return Ok(new TestOpenAiServerResponse(false, $"Sidecar connection test failed: {ex.Message}", new()));
            }
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

            using var httpReq = new HttpRequestMessage(
                HttpMethod.Get, DriverModelDiscovery.ToModelsUrl(req.Endpoint));

            if (!string.IsNullOrWhiteSpace(effectiveApiKey))
            {
                httpReq.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", effectiveApiKey.Trim());
            }

            using var res = await client.SendAsync(httpReq, ct);
            if (!res.IsSuccessStatusCode)
            {
                return Ok(new TestOpenAiServerResponse(
                    false, $"Server returned HTTP {(int)res.StatusCode} ({res.ReasonPhrase})", new()));
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            var models = DriverModelDiscovery.ParseOpenAiModelIds(json)
                .Select(id => new OpenAiModelItemDto(id, id))
                .ToList();

            // A 200 with an unparseable body used to be reported as success with zero models,
            // which read as "this server has no models" rather than "this server is not speaking
            // the models API".
            if (models.Count == 0)
            {
                return Ok(new TestOpenAiServerResponse(
                    false,
                    "Server responded successfully but returned no models in the expected `data[].id` shape.",
                    models));
            }

            return Ok(new TestOpenAiServerResponse(true, null, models));
        }
        catch (Exception ex)
        {
            return Ok(new TestOpenAiServerResponse(false, $"Connection failed: {ex.Message}", new()));
        }
    }

    /// <summary>
    /// True when the request carries credentials that actually validated.
    /// </summary>
    /// <remarks>
    /// The dashboard's fallback header is a literal dev bearer token, so "has an Authorization
    /// header" is not a usable signal here; only an authenticated principal is.
    /// </remarks>
    private bool IsCallerAuthenticated() => User?.Identity?.IsAuthenticated == true;

    private async Task<List<DriverCapabilitiesDto>?> ResolveDriversAsync(
        AgentMeshDbContext db,
        string? agentId,
        bool forceRefresh,
        int limit,
        CancellationToken ct)
    {
        IQueryable<Agent> query = db.Agents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(agentId))
        {
            // Same normalization the quota lookup uses: ids are stored lowercased and the UI
            // sometimes passes them with a leading '@'.
            var normalized = agentId.Trim().TrimStart('@').ToLowerInvariant();
            query = query.Where(a => a.Id == normalized);
        }

        List<Agent> agents;
        try
        {
            agents = await query
                .OrderBy(a => a.Id)
                .Take(limit)
                .ToListAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // This endpoint used to read nothing but configuration, so a database outage could not
            // affect it. Per-driver resolution introduced that dependency, and it must degrade the
            // same way a failed probe does: the baseline map still lets the dashboard render mode
            // and tier pickers. Returning null means "unknown", which the client distinguishes from
            // "this host has no drivers" - an empty list here would be a lie about the fleet.
            _logger.LogWarning(ex, "Could not enumerate drivers for capability resolution");
            return null;
        }

        var results = new List<DriverCapabilitiesDto>(agents.Count);

        foreach (var agent in agents)
        {
            var view = DriverCapabilities.Parse(agent.CapabilitiesJson);
            var request = view.ToProbeRequest(agent.Id, agent.RuntimeType, forceRefresh);

            DriverCapabilityReading? reading = null;
            try
            {
                reading = await _probe.ProbeAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // client went away; nothing to answer
            }
            catch (Exception ex)
            {
                // A probe bug must not take the capabilities endpoint down: the dashboard needs
                // the baseline map to render at all. Report the failure instead of hiding it.
                reading = new DriverCapabilityReading(
                    Adapter: DriverAdapters.Canonicalize(request.Adapter) ?? request.Adapter ?? "unknown",
                    Models: Array.Empty<DriverModelCapability>(),
                    ModelSource: CapabilitySourceKind.Unavailable,
                    ObservedAt: DateTimeOffset.UtcNow,
                    SupportsContextSelection: ModelContextLimits.SupportsContextSelection(request.Adapter),
                    UnavailableReason: $"Capability resolution failed: {ex.Message}");
            }

            results.Add(ToDto(agent, view, reading));
        }

        return results;
    }

    private static DriverCapabilitiesDto ToDto(
        Agent agent,
        DriverCapabilitiesView view,
        DriverCapabilityReading? reading)
    {
        var adapter = DriverAdapters.Canonicalize(view.Adapter) ?? view.Adapter;

        if (string.IsNullOrEmpty(adapter))
        {
            adapter = view.Endpoint != null
                ? DriverAdapters.OpenAi
                : agent.RuntimeType switch
                {
                    RuntimeType.OpenAiCompatible or RuntimeType.LocalOllama => DriverAdapters.OpenAi,
                    RuntimeType.CliAgy => DriverAdapters.Agy,
                    RuntimeType.McpCloud => DriverAdapters.Generic,
                    _ => DriverAdapters.Generic
                };
        }

        // No reading means the probe had nothing to say (generic drivers, or a host where probing
        // is disabled). That is "unavailable", not "empty list of everything".
        reading ??= new DriverCapabilityReading(
            Adapter: adapter,
            Models: Array.Empty<DriverModelCapability>(),
            ModelSource: CapabilitySourceKind.Unavailable,
            ObservedAt: DateTimeOffset.UtcNow,
            SupportsContextSelection: ModelContextLimits.SupportsContextSelection(adapter),
            UnavailableReason: "This driver type has no capability discovery surface.");

        var baseline = BaselineRuntimes.For(adapter);

        return new DriverCapabilitiesDto(
            DriverId: agent.Id,
            Name: agent.Name,
            Adapter: adapter,
            Endpoint: view.Endpoint,
            // The driver's own reading wins; the baseline only supplies modes/tiers, which are
            // adapter-level interaction concepts rather than per-install facts.
            Models: reading.Models.Select(ToModelDto).ToList(),
            ModelSource: reading.ModelSource.ToWireValue(),
            ObservedAt: reading.ObservedAt,
            SupportsContextSelection: reading.SupportsContextSelection,
            ContextSizes: reading.SupportsContextSelection
                ? (baseline?.ContextSizes ?? BaselineRuntimes.DefaultContextSizes())
                : null,
            Modes: baseline?.Modes ?? new List<ModeOptionDto>(),
            Tiers: baseline?.Tiers,
            EffortSource: reading.EffortSource,
            UnavailableReason: reading.UnavailableReason,
            DefaultModel: view.DefaultModel,
            OperatorModels: view.OperatorModels);
    }

    private static ModelCapabilityDto ToModelDto(DriverModelCapability model) =>
        new(model.Id, model.Name, model.Efforts, model.ContextSizes);

    /// <summary>
    /// The adapter-level interaction surface: modes and tiers.
    /// </summary>
    /// <remarks>
    /// These are concepts our adapters implement, not provider telemetry, so a static table is
    /// honest here. Models and context windows are provider facts and must not come from a table -
    /// they are resolved per driver by <see cref="IDriverCapabilityProbe"/>.
    /// </remarks>
    private static Dictionary<string, RuntimeCapabilitiesDto> BuildBaselineRuntimes()
        => BaselineRuntimes.All();
}

public record ModeOptionDto(string Id, string Label, string Description);

public record TierOptionDto(string Id, string Label, string Description);

public record ContextSizeOptionDto(string Id, string Label, string Description);

public record ModelCapabilityDto(
    string Id,
    string Name,
    IReadOnlyCollection<string> Efforts,
    IReadOnlyCollection<string>? ContextSizes = null
);

/// <param name="SupportsContextSelection">
/// False when the driver cannot be told its context window, so the dashboard hides the picker.
/// Defaults to false: a new adapter must earn the control rather than inherit one that silently
/// does nothing.
/// </param>
/// <param name="ModelSource">
/// One of the literals produced by <see cref="CapabilitySourceKindExtensions.ToWireValue"/> -
/// <c>probed</c>, <c>declared</c>, <c>static_preset</c>, <c>user_override</c> or <c>unavailable</c>.
/// The UI labels the picker with it so a bundled guess is never presented as a driver answer.
/// The list is stated here for readability only; <c>CapabilitySourceKindValues_AreExactlyTheUnionThe
/// DashboardDeclares</c> is what keeps it in step with the dashboard's union, and it was the absence
/// of that check that let <c>static_preset</c> ship as <c>"staticpreset"</c>.
/// </param>
public record RuntimeCapabilitiesDto(
    string Adapter,
    string Name,
    List<ModeOptionDto> Modes,
    List<ModelCapabilityDto> Models,
    List<TierOptionDto>? Tiers = null,
    List<ContextSizeOptionDto>? ContextSizes = null,
    bool SupportsContextSelection = false,
    // Defaults to "unavailable", not "static_preset": a new baseline entry that forgets to pass a
    // source must not claim its empty model list was verified against a driver.
    string ModelSource = "unavailable",
    DateTimeOffset? ObservedAt = null,
    string? EffortSource = null,
    string? UnavailableReason = null
);

/// <summary>
/// One driver's resolved capabilities.
/// </summary>
/// <param name="ContextSizes">
/// Null when <see cref="SupportsContextSelection"/> is false, so a stale saved value has nothing
/// to render against.
/// </param>
public record DriverCapabilitiesDto(
    string DriverId,
    string Name,
    string Adapter,
    string? Endpoint,
    List<ModelCapabilityDto> Models,
    string ModelSource,
    DateTimeOffset ObservedAt,
    bool SupportsContextSelection,
    List<ContextSizeOptionDto>? ContextSizes,
    List<ModeOptionDto> Modes,
    List<TierOptionDto>? Tiers = null,
    string? EffortSource = null,
    string? UnavailableReason = null,
    string? DefaultModel = null,
    IReadOnlyList<string>? OperatorModels = null
);

public record SystemCapabilitiesResponse(
    Dictionary<string, RuntimeCapabilitiesDto> AgentRuntimes
)
{
    /// <summary>
    /// Bumped when the response shape changes. Callers use it to tell "older API build" from
    /// "newer API build with no drivers".
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Per-driver readings, newest surface. Empty when the API has no database attached.
    /// </summary>
    public List<DriverCapabilitiesDto>? Drivers { get; init; }
}

public record TestOpenAiServerRequest(string Endpoint, string? ApiKey = null, DriverSidecarConfig? Sidecar = null, string? AgentId = null);

public record OpenAiModelItemDto(string Id, string Name);

public record TestOpenAiServerResponse(bool Success, string? Error, List<OpenAiModelItemDto> Models);
