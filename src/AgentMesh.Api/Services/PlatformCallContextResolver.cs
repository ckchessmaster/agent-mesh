using System.Security.Claims;
using AgentMesh.Api.Controllers;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Services;

/// <summary>
/// Who is calling a platform tool, and which job and turn session they are calling from.
/// </summary>
/// <remarks>
/// Every field carries the reason it has the value it does. That is not decoration: the failure
/// mode this exists to prevent is an agent calling <c>post_job_message</c> and getting a silent
/// "no context" back, then burning turns guessing which header it forgot. Each resolver reports
/// its own provenance so the tool can say "I looked at the bearer claim, then X-Persona-Id, then
/// the turn session, and none of them had it."
/// <para>
/// <see cref="JobId"/> is the important one and the one least likely to be sent explicitly. A
/// runner does not know its job id — the control plane chose it. It is recovered from the turn
/// session's metadata, which <c>JobTurnDispatcher</c> writes <c>jobId</c> into for exactly this
/// reason.
/// </para>
/// </remarks>
public sealed record PlatformCallContext(
    string? AgentId,
    string AgentIdSource,
    string? PersonaId,
    string PersonaIdSource,
    Guid? SessionId,
    string SessionIdSource,
    Guid? JobId,
    string JobIdSource,
    string? ProjectId,
    string? GitBranch,
    string? GitRemoteUrl,
    string? GitBaseBranch,
    string? WorkspaceDirectory)
{
    public bool HasIdentity => !string.IsNullOrWhiteSpace(AgentId) || !string.IsNullOrWhiteSpace(PersonaId);
}

public interface IPlatformCallContextResolver
{
    Task<PlatformCallContext> ResolveAsync(CancellationToken ct = default);
}

