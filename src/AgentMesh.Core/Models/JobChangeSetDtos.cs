using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

/// <summary>
/// Where a job's file-change list came from, and therefore how much of the answer it is.
/// </summary>
/// <remarks>
/// Carried as stable lowercase strings rather than a C# enum for the same reason
/// <see cref="JobTaskStatus"/> is: the value crosses a wire that a browser and a future runner both
/// read, and a renumbered enum is a breaking change nobody notices until a panel silently labels
/// every row "LocalGit". The set is closed and declared by the constants on
/// <see cref="Services.JobChangesSources"/>.
/// </remarks>
public record JobChangeSourceInfo(
    /// <summary>The source actually used, or null when nothing could be read.</summary>
    [property: JsonPropertyName("kind")] string? Kind,
    /// <summary>Machine-readable reason when <see cref="Kind"/> is null — see <c>JobChangeUnavailableReasons</c>.</summary>
    [property: JsonPropertyName("reasonCode")] string? ReasonCode,
    /// <summary>Human-readable explanation. Safe to show verbatim; it carries no paths or secrets.</summary>
    [property: JsonPropertyName("message")] string? Message,
    /// <summary>True when a better source was tried first and failed, so this answer is a fallback.</summary>
    [property: JsonPropertyName("degraded")] bool Degraded = false,
    /// <summary>True when uncommitted work could not be included (a remote-only read sees committed work).</summary>
    [property: JsonPropertyName("committedOnly")] bool CommittedOnly = false,
    /// <summary>True when more paths exist than the response carries.</summary>
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    /// <summary>The repo the branch lives in, for a remote read (<c>owner/repo</c>).</summary>
    [property: JsonPropertyName("repository")] string? Repository = null);

/// <summary>
/// Per-job line totals plus the count of paths the response left out.
/// </summary>
/// <remarks>
/// <see cref="FilesChanged"/> is the real total even when <see cref="JobChangeSetDto.Files"/> is
/// capped. Presenting a capped list as if it were complete would understate the blast radius of a
/// change — the one thing a reader is consulting this panel to judge.
/// </remarks>
public record JobChangeStats(
    [property: JsonPropertyName("filesChanged")] int FilesChanged,
    [property: JsonPropertyName("additions")] int Additions,
    [property: JsonPropertyName("deletions")] int Deletions,
    /// <summary>Paths omitted from <see cref="JobChangeSetDto.Files"/>; zero when nothing was dropped.</summary>
    [property: JsonPropertyName("omittedFiles")] int OmittedFiles = 0);

/// <summary>
/// One file the job's branch touched, relative to the project's base branch.
/// </summary>
/// <remarks>
/// <see cref="PreviousPath"/> is populated only for a rename or a copy, so the UI can render
/// <c>old → new</c> instead of a deletion and an addition the reader has to stitch together.
/// <para>
/// <see cref="Additions"/>/<see cref="Deletions"/> are null for a binary file: git reports
/// <c>-</c> in those columns, and zero is a different fact from "not countable".
/// </para>
/// </remarks>
public record JobChangedFileDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("previousPath")] string? PreviousPath = null,
    [property: JsonPropertyName("additions")] int? Additions = null,
    [property: JsonPropertyName("deletions")] int? Deletions = null,
    [property: JsonPropertyName("binary")] bool Binary = false,
    /// <summary>False when the source can describe the path but not diff it (the checkpoint fallback).</summary>
    [property: JsonPropertyName("patchAvailable")] bool PatchAvailable = true);

/// <summary>One commit that belongs to the job and not to the base branch.</summary>
public record JobChangeCommitDto(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("shortSha")] string ShortSha,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("authorName")] string? AuthorName = null,
    [property: JsonPropertyName("authorEmail")] string? AuthorEmail = null,
    [property: JsonPropertyName("committedAt")] DateTimeOffset? CommittedAt = null);

/// <summary>
/// Everything the Cockpit's Changes panel needs about a job's file changes.
/// </summary>
/// <remarks>
/// Deliberately close to what GitHub's compare view reports for the same branch, because a reader
/// will check one against the other; a mismatch in the counts would otherwise look like a bug in the
/// panel rather than a difference in what each side can see (uncommitted work, chiefly).
/// </remarks>
public record JobChangeSetDto(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("baseBranch")] string? BaseBranch,
    /// <summary>The commit the diff was taken from.</summary>
    [property: JsonPropertyName("headSha")] string? HeadSha,
    /// <summary>
    /// The commit the diff was taken against: the fork point, not the base branch's tip.
    /// </summary>
    /// <remarks>
    /// Reported so a patch request can be pinned to the exact pair that produced these counts.
    /// Without the pin, a base branch that moves between the list call and the patch call changes
    /// the diff under a list of counts the reader can no longer reconcile.
    /// </remarks>
    [property: JsonPropertyName("baseSha")] string? BaseSha,
    [property: JsonPropertyName("stats")] JobChangeStats Stats,
    [property: JsonPropertyName("files")] IReadOnlyList<JobChangedFileDto> Files,
    [property: JsonPropertyName("source")] JobChangeSourceInfo Source,
    [property: JsonPropertyName("commits")] IReadOnlyList<JobChangeCommitDto> Commits,
    /// <summary>True when the tree has changes that are not committed on the branch.</summary>
    [property: JsonPropertyName("hasUncommittedChanges")] bool HasUncommittedChanges = false,
    /// <summary>Paths with uncommitted changes, capped; a summary, not a file browser.</summary>
    [property: JsonPropertyName("uncommittedPaths")] IReadOnlyList<string>? UncommittedPaths = null,
    /// <summary>Exact uncommitted path count, even when <see cref="UncommittedPaths"/> is capped.</summary>
    [property: JsonPropertyName("uncommittedCount")] int? UncommittedCount = null,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset? GeneratedAt = null,
    /// <summary>Why a source was skipped, in precedence order. Read-aloud material for the panel.</summary>
    [property: JsonPropertyName("notes")] IReadOnlyList<string>? Notes = null);

/// <summary>
/// A unified diff for one file or for the whole change set, plus the identity of the pair it was
/// computed from.
/// </summary>
/// <remarks>
/// Served as <c>text/plain</c> rather than embedded in the list response: a large patch inside JSON is
/// escaped and re-parsed to render nothing but text, and it would make the list response's cache key
/// depend on every file's contents.
/// </remarks>
public record JobChangePatchDto(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("baseBranch")] string? BaseBranch,
    [property: JsonPropertyName("headSha")] string? HeadSha,
    [property: JsonPropertyName("baseSha")] string? BaseSha,
    /// <summary>The unified diff text; empty for a binary file.</summary>
    [property: JsonPropertyName("patch")] string Patch,
    /// <summary>The repository-relative path, or null for the whole-change-set patch.</summary>
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("previousPath")] string? PreviousPath = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("binary")] bool Binary = false,
    /// <summary>True when neither side has content (a rename detected with no edits).</summary>
    [property: JsonPropertyName("empty")] bool Empty = false,
    /// <summary>True when <see cref="Patch"/> was cut at the server's byte limit.</summary>
    [property: JsonPropertyName("truncated")] bool Truncated = false,
    [property: JsonPropertyName("additions")] int? Additions = null,
    [property: JsonPropertyName("deletions")] int? Deletions = null,
    [property: JsonPropertyName("contextLines")] int ContextLines = 3,
    [property: JsonPropertyName("source")] JobChangeSourceInfo? Source = null);
