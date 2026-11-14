using System.Security.Claims;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Services;

/// <summary>
/// The identity and project boundary a memory write is attributed to.
/// </summary>
/// <param name="AgentId">Normalized agent identity, or null when the caller is anonymous.</param>
/// <param name="AgentIdSource">Where <see cref="AgentId"/> came from; surfaced in tool output so a
/// mis-scoped write can be diagnosed from the response alone.</param>
/// <param name="ProjectId">
/// Normalized project slug, or null when the caller is not global and no project context could be
/// resolved. Null is not the same thing as <see cref="AgentMemory.GlobalProjectId"/>: the first means
/// "we do not know where this belongs", the second means "this belongs everywhere".
/// </param>
/// <param name="ProjectIdSource">Where <see cref="ProjectId"/> came from (or why it is null).</param>
public sealed record MemoryCallScope(
    string? AgentId,
    string? ProjectId,
    string AgentIdSource,
    string ProjectIdSource);

/// <summary>
/// Resolves the caller's agent identity and project boundary for memory writes.
/// </summary>
public interface IMemoryContextResolver
{
    /// <summary>
    /// Builds the scope for the current request.
    /// </summary>
    /// <param name="isGlobal">
    /// True when the caller asserts the lesson applies to every project, which short-circuits project
    /// resolution to <see cref="AgentMemory.GlobalProjectId"/>.
    /// </param>
    Task<MemoryCallScope> ResolveAsync(bool? isGlobal, CancellationToken ct = default);
}

