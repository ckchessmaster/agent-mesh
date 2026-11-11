using AgentMesh.Core.Models;
using AgentMesh.Core.Services;

namespace AgentMesh.Api.Services;

/// <summary>
/// Finds the checkouts that could answer "what did this job change", in precedence order.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a candidate list and not one directory.</strong> The platform has three legitimate
/// answers to "where is a job's repository" and they are not mutually exclusive: a job may carry an
/// explicit <c>working_directory</c>; a runner keeps a per-job checkout under its workspace root; and
/// a project has one shared conventional checkout at <c>/workspace/repos/{repo_name}</c>
/// (<see cref="WorkspacePathResolver"/>) that multi-repo jobs work in. Returning the first
/// <em>existing</em> directory rather than the first <em>candidate</em> is the bug this list avoids:
/// the shared path always exists in the containerized topologies, so a job with its own checkout
/// would be measured against whichever tree happened to be first and the panel would report another
/// job's changes as this one's.
/// </para>
/// <para>
/// <strong>A candidate only counts if it knows the job's branch</strong> — checked by
/// <see cref="JobChangesGitSource"/>, not here, because "knows the branch" is a git question and this
/// class is a filesystem question. A checkout that does not have the ref cannot answer, and says so,
/// rather than answering from <c>HEAD</c> and being wrong.
/// </para>
/// <para>
/// <see cref="LegacySharedCheckout"/> is last and is not a hypothetical: this deployment mounts the
/// persona's single working copy at <c>/workspace/repo</c>, which predates
/// <see cref="WorkspacePathResolver"/>'s per-repository layout. Leaving it out would mean the Changes
/// panel reports "no workspace" on the deployment the platform is actually running on, which is the
/// definition of a feature that does not work in practice.
/// </para>
/// </remarks>
public interface IJobWorkspaceLocator
{
    /// <summary>
    /// Candidate checkout roots for a job, best first, deduplicated, existing-only.
    /// </summary>
    /// <param name="job">The job whose work is being described.</param>
    /// <param name="project">The job's project, for the conventional shared checkout.</param>
    IReadOnlyList<JobWorkspaceCandidate> Candidates(ProjectJob job, Project? project);
}

/// <summary>
/// One checkout that could answer the question.
/// </summary>
/// <param name="Root">Absolute directory.</param>
/// <param name="Provenance">
/// Which rule produced it, for the response's source note. A path alone tells a reader nothing about
/// whether they are looking at the runner's tree or a shared clone, which is exactly the distinction
/// they need when the numbers look off.
/// </param>
public readonly record struct JobWorkspaceCandidate(string Root, string Provenance)
{
    public override string ToString() => $"{Root} ({Provenance})";
}

/// <summary>
/// The locator's ordering rules, shared with the resolver that the platform MCP tools use so the two
/// cannot disagree about where a job's tree is.
/// </summary>
/// <remarks>
/// The runner-root chain mirrors <c>PlatformCallContextResolver.ResolveWorkspaceDirectory</c> on
/// purpose: that method is what <c>open_pull_request</c> pushes from, so if the Changes panel looked
/// somewhere else, the diff and the PR would describe different trees and the mismatch would be
/// invisible. The configuration key is referenced by name
/// (<see cref="PlatformCallContextResolver.WorkspaceRootConfigKey"/>) rather than duplicated.
/// </remarks>
public sealed class JobWorkspaceLocator(
    Microsoft.Extensions.Configuration.IConfiguration? configuration = null) : IJobWorkspaceLocator
{
    /// <summary>The pre-multi-repo shared checkout this deployment still mounts for persona runners.</summary>
    public const string LegacySharedCheckout = "/workspace/repo";

    private const string JobScopedWorkspaceSubpath = "jobs";

    public IReadOnlyList<JobWorkspaceCandidate> Candidates(ProjectJob job, Project? project)
    {
        var candidates = new List<JobWorkspaceCandidate>();

        if (!string.IsNullOrWhiteSpace(job.WorkingDirectory))
        {
            candidates.Add(new JobWorkspaceCandidate(Expand(job.WorkingDirectory.Trim()), "job.workingDirectory"));
        }

        foreach (var root in RunnerRoots())
        {
            candidates.Add(new JobWorkspaceCandidate(
                Path.Combine(root, JobScopedWorkspaceSubpath, job.Id.ToString()), "runnerPerJob"));
            candidates.Add(new JobWorkspaceCandidate(
                Path.Combine(root, job.Id.ToString()), "runnerPerJobLegacyFlat"));
        }

        if (project != null)
        {
            candidates.Add(new JobWorkspaceCandidate(
                project.GetResolvedWorkingDirectory(), "project.conventionalCheckout"));
        }

        candidates.Add(new JobWorkspaceCandidate(LegacySharedCheckout, "legacySharedCheckout"));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var existing = new List<JobWorkspaceCandidate>();

        foreach (var candidate in candidates)
        {
            var normalized = Normalize(candidate.Root);
            if (normalized.Length == 0 || !seen.Add(normalized)) continue;
            if (!IsCheckout(normalized)) continue;

            existing.Add(new JobWorkspaceCandidate(normalized, candidate.Provenance));
        }

        return existing;
    }

    /// <summary>
    /// True when <paramref name="directory"/> is a git checkout — a <c>.git</c> entry of any shape.
    /// </summary>
    /// <remarks>
    /// A file rather than a directory is a valid <c>.git</c> (a worktree or a submodule points at the
    /// real gitdir with one), and those are exactly the layouts that show up in multi-repo jobs.
    /// Probing with <c>rev-parse</c> instead would mean running a process per candidate just to decide
    /// whether to run processes; this cheap check plus the ref check downstream is enough, because a
    /// non-repository fails the very next command with a message worth showing.
    /// </remarks>
    private static bool IsCheckout(string directory)
    {
        try
        {
            return Directory.Exists(directory) &&
                   (Directory.Exists(Path.Combine(directory, ".git")) ||
                    File.Exists(Path.Combine(directory, ".git")));
        }
        catch (Exception)
        {
            // An unreadable mount (permission denied on a foreign volume) is not a candidate. It must
            // not take the whole locator down: the other candidates may well answer.
            return false;
        }
    }

    private IEnumerable<string> RunnerRoots()
    {
        var configured = configuration?[PlatformCallContextResolver.WorkspaceRootConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return Expand(configured.Trim());
            yield break;
        }

        if (Directory.Exists("/workspace")) yield return "/workspace";

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentmesh", "workspaces");
    }

    private static string Expand(string value)
    {
        if (!value.StartsWith("~/", StringComparison.Ordinal) && value != "~") return value;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return value == "~" ? home : Path.Combine(home, value[2..]);
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = value.Replace('\\', '/');
        while (text.Length > 1 && text.EndsWith('/')) text = text[..^1];
        return text;
    }
}