/// <summary>
/// Resolves platform-tool context from the HTTP request, falling back to the turn session's
/// recorded job metadata.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Identity precedence is claims first, headers second.</strong> <c>X-Agent-Id</c> and
/// <c>X-Persona-Id</c> are plain headers anyone can set; the <c>ApiKeyAuthenticationHandler</c>
/// honours <c>X-Agent-Id</c> for master-key and dev callers, which is why the header is trusted at
/// all. A verified bearer claim therefore always wins, and the header is only used when there is
/// no claim to read. Same ordering as <see cref="MemoryContextResolver"/>.
/// </para>
/// <para>
/// <strong>Persona identity is derived, not asserted, whenever possible.</strong> A persona that
/// could name any target would let one agent post as another in the shared Job Chat. So the
/// persona is taken from the turn session the control plane created for that persona
/// (<c>AgentSession.PersonaId</c>) when a session id is available, and the header is only used
/// when there is no session to consult.
/// </para>
/// <para>
/// <strong>Workspace directory.</strong> <see cref="PlatformCallContext.WorkspaceDirectory"/> is
/// where a runner's per-job checkout lives, computed the same way the runner computes it. It is
/// only correct when the API process shares a filesystem with the runner — the docker-compose and
/// Aspire topologies, where both see <c>/workspace</c> or the same home directory. On Kubernetes
/// the API has no view of a pod's filesystem, and the PR tool reports that rather than pushing
/// the wrong tree; see <c>PlatformMcpTools.OpenPullRequest</c>.
/// </para>
/// </remarks>
public class PlatformCallContextResolver(
    AgentMeshDbContext db,
    IHttpContextAccessor? httpContextAccessor,
    Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
    ILogger<PlatformCallContextResolver>? logger = null) : IPlatformCallContextResolver
{
    public const string PersonaIdHeader = "X-Persona-Id";
    public const string JobIdHeader = "X-Job-Id";
    public const string AgentIdHeader = "X-Agent-Id";
    public const string SessionIdHeader = "X-Session-Id";

    /// <summary>
    /// Overrides the workspace root for tests and for non-default runner layouts. When unset, the
    /// same rule the runner uses applies: <c>/workspace</c> if it exists, else
    /// <c>$HOME/.agentmesh/workspaces</c>.
    /// </summary>
    public const string WorkspaceRootConfigKey = "Platform:WorkspaceRoot";

    private const string JobScopedWorkspaceSubpath = "jobs";

    public async Task<PlatformCallContext> ResolveAsync(CancellationToken ct = default)
    {
        var context = httpContextAccessor?.HttpContext;

        var (agentId, agentSource) = ResolveAgentId(context);
        var (sessionId, sessionSource) = ResolveSessionId(context);

        AgentSession? session = null;
        if (sessionId is not null)
        {
            session = await db.AgentSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

            if (session is null)
            {
                // The header parsed but no such session exists. Drop the id rather than carry it
                // forward: it is written into job_messages.turn_session_id and the Cockpit resolves
                // the sending runner through it. A dangling value there renders a message with no
                // avatar and no way to tell why, which is strictly worse than the field being empty.
                sessionSource = $"header:{SessionIdHeader} (session {sessionId} not found)";
                sessionId = null;
            }
        }

        var (personaId, personaSource) = ResolvePersonaId(context, session);

        // The turn session is the only trustworthy link to a job: it was created by the control
        // plane for one. An explicit header is accepted for out-of-band callers (the Cockpit, a
        // script driving the mesh) but never overrides what the session says, because a runner
        // cannot know better than the dispatcher which job dispatched it.
        var sessionJobId = session is null ? null : ProjectJobsController.ExtractJobId(session.MetadataJson);
        var (jobId, jobSource) = sessionJobId is not null
            ? (sessionJobId.Value, $"session:{sessionId}:metadata")
            : ResolveJobId(context);

        var (projectId, _) = ResolveProjectId(context, session);
        var gitBranch = ReadMetadataString(session?.MetadataJson, "gitBranch");
        var gitRemote = ReadMetadataString(session?.MetadataJson, "workspaceGitRemote")
                        ?? ReadMetadataString(session?.MetadataJson, "gitUrl");
        var gitBase = ReadMetadataString(session?.MetadataJson, "gitBaseBranch");

        return new PlatformCallContext(
            AgentId: agentId,
            AgentIdSource: agentSource,
            PersonaId: personaId,
            PersonaIdSource: personaSource,
            SessionId: sessionId,
            SessionIdSource: sessionSource,
            JobId: jobId,
            JobIdSource: jobSource,
            ProjectId: projectId,
            GitBranch: gitBranch,
            GitRemoteUrl: gitRemote,
            GitBaseBranch: gitBase,
            WorkspaceDirectory: jobId is not null ? ResolveWorkspaceDirectory(jobId.Value) : null);
    }

    private static (string? Value, string Source) ResolveAgentId(HttpContext? context)
    {
        var claim = context?.User?.Identity?.IsAuthenticated == true
            ? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst("AgentId")?.Value
            : null;

        if (!string.IsNullOrWhiteSpace(claim)) return (Normalize(claim), "claim:name-identifier");

        var header = context?.Request.Headers.TryGetValue(AgentIdHeader, out var h) == true ? h.ToString() : null;
        if (!string.IsNullOrWhiteSpace(header)) return (Normalize(header), $"header:{AgentIdHeader}");

        return (null, "unauthenticated");
    }

    private static (string? Value, string Source) ResolvePersonaId(HttpContext? context, AgentSession? session)
    {
        if (!string.IsNullOrWhiteSpace(session?.PersonaId))
        {
            return (Normalize(session.PersonaId), "session:personaId");
        }

        var header = context?.Request.Headers.TryGetValue(PersonaIdHeader, out var h) == true ? h.ToString() : null;
        if (!string.IsNullOrWhiteSpace(header)) return (Normalize(header), $"header:{PersonaIdHeader}");

        // A driver running without a persona is still an identity; the caller can address it by
        // agent id, so report the agent id as the persona rather than reporting nothing.
        var (agentId, agentSource) = ResolveAgentId(context);
        return (agentId, agentId is null ? "unresolved" : $"fallback:{agentSource}");
    }

    private static (Guid? Value, string Source) ResolveSessionId(HttpContext? context)
    {
        var raw = context?.Request.Headers.TryGetValue(SessionIdHeader, out var h) == true ? h.ToString() : null;

        if (string.IsNullOrWhiteSpace(raw)) return (null, "absent");
        if (!Guid.TryParse(raw.Trim(), out var id)) return (null, $"header:{SessionIdHeader} (not a GUID)");

        return (id, "header");
    }

    private static (Guid? Value, string Source) ResolveJobId(HttpContext? context)
    {
        var raw = context?.Request.Headers.TryGetValue(JobIdHeader, out var h) == true ? h.ToString() : null;

        if (string.IsNullOrWhiteSpace(raw)) return (null, "absent");
        if (!Guid.TryParse(raw.Trim(), out var id)) return (null, $"header:{JobIdHeader} (not a GUID)");

        return (id, $"header:{JobIdHeader}");
    }

    private (string? Value, string Source) ResolveProjectId(HttpContext? context, AgentSession? session)
    {
        var fromSession = ReadMetadataString(session?.MetadataJson, "projectId");
        if (!string.IsNullOrWhiteSpace(fromSession)) return (Normalize(fromSession), "session:metadata");

        var header = context?.Request.Headers.TryGetValue("X-Project-Id", out var h) == true ? h.ToString() : null;
        if (!string.IsNullOrWhiteSpace(header)) return (Normalize(header), "header:X-Project-Id");

        return (null, "unresolved");
    }

    /// <summary>
    /// The runner's checkout for a job, or null when this process cannot see one.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>AgentProcessManager</c>'s own resolution: a containerized runner works in
    /// <c>/workspace</c>, a host runner in <c>$HOME/.agentmesh/workspaces</c>. The job-scoped
    /// layout (<c>workspaces/jobs/&lt;jobId&gt;</c>) is what <c>IJobWorkspaceService</c> documents
    /// as the durable per-job tree, so it is checked first; the flat
    /// <c>workspaces/&lt;jobId&gt;</c> form is checked second because older runners created it.
    /// Returning null (rather than the first directory that happens to exist) is what keeps the
    /// PR tool from pushing an unrelated tree.
    /// </remarks>
    public string? ResolveWorkspaceDirectory(Guid jobId)
    {
        foreach (var root in CandidateWorkspaceRoots())
        {
            foreach (var candidate in new[]
            {
                Path.Combine(root, JobScopedWorkspaceSubpath, jobId.ToString()),
                Path.Combine(root, jobId.ToString()),
            })
            {
                if (Directory.Exists(Path.Combine(candidate, ".git"))) return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> CandidateWorkspaceRoots()
    {
        var configured = configuration?[WorkspaceRootConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return Expand(configured.Trim());
            yield break;
        }

        if (Directory.Exists("/workspace")) yield return "/workspace";

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentmesh",
            "workspaces");
    }

    private string? ReadMetadataString(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(propertyName, out var element)) return null;

            var value = element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString()
                : element.ToString();

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (System.Text.Json.JsonException)
        {
            logger?.LogWarning("Session metadata is not valid JSON; skipping '{Property}' lookup", propertyName);
            return null;
        }
    }

    /// <summary>
    /// Expand environment-variable references in a configured path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Environment.ExpandEnvironmentVariables"/> alone is not enough, and the reason is
    /// platform-specific in a way that bites exactly here: on Unix it recognises <em>only</em> the
    /// <c>%VAR%</c> form, which no Linux operator writes. Verified empirically —
    /// <c>ExpandEnvironmentVariables("$HOME/nested")</c> returns the string unchanged on Linux.
    /// </para>
    /// <para>
    /// The forms that actually arrive through a compose file, a Helm value, or
    /// <c>Platform__WorkspaceRoot</c> are <c>$VAR</c>, <c>${VAR}</c>, and <c>~</c>. All three are
    /// handled here so a path copied out of a shell script behaves the way the person who pasted it
    /// expects. An unresolvable reference is left literal rather than blanked: a root that expands
    /// to <c>"/nested/workspaces"</c> because <c>$HOME</c> was missing would search a plausible
    /// absolute path, whereas the unexpanded string matches nothing and produces the honest
    /// <c>no_working_directory</c> refusal.
    /// </para>
    /// </remarks>
    internal static string Expand(string path)
    {
        var result = Environment.ExpandEnvironmentVariables(path);

        result = System.Text.RegularExpressions.Regex.Replace(result, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", match =>
        {
            var value = Environment.GetEnvironmentVariable(match.Groups[1].Value);
            // Keep the literal text when unset, for the reason described above.
            return value ?? match.Value;
        });

        result = System.Text.RegularExpressions.Regex.Replace(result, @"\$([A-Za-z_][A-Za-z0-9_]*)", match =>
            Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value);

        if (result == "~" || result.StartsWith("~/", StringComparison.Ordinal))
        {
            result = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                result.Length > 1 ? result[2..] : string.Empty);
        }

        return result;
    }

    private static string Normalize(string value) => value.Trim().TrimStart('@').ToLowerInvariant();
}
