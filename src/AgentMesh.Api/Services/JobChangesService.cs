using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Services;

/// <summary>
/// Answers "what did this job change?" from the best source the process can reach, and labels the
/// answer so its limits are visible.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why one service folds three sources.</strong> The three ways to answer differ in what they
/// can see, not in what they mean. A local <c>git</c> read sees work that is neither committed nor
/// pushed — the state an agent's work is in most of the time. GitHub's comparison sees the pushed
/// branch and nothing else. The stored working checkpoint sees the file list the harness measured at
/// the last turn boundary, with no line counts. Which of them exists depends on topology (the API
/// shares a filesystem with the runner under docker-compose/Aspire/a single-host install and does not
/// on Kubernetes — the same distinction that makes <c>open_pull_request</c> report
/// <c>no_working_directory</c> there) and on how far the job has got. Choosing between them per request
/// and labelling the result is the whole feature; handing the topology question to the browser instead
/// would mean three endpoints and a UI that has to guess which one is valid.
/// </para>
/// <para>
/// <strong>A source that cannot answer becomes a note, not an error.</strong> <see cref="ProbeAsync"/>
/// turns a <see cref="JobChangeUnavailableException"/> into a reason and moves to the next source, and
/// rethrows only when the caller's own token was cancelled: a user navigating away is not a source
/// failure to report. Notes accumulate in precedence order, so the first line a reader sees explains
/// why the source they got is not the one they expected.
/// </para>
/// <para>
/// <strong>Reuse is short, keyed by what was measured, and bypassable</strong> — see
/// <see cref="JobChangesPolicy.SuccessCacheLifetime"/> and <see cref="IJobChangesCache"/>. Only a
/// resolved answer is stored: the common transient here is a runner that has not pushed yet, and a
/// panel stuck reporting "no changes" for a few seconds after the agent committed is worse than one
/// extra <c>git</c> invocation.
/// </para>
/// <para>
/// <strong>Registered scoped</strong> because it reads the database; <see cref="IJobChangesCache"/> and
/// <see cref="IJobGitCommandRunner"/> are singletons it may depend on, which is the only arrangement
/// that gives the reuse window anything to reuse.
/// </para>
/// </remarks>
public sealed class JobChangesService(
    AgentMeshDbContext db,
    IJobWorkspaceLocator workspaces,
    JobChangesGitSource gitSource,
    IGitHubPullRequestService pullRequests,
    IJobChangesCache cache,
    Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
    ILogger<JobChangesService>? logger = null)
{
    private readonly JobChangesOptions _options = JobChangesOptions.From(configuration);

    /// <summary>
    /// The job's change set, from the best available source.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="fileLimit">Requested cap on paths; clamped by <see cref="JobChangesOptions.ResolveFileLimit"/>.</param>
    /// <param name="refresh">Bypasses reuse. The panel's refresh button, and the answer to "I know it changed".</param>
    /// <param name="ct">Request cancellation.</param>
    /// <returns>
    /// Null only when the job does not exist. Every other outcome — including "nothing could be read
    /// anywhere" — is a change set carrying an explanatory
    /// <see cref="JobChangeSetDto.Source"/>, because "no changes yet" and "this deployment cannot read
    /// changes" are both normal states a reader has to be able to tell apart.
    /// </returns>
    public async Task<JobChangeSetDto?> GetChangeSetAsync(
        Guid jobId,
        int? fileLimit = null,
        bool refresh = false,
        CancellationToken ct = default)
    {
        var context = await LoadContextAsync(jobId, ct);
        if (context is null) return null;

        var limit = _options.ResolveFileLimit(fileLimit);

        // The key names the branch pair and the limit, not the resolved SHAs: those are what this call
        // resolves, so pinning them here would require the answer before the question. A caller that
        // pins a pair is served at the patch level, where the pair must match the list it came from —
        // see GetPatchAsync and the no-reuse rule in JobChangesPolicy.CacheKey.
        var key = JobChangesPolicy.CacheKey(
            JobChangesSources.LocalGit,
            context.Workspace?.Root ?? $"project:{context.Job.ProjectId}",
            context.Job.GitBranchName,
            context.Project?.GitBaseBranch,
            pinnedBaseSha: null,
            pinnedHeadSha: null,
            contextLines: 0,
            path: $"changes:limit={limit}");

        if (!refresh && cache.TryGet(key, out var cached))
        {
            return cached with { JobId = jobId };
        }

        var composed = await ComposeAsync(context, limit, ct);

        if (composed.Source.Kind is not null)
        {
            cache.Set(key, composed, JobChangesPolicy.SuccessCacheLifetime);
        }

        return composed with { JobId = jobId };
    }

    /// <summary>
    /// One file's patch, or the whole change set's, chosen by the same source precedence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row's identity arrives from the caller rather than being re-derived by re-listing the change
    /// set. Two reasons, and the second matters more: re-listing would double the <c>git</c> work per
    /// click, and it could describe a patch computed against a state the list never showed. The pinned
    /// SHAs from the list response are forwarded so a patch is provably the same pair the counts on
    /// screen came from.
    /// </para>
    /// <para>
    /// <paramref name="status"/> decides whether the read is one-sided, and that is load-bearing rather
    /// than decorative: an untracked file is in no tree, so <c>git diff &lt;base&gt; &lt;head&gt;</c> can
    /// never list it and a pathspec-limited diff returns exit 0 with an empty body — which renders as
    /// "this file changed nothing" for the one file in the list that definitely did change. Those rows
    /// go through <c>git diff --no-index /dev/null &lt;path&gt;</c>, the only command that can produce a
    /// patch for content no commit contains.
    /// </para>
    /// </remarks>
    public async Task<JobPatchResult?> GetPatchAsync(
        Guid jobId,
        string? path,
        string? previousPath = null,
        string? status = null,
        int? contextLines = null,
        string? baseSha = null,
        string? headSha = null,
        CancellationToken ct = default)
    {
        var context = await LoadContextAsync(jobId, ct);
        if (context is null) return null;

        var resolvedContext = _options.ResolveContext(contextLines);
        var notes = new List<string>();
        var pinned = baseSha is not null || headSha is not null;

        // ── 1. The checkout ─────────────────────────────────────────────────────
        if (context.Workspace is { } workspace)
        {
            try
            {
                var patch = await gitSource.ReadPatchAsync(
                    workspace.Root, workspace.Provenance,
                    context.Job.GitBranchName, context.Project?.GitBaseBranch,
                    path, previousPath, IsUntrackedStatus(status), resolvedContext,
                    baseSha, headSha, _options.MaxPatchBytes, ct);

                return new JobPatchResult(patch with { JobId = jobId }, null, notes);
            }
            catch (JobChangeUnavailableException ex) when (!ct.IsCancellationRequested)
            {
                // Fall through with the reason attached: the fallback still answers the question, and
                // the reader still learns it is a fallback.
                logger?.LogDebug("Local patch for job {JobId} unavailable ({Reason}): {Message}",
                    jobId, ex.ReasonCode, ex.Message);
                notes.Add($"Checkout ({workspace.Provenance}): {ex.ReasonCode}: {ex.Message}");
            }
        }
        else
        {
            notes.Add($"Checkout: {JobChangeUnavailableReasons.NoWorkspace} — " +
                      "the control plane sees no working directory for this job.");
        }

        // A pinned pair only ever describes local commits. GitHub's comparison resolves refs, so
        // honouring a local SHA against the remote would silently answer a different question than the
        // one whose numbers are on screen — which is the invariant the whole pinning rule protects.
        if (pinned)
        {
            return new JobPatchResult(null,
                new JobChangeFailure(JobChangeUnavailableReasons.NoWorkspace,
                    "This patch was requested for commits in a checkout the control plane cannot read. " +
                    "Push the branch and drop the pinned SHAs to compare it on the remote instead."),
                notes);
        }

        // ── 2. The pushed branch's comparison payload ───────────────────────────
        var remote = await ReadRemoteAsync(context, ct);
        if (remote.ChangeSet is { } remoteSet)
        {
            var extracted = ExtractRemotePatch(remote.RawPayload, path, remoteSet, resolvedContext);
            if (extracted is not null)
            {
                return new JobPatchResult(extracted with { JobId = jobId }, null, notes);
            }

            return new JobPatchResult(null,
                new JobChangeFailure(JobChangeUnavailableReasons.PathNotInChangeSet,
                    path is null
                        ? "The pushed comparison carried no textual patches."
                        : $"'{path}' is not part of the pushed comparison for this branch."),
                notes);
        }

        notes.Add($"Pushed branch: {remote.Reason}");

        // ── 3. The checkpoint records paths, so it cannot produce a patch ───────
        return new JobPatchResult(null,
            new JobChangeFailure(JobChangeUnavailableReasons.NoChanges,
                "No diff could be produced: the checkout and the pushed branch both refused, and the " +
                "working checkpoint stores file names rather than content."),
            notes);
    }

    /// <summary>
    /// True when a list row reported a path that is present in the tree and absent from every commit.
    /// </summary>
    /// <remarks>
    /// Matches the same constant the local reader wrote into the row, so the two ends of the contract
    /// are one name and cannot drift.
    /// </remarks>
    private static bool IsUntrackedStatus(string? status) =>
        string.Equals(status, JobFileChangeKinds.Untracked, StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs the sources in precedence order and labels the answer.</summary>
    private async Task<JobChangeSetDto> ComposeAsync(
        JobChangeContext context,
        int limit,
        CancellationToken ct)
    {
        var branch = context.Job.GitBranchName;
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(branch))
        {
            return JobChangesPolicy.Unavailable(context.Job.Id, null, context.Project?.GitBaseBranch,
                JobChangeUnavailableReasons.NoBranch,
                "This job has no git branch, so there are no file changes to show.");
        }

        // `localUnavailable` rather than `localTouched`: a missing checkout is just as much a reason
        // for the reader to treat the remote list as a fallback as a checkout that refused. Flagging
        // only the refusal case would let a Kubernetes deployment present a committed-files-only list
        // as if it were the current state of the work, which is the distinction the flag exists to make.
        var localUnavailable = false;

        // ── 1. The checkout ─────────────────────────────────────────────────────
        if (context.Workspace is { } workspace)
        {
            var attempt = await ProbeAsync(() => gitSource.ReadAsync(
                workspace.Root, workspace.Provenance, branch, context.Project?.GitBaseBranch,
                _options with { MaxFiles = limit }, ct));

            if (attempt.ChangeSet is { } local)
            {
                return Finish(local, context, notes);
            }

            localUnavailable = true;
            notes.Add($"Checkout ({workspace.Provenance}): {attempt.Reason}");
        }
        else
        {
            localUnavailable = true;
            notes.Add($"Checkout: {JobChangeUnavailableReasons.NoWorkspace} — " +
                      "the control plane sees no working directory for this job.");
        }

        // ── 2. The pushed branch ───────────────────────────────────────────────
        var remote = await ReadRemoteAsync(context, ct);
        if (remote.ChangeSet is { } remoteSet)
        {
            return Finish(remoteSet with
            {
                Source = remoteSet.Source with
                {
                    Degraded = localUnavailable,
                    Message = localUnavailable
                        ? remoteSet.Source.Message + " The checkout could not be read, so this list is " +
                          "the pushed branch as it stands: anything not committed or not pushed is missing."
                        : remoteSet.Source.Message,
                },
            }, context, notes);
        }

        notes.Add($"Pushed branch: {remote.Reason}");

        // ── 3. The working checkpoint ──────────────────────────────────────────
        var checkpoint = ReadCheckpoint(context, limit);
        if (checkpoint is not null)
        {
            notes.Add("Working checkpoint: the file list the harness measured at the last turn boundary. " +
                      "It names files, not line counts, and there is no diff behind it.");

            return Finish(checkpoint with { Source = checkpoint.Source with { Degraded = true } }, context, notes);
        }

        return Finish(JobChangesPolicy.Unavailable(
            context.Job.Id, branch, context.Project?.GitBaseBranch,
            JobChangeUnavailableReasons.NoChanges,
            "No file changes could be read for this job — from a checkout, from the pushed branch, or from a checkpoint.",
            degraded: localUnavailable, notes), context, notes);
    }

    /// <summary>
    /// Reads the checkpoint's stored path list out of its <c>jsonb</c> column.
    /// </summary>
    /// <remarks>
    /// Re-implemented here rather than reusing
    /// <c>WorkspaceStateRepository.DeserializeModifiedFiles</c> because that helper is <c>internal</c>
    /// and Infrastructure's only <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/>
    /// grant is to the test project. Opening the assembly's internals to the API host to save fifteen
    /// lines of parsing would widen the one boundary that says which of these types a host may reach, so
    /// the parse lives here and the two are pinned together by a round-trip test through the
    /// repository's own serializer.
    /// </remarks>
    internal static IReadOnlyList<string> ReadCheckpointFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();

        try
        {
            // Default options on purpose: the column holds a JSON array of strings, so there is no
            // property naming to agree on with the writer's serializer settings. Anything that is not
            // an array of strings is the realistic corruption case, and it degrades to "files unknown"
            // below rather than throwing on a read endpoint.
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);

            return parsed is { Count: > 0 } ? parsed : Array.Empty<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            // A JSON array whose elements are not strings (e.g. an array of objects) — same handling.
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Stamps identity onto a probe's answer and merges the accumulated notes ahead of its own.
    /// </summary>
    /// <remarks>
    /// Notes from the skipped sources go <em>first</em>: they explain the choice, while a probe's own
    /// notes describe the answer it produced. Ordering them the other way round puts the reason after
    /// the result, and the reader scrolls past the explanation to reach a number they cannot trust.
    /// </remarks>
    private static JobChangeSetDto Finish(
        JobChangeSetDto dto,
        JobChangeContext context,
        IReadOnlyList<string> notes)
    {
        var merged = new List<string>(notes.Count + (dto.Notes?.Count ?? 0));
        merged.AddRange(notes);

        foreach (var note in dto.Notes ?? Array.Empty<string>())
        {
            if (!merged.Contains(note)) merged.Add(note);
        }

        merged.RemoveAll(string.IsNullOrWhiteSpace);

        return dto with
        {
            JobId = context.Job.Id,
            Branch = string.IsNullOrWhiteSpace(dto.Branch) ? context.Job.GitBranchName : dto.Branch,
            BaseBranch = string.IsNullOrWhiteSpace(dto.BaseBranch) ? context.Project?.GitBaseBranch : dto.BaseBranch,
            Notes = merged.Count > 0 ? merged : null,
            GeneratedAt = dto.GeneratedAt ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Asks GitHub about the pushed branch, or reports why it could not.</summary>
    private async Task<GitHubPullRequestService.JobChangeRemoteRead> ReadRemoteAsync(
        JobChangeContext context,
        CancellationToken ct)
    {
        // The job's own recorded remote wins over configuration: a multi-repo project's jobs do not all
        // live in the configured repository, and comparing the wrong one produces a confident wrong
        // answer with no error anywhere.
        var remoteOverride = context.WorkspaceState?.GitRemoteUrl;

        try
        {
            return await pullRequests.ReadBranchComparisonAsync(
                context.Job.GitBranchName, context.Project?.GitBaseBranch, remoteOverride, ct);
        }
        catch (JobChangeUnavailableException ex) when (!ct.IsCancellationRequested)
        {
            return GitHubPullRequestService.JobChangeRemoteRead.Fail(ex.ReasonCode, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Remote comparison for job {JobId} threw", context.Job.Id);
            return GitHubPullRequestService.JobChangeRemoteRead.Fail(
                JobChangeUnavailableReasons.RemoteUnavailable, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The working checkpoint as a change set: paths without stats, labelled as exactly that.
    /// </summary>
    /// <remarks>
    /// The stored list is capped when it is written
    /// (<c>WorkspaceStateRepository.MaxPersistedModifiedFiles</c>) and a truncation marker
    /// element is appended to say so. That marker is parsed back out here and turned into
    /// <see cref="JobChangeStats.OmittedFiles"/> rather than rendered as a file named
    /// <c>… 3 more (truncated; count is exact)</c>, which is what mapping the stored array naively
    /// would put on screen.
    /// </remarks>
    /// <param name="limit">
    /// The caller's cap, the same one the git source receives. Honouring it here is not
    /// bookkeeping-for-its-own-sake: a source that ignored the limit would return more rows than the
    /// client asked for and then report an <c>OmittedFiles</c> of zero, so the panel's "showing N of M"
    /// line would contradict the list beneath it — and the checkpoint source, whose lists are capped
    /// upstream at sixty anyway, is exactly where the discrepancy would hide.
    /// </param>
    private JobChangeSetDto? ReadCheckpoint(JobChangeContext context, int limit)
    {
        var state = context.WorkspaceState;
        if (state is null || !state.HasCheckpoint) return null;

        var (paths, omitted) = SplitCheckpointFiles(ReadCheckpointFiles(state.CheckpointModifiedFilesJson));
        var entries = new List<ChangedFileEntry>(paths.Count);

        foreach (var value in paths)
        {
            var normalized = GitChangesParser.NormalizeRelativePath(value);
            if (normalized.Length == 0 || !JobChangeMerge.IsSafeRelativePath(normalized)) continue;

            entries.Add(new ChangedFileEntry(normalized, JobFileChangeKinds.Modified));
        }

        if (entries.Count == 0 && omitted == 0) return null;

        // patchAvailable: false — a checkpoint stores names, not content, so offering a patch would be
        // a guaranteed empty result the reader would interpret as "the file did not really change".
        var (files, stats, truncated) = JobChangeMerge.Join(
            entries, Array.Empty<ChangedFileEntry>(), limit, patchAvailable: false);

        return new JobChangeSetDto(
            JobId: context.Job.Id,
            Branch: string.IsNullOrWhiteSpace(state.CheckpointBranch) ? context.Job.GitBranchName : state.CheckpointBranch,
            BaseBranch: context.Project?.GitBaseBranch,
            HeadSha: state.CheckpointHeadCommit,
            BaseSha: null,
            // Both totals grow by the marker's count. JobChangeStats.FilesChanged is documented as the
            // real total even when the list is capped, and the checkpoint's cap happened upstream in the
            // writer: reporting 60 of 63 here would understate the blast radius of the change, which is
            // the one number a reader consults this panel to judge.
            Stats: stats with
            {
                FilesChanged = stats.FilesChanged + omitted,
                OmittedFiles = stats.OmittedFiles + omitted,
            },
            Files: files,
            Source: new JobChangeSourceInfo(
                JobChangesSources.Checkpoint,
                ReasonCode: null,
                Message: "The working checkpoint written at the last turn boundary.",
                Degraded: true,
                CommittedOnly: true,
                Truncated: truncated || omitted > 0),
            Commits: Array.Empty<JobChangeCommitDto>(),
            HasUncommittedChanges: entries.Count > 0,
            UncommittedPaths: files.Select(f => f.Path).Take(JobChangesPolicy.UncommittedPathLimit).ToList(),
            UncommittedCount: entries.Count,
            GeneratedAt: state.CheckpointUpdatedAt ?? state.UpdatedAt);
    }

    /// <summary>
    /// Counts how many paths the stored checkpoint list dropped, from its length alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The writer caps the array at
    /// <see cref="WorkspaceStateRepository.MaxPersistedModifiedFiles"/> elements and appends exactly one
    /// marker when it truncates, so an element past the cap <em>is</em> the marker. Detecting it by
    /// position rather than by matching its prose is the difference between a contract and a
    /// coincidence: the marker's wording ("… N more (truncated; count is exact)") is display text that
    /// somebody may reword, and a parser keyed to that wording starts keeping the marker as a file
    /// named <c>… 3 more (truncated; count is exact)</c> the day somebody does.
    /// </para>
    /// <para>
    /// The dropped count is then taken from the marker's digits when they are there, and floored at one
    /// when they are not: the length alone already proves something was dropped, so an unparseable
    /// marker degrades to "at least one more" rather than to a false "nothing was omitted".
    /// </para>
    /// </remarks>
    internal static (IReadOnlyList<string> Paths, int Omitted) SplitCheckpointFiles(IReadOnlyList<string> stored)
    {
        var cap = WorkspaceStateRepository.MaxPersistedModifiedFiles;
        if (stored.Count <= cap) return (stored, 0);

        var paths = new List<string>(cap);
        var omitted = 0;

        for (var i = 0; i < stored.Count; i++)
        {
            if (i < cap)
            {
                paths.Add(stored[i]);
                continue;
            }

            // Everything past the cap is marker material. Sum rather than assign so a list that somehow
            // carries two markers still reports both, and never let a parse failure claim zero omissions
            // once the length has already proved there was one.
            var parsed = TryParseDroppedCount(stored[i]);
            omitted += parsed ?? 1;
        }

        return (paths, omitted);
    }

    /// <summary>
    /// Reads the count out of a marker element — the first run of digits in it, skipping any leading
    /// punctuation — or null when the element carries none.
    /// </summary>
    /// <remarks>
    /// The writer's marker is <c>… 3 more (truncated; count is exact)</c>, so the digits are preceded
    /// by an ellipsis and a space: scanning from the first character rather than for the first digit
    /// returns nothing and the omission silently degrades to a count of one, understating how much of
    /// the list was dropped. Taking the first digit run (not the last, and not "all digits") keeps
    /// working if the wording gains another number later.
    /// </remarks>
    private static int? TryParseDroppedCount(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var start = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsDigit(value[i])) { start = i; break; }
        }
        if (start < 0) return null;

        var length = 0;
        while (start + length < value.Length && char.IsDigit(value[start + length])) length++;

        return int.TryParse(value.AsSpan(start, length), out var parsed) && parsed > 0 ? parsed : null;
    }

    /// <summary>
    /// Pulls one file's patch out of the comparison payload GitHub already returned, or assembles the
    /// whole change set's from its per-file patches.
    /// </summary>
    /// <remarks>
    /// GitHub's compare payload carries a unified <c>patch</c> per file, so a per-file view costs no
    /// second request. The whole-branch view re-emits git's own <c>diff --git</c> header (including the
    /// preimage when GitHub reported a rename) so the browser needs one patch parser rather than one
    /// per source. A file GitHub gives no patch for — a binary — yields an empty body with
    /// <see cref="JobChangePatchDto.Binary"/> set, which is the truth: there is no textual diff, and the
    /// panel says so instead of showing a blank pane.
    /// </remarks>
    private static JobChangePatchDto? ExtractRemotePatch(
        string? rawPayload,
        string? path,
        JobChangeSetDto changeSet,
        int contextLines)
    {
        if (string.IsNullOrWhiteSpace(rawPayload)) return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(rawPayload);
            if (!TryGetProperty(document.RootElement, "files", out var files) ||
                files.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return null;
            }

            var wanted = path is null ? null : GitChangesParser.NormalizeRelativePath(path);
            var assembled = new System.Text.StringBuilder();
            var found = false;

            foreach (var file in files.EnumerateArray())
            {
                var filePath = GitChangesParser.NormalizeRelativePath(ReadString(file, "filename"));
                if (filePath.Length == 0) continue;
                if (wanted is not null && !string.Equals(filePath, wanted, StringComparison.Ordinal)) continue;

                found = true;

                var patch = ReadString(file, "patch");
                var previous = GitChangesParser.NormalizeRelativePath(ReadString(file, "previous_filename"));
                var row = changeSet.Files.FirstOrDefault(f => string.Equals(f.Path, filePath, StringComparison.Ordinal));

                if (wanted is not null)
                {
                    return new JobChangePatchDto(
                        changeSet.JobId, changeSet.Branch, changeSet.BaseBranch,
                        changeSet.HeadSha, changeSet.BaseSha,
                        Path: wanted,
                        PreviousPath: previous.Length == 0 ? null : previous,
                        Status: row?.Status ?? GitChangesParser.MapGitHubStatus(ReadString(file, "status")),
                        Binary: patch.Length == 0 && row?.Binary == true,
                        Empty: patch.Length == 0,
                        Patch: patch,
                        Truncated: false,
                        Additions: row?.Additions,
                        Deletions: row?.Deletions,
                        ContextLines: contextLines,
                        Source: changeSet.Source);
                }

                var source = previous.Length == 0 ? filePath : previous;
                assembled.Append("diff --git a/").Append(source).Append(" b/").Append(filePath).Append('\n');
                assembled.Append(patch.Length == 0 ? "# (no textual patch)\n" : patch + "\n");
            }

            if (!found) return null;

            var body = assembled.ToString();
            var limit = JobChangesPolicy.DefaultPatchByteLimit;
            var truncated = body.Length > limit;

            return new JobChangePatchDto(
                changeSet.JobId, changeSet.Branch, changeSet.BaseBranch,
                changeSet.HeadSha, changeSet.BaseSha,
                Path: null, PreviousPath: null, Status: null,
                Binary: false, Empty: body.Length == 0,
                Patch: truncated ? body[..limit] : body,
                Truncated: truncated,
                ContextLines: contextLines,
                Source: changeSet.Source);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Case-insensitive property lookup: GitHub's payloads are camelCase while a stubbed or proxied
    /// response can carry whatever the author typed, and a mapper that reads one spelling renders an
    /// empty panel with no error — the failure every DTO mapper in this repo guards against.
    /// </summary>
    private static bool TryGetProperty(
        System.Text.Json.JsonElement element, string name, out System.Text.Json.JsonElement value)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value)) return true;

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string ReadString(System.Text.Json.JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private async Task<(JobChangeSetDto? ChangeSet, string? Reason)> ProbeAsync(
        Func<Task<JobChangeSetDto>> probe)
    {
        try
        {
            return (await probe(), null);
        }
        catch (JobChangeUnavailableException ex)
        {
            // Any refusal is "this source could not answer", including NoChanges, which the git source
            // reports rather than throwing today. Returning (null, reason) unconditionally is what keeps
            // a filtered catch from letting one code path escape ComposeAsync as a 500 on a read
            // endpoint — the shape this method exists to prevent.
            return (null, $"{ex.ReasonCode}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An unexpected throw from a source is still a source that could not answer: the panel
            // keeps its fallback and the reason lands in the notes and the log rather than becoming a
            // 500 on a read endpoint.
            logger?.LogWarning(ex, "A job change source threw unexpectedly");
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<JobChangeContext?> LoadContextAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return null;

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == job.ProjectId, ct);

        // The job's most recently updated workspace row: the same "active row" rule
        // JobWorkspaceService documents. Read directly rather than through it because this needs the
        // checkpoint columns, which WorkspaceSnapshot does not project, and widening that record for a
        // second consumer would change a contract three other callers depend on.
        var workspaceState = await db.JobWorkspaceStates.AsNoTracking()
            .Where(s => s.JobId == jobId)
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        var candidates = workspaces.Candidates(job, project);

        return new JobChangeContext(job, project,
            candidates.Count > 0 ? candidates[0] : null, workspaceState);
    }

    /// <summary>The job, its project, the best visible checkout, and the workspace row.</summary>
    private sealed record JobChangeContext(
        ProjectJob Job,
        Project? Project,
        JobWorkspaceCandidate? Workspace,
        JobWorkspaceState? WorkspaceState);
}

/// <summary>
/// Why a source could not answer, in the shape the response carries.
/// </summary>
/// <remarks>
/// A named record rather than a <c>(string, string)</c> tuple, because the tuple version is a
/// *nullable value type*: every consumer has to write <c>Reason!.Value.Code</c>, and the compiler
/// cannot stop one of them from dereferencing the nullable wrapper instead of noticing the inner pair
/// is never null. A reference type makes <c>Reason?.Code</c> both the natural and the only spelling.
/// </remarks>
public sealed record JobChangeFailure(string Code, string Message);

/// <summary>A patch read's outcome: the patch, or the reason there is none, plus what was tried.</summary>
public sealed record JobPatchResult(
    JobChangePatchDto? Patch,
    JobChangeFailure? Reason,
    IReadOnlyList<string>? Notes = null)
{
    public bool Found => Patch is not null;
}