/// <summary>
/// Resolves memory scope from the authenticated principal and the request's session context.
/// </summary>
/// <remarks>
/// <para>
/// Identity precedence is claims first, header second. The <c>X-Agent-Id</c> header is the documented
/// routing header (see <c>ApiKeyAuthenticationHandler</c>, which honours it for master-key callers), but
/// it is a plain header: anyone can set it. A verified bearer token therefore wins, and the header is
/// only trusted when there is no claim to read — which is the master-key/dev path the header exists for.
/// </para>
/// <para>
/// Project precedence is header then session. The session path is what makes this work for a persona
/// container mid-job: <c>JobTurnDispatcher</c> stamps <c>projectId</c> into the turn session's metadata,
/// so a runner that only knows its session id still lands the lesson on the right project.
/// </para>
/// <para>
/// Both ids are lowercased because that is the identity the rest of the system stores: agent rows are
/// normalized with <c>ToLowerInvariant()</c> at registration and project slugs the same way in
/// <c>ProjectsController</c>. Memory keys are the uniqueness triple, so a case difference here would
/// silently fork one lesson into two rows.
/// </para>
/// </remarks>
public class MemoryContextResolver(
    IHttpContextAccessor? httpContextAccessor,
    AgentMeshDbContext? db,
    ILogger<MemoryContextResolver>? logger = null) : IMemoryContextResolver
{
    /// <summary>Explicit project override, e.g. an operator saving on behalf of a repository.</summary>
    public const string ProjectIdHeader = "X-Project-Id";

    /// <summary>The turn session the caller is running inside; resolved to its project.</summary>
    public const string SessionIdHeader = "X-Session-Id";

    public const string PersonaIdHeader = "X-Persona-Id";

    public const string AgentIdHeader = "X-Agent-Id";

    private const string UnknownSource = "none";

    public async Task<MemoryCallScope> ResolveAsync(bool? isGlobal, CancellationToken ct = default)
    {
        var httpContext = httpContextAccessor?.HttpContext;

        var (agentId, agentSource) = await ResolveIdentityAsync(httpContext, ct);

        if (isGlobal == true)
        {
            return new MemoryCallScope(agentId, AgentMemory.GlobalProjectId, agentSource, "is_global=true");
        }

        var (projectId, projectSource) = await ResolveProjectIdAsync(httpContext, ct);
        return new MemoryCallScope(agentId, projectId, agentSource, projectSource);
    }

    private async Task<(string? AgentId, string Source)> ResolveIdentityAsync(
        HttpContext? httpContext,
        CancellationToken ct)
    {
        if (httpContext is null)
        {
            return (null, "no http context");
        }

        // 1. If running inside a dispatched turn session with a PersonaId, the session row was created
        // by the control plane for that persona — attribute memories to PersonaId rather than DriverId.
        if (db is not null &&
            httpContext.Request.Headers.TryGetValue(SessionIdHeader, out var sessionHeader) &&
            Guid.TryParse(sessionHeader.ToString().Trim(), out var sessionId))
        {
            var sessionInfo = await db.AgentSessions
                .AsNoTracking()
                .Where(s => s.Id == sessionId)
                .Select(s => new { s.PersonaId, s.MetadataJson })
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(sessionInfo?.PersonaId))
            {
                return (Normalize(sessionInfo.PersonaId), $"session {sessionId} personaId");
            }

            var metaPersona = ExtractMetadataProperty(sessionInfo?.MetadataJson, "personaId");
            if (!string.IsNullOrWhiteSpace(metaPersona))
            {
                return (metaPersona, $"session {sessionId} metadata.personaId");
            }
        }

        var user = httpContext.User;
        var isDriverOrMasterKey = user?.HasClaim("IsMasterKey", "true") == true ||
                                  user?.IsInRole("Driver") == true;

        // 2. When authenticated as a Driver runner or MasterKey (or unauthenticated dev mode),
        // X-Persona-Id identifies the persona hosted on that driver.
        if ((isDriverOrMasterKey || user?.Identity?.IsAuthenticated != true) &&
            httpContext.Request.Headers.TryGetValue(PersonaIdHeader, out var personaHeader) &&
            !string.IsNullOrWhiteSpace(personaHeader))
        {
            return (Normalize(personaHeader.ToString()), $"{PersonaIdHeader} header");
        }

        if (user?.Identity?.IsAuthenticated == true)
        {
            var personaClaim = user.FindFirst("PersonaId")?.Value ?? user.FindFirst("persona_id")?.Value;
            if (!string.IsNullOrWhiteSpace(personaClaim))
            {
                return (Normalize(personaClaim), "authenticated persona claim");
            }

            var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("AgentId")?.Value;

            if (!string.IsNullOrWhiteSpace(claim))
            {
                return (Normalize(claim), "authenticated claim");
            }
        }

        if (httpContext.Request.Headers.TryGetValue(AgentIdHeader, out var header) &&
            !string.IsNullOrWhiteSpace(header))
        {
            return (Normalize(header.ToString()), $"{AgentIdHeader} header");
        }

        return (null, "unauthenticated");
    }

    private async Task<(string? ProjectId, string Source)> ResolveProjectIdAsync(
        HttpContext? httpContext,
        CancellationToken ct)
    {
        if (httpContext is null)
        {
            return (null, "no http context");
        }

        if (httpContext.Request.Headers.TryGetValue(ProjectIdHeader, out var header) &&
            !string.IsNullOrWhiteSpace(header))
        {
            return (Normalize(header.ToString()), $"{ProjectIdHeader} header");
        }

        var sessionIdRaw = httpContext.Request.Headers.TryGetValue(SessionIdHeader, out var sessionHeader)
            ? sessionHeader.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(sessionIdRaw) || !Guid.TryParse(sessionIdRaw.Trim(), out var sessionId))
        {
            // The raw header value is deliberately not echoed: it is caller-controlled text and this string
            // is returned to the caller inside the tool's error payload. Naming the header is enough for the
            // caller to know what to fix.
            return (null, string.IsNullOrWhiteSpace(sessionIdRaw)
                ? "no project context"
                : $"'{SessionIdHeader}' header is not a session GUID");
        }

        if (db is null)
        {
            return (null, "session context unavailable (no database)");
        }

        // Only the metadata column is read; the session entity is not needed and AsNoTracking keeps this
        // out of whatever the request's change tracker is already holding.
        var metadataJson = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => s.MetadataJson)
            .FirstOrDefaultAsync(ct);

        var projectId = ExtractProjectId(metadataJson);
        if (projectId is not null)
        {
            return (projectId, $"session {sessionId} metadata");
        }

        return (null, $"session {sessionId} carries no projectId");
    }

    /// <summary>
    /// Reads <c>projectId</c> out of a session's metadata blob.
    /// </summary>
    /// <remarks>
    /// Tolerant by design: <c>metadata</c> is jsonb but written by several call sites, and a malformed
    /// blob must degrade to "no project context" (which produces an actionable tool error) rather than
    /// throw inside a tool call.
    /// </remarks>
    internal string? ExtractProjectId(string? metadataJson) =>
        ExtractMetadataProperty(metadataJson, "projectId");

    private string? ExtractMetadataProperty(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(propertyName, out var propElement))
            {
                return null;
            }

            var value = propElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? propElement.GetString()
                : propElement.ToString();

            return string.IsNullOrWhiteSpace(value) ? null : Normalize(value);
        }
        catch (System.Text.Json.JsonException)
        {
            logger?.LogWarning("Session metadata is not valid JSON; skipping '{Property}' resolution", propertyName);
            return null;
        }
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
