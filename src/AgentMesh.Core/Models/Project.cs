using System.Text.Json.Serialization;
using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

public class Project
{
    public string Id { get; set; } = string.Empty; // slug identifier, e.g. "agent-mesh"
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public WorkspaceType WorkspaceType { get; set; } = WorkspaceType.Git;

    public string? GitRepositoryUrl { get; set; }
    public string GitBaseBranch { get; set; } = "main";

    /// <summary>
    /// Directory name the repository is checked out under, i.e. the <c>{repo_name}</c> in
    /// <c>/workspace/repos/{repo_name}</c>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="GitRepositoryUrl"/> because the URL is not always available and is
    /// never safe as a path: an SSH remote ends in <c>.git</c>, an HTTPS remote may carry credentials
    /// or a trailing slash, and a project with a <c>WorkspaceType.S3</c> workspace has no remote at
    /// all. The name is the identity the filesystem is keyed on; the URL stays the transport.
    /// <para>
    /// Defaults to <see cref="DefaultRepositoryName"/> rather than null so a project row created
    /// before multi-repo support still resolves to a real directory instead of
    /// <c>/workspace/repos/</c>, which is a path that exists and is writable — the worst possible
    /// fallback, because commands succeed there and operate on the wrong tree.
    /// </para>
    /// </remarks>
    public string RepositoryName { get; set; } = DefaultRepositoryName;

    /// <summary>
    /// Absolute checkout root for this project. When blank, derived from
    /// <see cref="RepositoryName"/> by <see cref="GetResolvedWorkingDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Stored rather than always computed because monorepos and worktree layouts genuinely need an
    /// override: a project can legitimately live at <c>/workspace/repos/monorepo/services/api</c>,
    /// and no amount of name derivation produces that. Blank means "use the convention".
    /// <para>
    /// <strong>Why the default is blank and not <see cref="DefaultWorkingDirectory"/>:</strong> the
    /// job spec's migration spells this column <c>NOT NULL DEFAULT
    /// '/workspace/repos/agent-mesh'</c>. As a <em>column</em> default that is harmless for existing
    /// rows, but EF also stamps CLR defaults onto new rows, so a project created with
    /// <c>repository_name = "payments-api"</c> and no working directory would be born pointing at
    /// <c>/workspace/repos/agent-mesh</c> — a directory that exists, is writable, and belongs to a
    /// different repository. Every command would succeed against the wrong tree and no error would
    /// ever surface. Blank derives the right path in both cases, so it satisfies the spec's intent
    /// (legacy rows resolve to <c>/workspace/repos/agent-mesh</c>) without that failure mode.
    /// </para>
    /// </remarks>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>The repository name every pre-multi-repo row carries.</summary>
    public const string DefaultRepositoryName = "agent-mesh";

    /// <summary>
    /// The path a row with <see cref="DefaultRepositoryName"/> and no override resolves to. Kept as
    /// a named constant because it is the value legacy rows must still produce, which makes it the
    /// assertion in the migration's backfill test rather than a default anyone should assign.
    /// </summary>
    public const string DefaultWorkingDirectory = "/workspace/repos/agent-mesh";

    /// <summary>
    /// The directory commands for this project run in: the explicit
    /// <see cref="WorkingDirectory"/> when set, otherwise the conventional path under
    /// <see cref="RepositoryName"/>.
    /// </summary>
    /// <remarks>
    /// Blank (not just null) falls back to the convention, because a form that posts an empty
    /// working directory is indistinguishable from one that never intended to override it, and
    /// <c>""</c> as a process working directory means "the worker's own directory" — a silent
    /// change of target that every subsequent command inherits.
    /// <para>
    /// The result is normalized to forward slashes and stripped of trailing separators so a value
    /// typed as <c>/workspace/repos/api/</c> compares equal to the derived one. Callers concatenate
    /// paths onto this; a trailing slash becomes <c>//</c> and a backslash is a literal filename
    /// character on Linux.
    /// </para>
    /// </remarks>
    public string GetResolvedWorkingDirectory() =>
        WorkspacePathResolver.Resolve(RepositoryName, WorkingDirectory);

    public string? S3BucketName { get; set; }
    public string? S3PrefixKey { get; set; }

    public string ProjectInstructions { get; set; } = string.Empty;
    public string? DefaultLeadPersonaId { get; set; }

    [JsonIgnore]
    public Persona? DefaultLeadPersona { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public ICollection<ProjectPersona> ProjectPersonas { get; set; } = new List<ProjectPersona>();

    [JsonIgnore]
    public ICollection<ProjectJob> Jobs { get; set; } = new List<ProjectJob>();
}
