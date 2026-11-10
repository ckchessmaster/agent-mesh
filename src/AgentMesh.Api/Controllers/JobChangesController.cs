using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentMesh.Api.Controllers;

/// <summary>
/// The file changes a job produced, for the Cockpit's Changes tab.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is its own controller.</strong> <c>ProjectJobsController</c> is already the
/// largest file in the API and its constructor is a standing contract with the test suite (five
/// optional dependencies, each added by a different feature). A change reader needs three new
/// dependencies of its own and shares no state with the job lifecycle, so bolting it on would push a
/// 2,400-line controller toward 3,000 for no gain in cohesion. The route prefix stays under
/// <c>api/project-jobs/{id}/…</c> so the client sees one surface either way — the split is a source
/// organization, not an API change.
/// </para>
/// <para>
/// <strong>Why <c>?refresh=true</c> is a verb on the query string and not a POST.</strong> A refresh
/// is still a read: it changes nothing, is idempotent, and a browser should be free to retry it. A
/// POST that returns a change set would make the client's fetch helper treat it as a mutation and
/// forfeit the caching headers below.
/// </para>
/// <para>
/// <strong>Failure shapes.</strong> <c>404</c> means "no such job" and nothing else — that is a
/// property of the route, not of the diff. Everything downstream, including "the branch was never
/// pushed" and "this deployment cannot see a checkout", is a <c>200</c> with an explanatory
/// <see cref="JobChangeSetDto.Source"/>: those are ordinary states of the system that the panel must
/// render as text rather than as an error boundary, and a reader who sees a red box cannot tell a
/// misconfigured token from a job that has not run yet.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/project-jobs/{id:guid}/changes")]
public class JobChangesController(
    JobChangesService changes,
    ILogger<JobChangesController>? logger = null) : ControllerBase
{
    /// <summary>The <c>X-Changes-*</c> headers, documented once because a proxy's behaviour depends on them.</summary>
    private static class Headers
    {
        public const string Source = "X-Changes-Source";
        public const string Reason = "X-Changes-Reason";
        public const string Files = "X-Changes-Files";
        public const string BaseSha = "X-Changes-Base-Sha";
        public const string HeadSha = "X-Changes-Head-Sha";
        public const string Reused = "X-Changes-Reused";
        public const string PatchTruncated = "X-Changes-Patch-Truncated";
    }

    /// <summary>
    /// The files a job changed, with line counts, the commit roll-up, and where the answer came from.
    /// </summary>
    /// <remarks>
    /// Responses carry <c>Cache-Control: no-store</c> for the reasons documented on
    /// <see cref="SetDiagnostics"/> — chiefly that an agent commits mid-turn, so a cached list is
    /// wrong within seconds in a way the reader cannot see.
    /// </remarks>
    [HttpGet("")]
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> GetChanges(
        Guid id,
        [FromQuery] int? limit = null,
        [FromQuery] bool refresh = false,
        CancellationToken ct = default)
    {
        var changeSet = await changes.GetChangeSetAsync(id, limit, refresh, ct);
        if (changeSet is null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        SetDiagnostics(changeSet.Source.Kind, changeSet.Source.ReasonCode,
            changeSet.Stats.FilesChanged, changeSet.BaseSha, changeSet.HeadSha, reused: false);

        return Ok(changeSet);
    }

    /// <summary>
    /// One file's unified diff, or the whole change set's when <c>path</c> is omitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The patch is returned as JSON with a <c>patch</c> string, not as <c>text/x-diff</c>.</strong>
    /// The panel renders a patch inside a React component that already knows how to colour lines, and
    /// the identity fields (<see cref="JobChangePatchDto.BaseSha"/> and friends) belong next to the
    /// body they describe: a separate content type would either move them to headers a browser cannot
    /// read cross-origin without <c>Access-Control-Expose-Headers</c>, or drop them, and dropping them
    /// is what lets a diff and its counts disagree silently.
    /// </para>
    /// <para>
    /// <paramref name="previousPath"/> must be forwarded from the list row for a rename, or git reports
    /// a two-line edit as a 60-line new file. <paramref name="untracked"/> must be set for a path the
    /// list marks as untracked, since no tree comparison can produce its patch.
    /// </para>
    /// </remarks>
    [HttpGet("patch")]
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> GetPatch(
        Guid id,
        [FromQuery] string? path = null,
        [FromQuery] string? from = null,
        [FromQuery] string? status = null,
        [FromQuery] int? context = null,
        [FromQuery] string? @base = null,
        [FromQuery] string? head = null,
        CancellationToken ct = default)
    {
        if (!IsShaParameter(@base) || !IsShaParameter(head))
        {
            return BadRequest(new { error = "base and head must be 40-character commit ids when supplied." });
        }

        var result = await changes.GetPatchAsync(
            id, path, previousPath: from, status: status, contextLines: context,
            baseSha: @base, headSha: head, ct: ct);

        if (result is null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        if (result.Patch is null)
        {
            // A refused patch is worth a log line: the response carries the reason for the reader, and
            // nothing else records which job asked for which path and why the answer was no — which is
            // the first thing anyone needs when a panel says "no diff" for a file they can see changed.
            // 404 with the reason, because there is genuinely no patch to return — but the reason is
            // the part the panel shows, and it distinguishes "this path is not in the change set" from
            // "no source in this deployment can produce a diff at all".
            logger?.LogInformation("No patch for job {JobId} path '{Path}': {Code} — {Message}",
                id, path, result.Reason?.Code, result.Reason?.Message);
            SetDiagnostics(sourceKind: null, result.Reason?.Code, 0, @base, head, reused: false);
            return NotFound(new
            {
                error = result.Reason?.Message ?? "No patch could be produced for this job.",
                reasonCode = result.Reason?.Code,
                notes = result.Notes,
            });
        }

        var patch = result.Patch;
        SetDiagnostics(patch.Source?.Kind, patch.Source?.ReasonCode, 0, patch.BaseSha, patch.HeadSha, reused: false);

        if (Response != null)
        {
            Response.Headers[Headers.PatchTruncated] = patch.Truncated ? "true" : "false";
        }

        return Ok(patch);
    }

    /// <summary>
    /// Stamps the response with what was actually read, for a panel header and for curl-level debugging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>These duplicate fields that are already in the JSON body, on purpose.</strong> The two
    /// consumers differ: the panel reads the body, while whoever is debugging
    /// "why does this say the branch changed nothing" from a shell reads headers off a
    /// <c>curl -i</c> without piping through a JSON parser. The cost is a second place the value is
    /// written; the alternative is an operator guessing from a 4 KB body whether a token was missing.
    /// </para>
    /// <para>
    /// <strong>No-store, and why the reuse window is not expressed as <c>max-age</c>.</strong> A
    /// <c>max-age=10</c> would let a shared cache (the nginx sidecar in front of the dashboard, or any
    /// ingress in the way) answer for other users, and the panel's whole point is showing work an agent
    /// committed seconds ago. The server-side window in <see cref="JobChangesPolicy"/> is keyed by
    /// checkout and branch pair, so it cannot cross those boundaries; an HTTP cache can. The
    /// <c>X-Changes-Reused</c> header reports the server-side reuse explicitly so a reader can tell a
    /// fresh read from a reused one without inferring it from timing.
    /// </para>
    /// <para>
    /// Values are sanitized and length-capped before they become headers: a header is a single ASCII
    /// line, and a reason string that carried a newline from git's stderr would otherwise corrupt the
    /// response — a crash shape that has nothing to do with the feature it appears in.
    /// </para>
    /// </remarks>
    private void SetDiagnostics(
        string? sourceKind,
        string? reasonCode,
        int files,
        string? baseSha,
        string? headSha,
        bool reused)
    {
        if (Response == null) return;

        Response.Headers[Headers.Source] = Clean(sourceKind);
        Response.Headers[Headers.Reason] = Clean(reasonCode);
        Response.Headers[Headers.Files] = files.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers[Headers.BaseSha] = Clean(baseSha);
        Response.Headers[Headers.HeadSha] = Clean(headSha);
        Response.Headers[Headers.Reused] = reused ? "true" : "false";
    }

    /// <summary>Strips characters that would break out of a header line and caps the length.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";

        var text = value.Replace("\r", " ").Replace("\n", " ").Replace("\0", " ");
        return text.Length <= 120 ? text : text[..120];
    }

    /// <summary>
    /// A pinned SHA is forwarded straight into a git argument vector, so it is validated here at the
    /// boundary rather than relying on the reader's own check: a hex-only 40-character value is the
    /// only shape that can be a commit id, and anything else is a client bug worth an immediate 400.
    /// </summary>
    private static bool IsShaParameter(string? value) =>
        string.IsNullOrEmpty(value) ||
        (value.Length == 40 && value.All(Uri.IsHexDigit));
}
