namespace AgentMesh.Core.Models;

/// <summary>
/// Typed contracts behind the platform communication and PR tools
/// (<c>post_job_message</c>, <c>manage_task</c>, <c>open_pull_request</c>).
/// </summary>
/// <remarks>
/// <para>
/// These are the <em>domain</em> shapes: what a handler receives after the MCP layer has bound a
/// JSON payload, and what the GitHub service hands back. They are deliberately not the MCP
/// parameter list. <c>ModelContextProtocol</c> 2.2.0 derives a tool's published input schema from
/// the C# parameter names of the tool method, so a record bound as a single parameter is
/// advertised under its serialized member name rather than the snake_case name the job spec
/// fixes on the wire. Verified against 2.2.0: a <c>Summary3R summary_3r</c> parameter emits
/// <c>"summary_3r"</c> and binds correctly, while <c>Summary3R summary3R</c> emits
/// <c>"summary3R"</c>. The tool methods therefore keep snake_case parameter names that match the
/// spec exactly, and these records carry the values from there.
/// </para>
/// <para>
/// The records still earn their keep as the seam between the API layer and the GitHub service:
/// <see cref="GitHubPullRequestRequest"/> is what lets <c>AgentMesh.Infrastructure</c> format a
/// PR body without the API project owning that formatting, and it is the type the body-template
/// tests assert against.
/// </para>
/// </remarks>
public record PostJobMessageArgs(
    string Content,
    string TargetPersona,
    bool RequiresReply
);

public record ChecklistTaskItem(
    string Title,
    string? TargetFile = null,
    string? VerificationMethod = null
);

public record ManageTaskArgs(
    string Action,
    IReadOnlyList<ChecklistTaskItem>? Tasks = null,
    string? TaskId = null,
    string? Status = null,
    string? ProgressNote = null,
    string? Notes = null
)
{
    public ManageTaskArgs(string action, string? taskId = null, string? status = null, string? notes = null)
        : this(action, null, taskId, status, notes, notes)
    {
    }
}

public record PlanApprovedSignal(
    string ApproverId,
    string ApproverRole,
    string? Comments,
    DateTimeOffset Timestamp
);

public record JobMessageRecord(
    string JobId,
    string SenderId,
    string Content,
    DateTimeOffset Timestamp
);

public record Summary3R(
    string Result,
    string Reasoning,
    string Risk
);

public record OpenPullRequestArgs(
    string Title,
    Summary3R Summary3R,
    string? BranchName = null
);

// NOTE: the job spec also lists an `OpenPullRequestResult(Success, PrNumber, PrUrl, Message)`
// record, and it is deliberately not implemented here. Two reasons.
//
// First, the spec contradicts itself: §C.1 documents the tool's return as
// {success, pr_number, pr_url, status} — with a `status` and no `message` — while §3 defines a DTO
// with a `message` and no `status`. Only one can be the wire contract, and the documented response
// body is the thing an agent codes against and the DoD test pins.
//
// Second, the response the tool actually returns is a superset of both: it adds `simulated`,
// `headBranch`, `baseBranch`, `reviewer`, `nextStep`, and `resolvedFrom`, each of which exists to
// stop a specific agent failure (believing a simulated PR was real; re-pushing a branch that
// already landed; passing back an empty reviewer). Binding that to the spec's four fields would
// either drop the safeguards or widen the record until it matched nothing in the document.
//
// The domain-level result that IS shared across layers is GitHubPullRequestResult below. It is
// named and pinned by tests; a second, unused shape for the same concept would only invite someone
// to "fix" one half and break the other.

/// <summary>
/// Everything the PR service needs to push a branch and open a pull request against it.
/// </summary>
/// <param name="RepositoryOwner">Owner half of <c>owner/repo</c>. Null lets the service fall back
/// to the remote URL recorded for the job, then to <c>GitHub:Repository</c> in configuration.</param>
/// <param name="RepositoryName">Repo half of <c>owner/repo</c>; same fallback chain as
/// <see cref="RepositoryOwner"/>.</param>
/// <param name="HeadBranch">The branch to push and to name as the PR head.</param>
/// <param name="BaseBranch">The branch to merge into. Null falls back to
/// <c>Project.GitBaseBranch</c>, then <c>main</c>.</param>
/// <param name="WorkingDirectory">
/// The directory the push runs in. This is the runner's per-job checkout, not a directory the
/// control plane owns — see <see cref="GitHubPullRequestRequest.WorkingDirectory"/>.
/// </param>
/// <param name="RemoteUrl">
/// The git remote to push to. When it is an https URL and a token is configured, the token is
/// injected into the push URL in memory only; it is never written to the repository's git config.
/// </param>
/// <param name="ReviewerLogin">GitHub login to request review from. Null skips the review request.</param>
public record GitHubPullRequestRequest(
    string Title,
    Summary3R Summary,
    string HeadBranch,
    string? BaseBranch = null,
    string? WorkingDirectory = null,
    string? RemoteUrl = null,
    string? RepositoryOwner = null,
    string? RepositoryName = null,
    string? ReviewerLogin = null,
    string? SenderAgentId = null,
    string? TeamLeadAgentId = null
);

/// <summary>
/// The outcome of a push-and-open-PR attempt.
/// </summary>
/// <param name="Simulated">
/// True when no GitHub credentials were configured and the service returned a fabricated PR
/// number instead of calling GitHub. The tool surfaces this on the wire so a caller can never
/// mistake a simulated result for a real pull request — a runner that believes it opened PR #999
/// will report a review that will never arrive.
/// </param>
/// <param name="FailureReason">Machine-readable reason when <see cref="Success"/> is false, so the
/// caller can tell "no credentials" from "dirty tree" from "GitHub rejected the request".</param>
public record GitHubPullRequestResult(
    bool Success,
    int? PrNumber,
    string? PrUrl,
    string? HeadBranch,
    string? BaseBranch,
    string Message,
    bool Simulated = false,
    string? FailureReason = null
);
