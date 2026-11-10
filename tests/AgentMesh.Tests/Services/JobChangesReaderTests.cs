using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The decision the Changes tab is built on: which checkout a job's diff is measured in, what happens
/// when that checkout cannot answer honestly, and what the reader is told about the answer it gets.
/// </summary>
/// <remarks>
/// <para>
/// These tests drive <see cref="JobChangesService"/> through a scripted
/// <see cref="IJobGitCommandRunner"/> and a stub of the remote comparison, because the behaviour under
/// test is a <em>choice</em> the service makes: read the checkout, fall back to the pushed branch, fall
/// back to the checkpoint, or say plainly that nothing could be read. A git stub that always succeeds
/// would exercise none of that, and a real repository cannot produce "the branch is not in this
/// checkout" or "the remote refused" on demand.
/// </para>
/// <para>
/// Failures assert on the <see cref="JobChangeSourceInfo.ReasonCode"/> and on
/// <see cref="JobChangeSetDto.Source"/>.<see cref="JobChangeSourceInfo.Kind"/>, not on prose. The prose
/// is what a panel renders and a reviewer may want reworded; the code is what the panel switches on,
/// and the two have different stability requirements. Where a message is asserted, it is asserted on a
/// substring that carries the reader's decision rather than on the sentence around it.
/// </para>
/// <para>
/// The git fixture (<see cref="ForkPointGit"/>) is shared by every flow. Several earlier failures in
/// this file came from a second copy of it that had drifted, so there is one script and each test
/// overrides only the command its scenario turns on.
/// </para>
/// </remarks>
public class JobChangesReaderTests : IDisposable
{
    private static readonly Guid JobId = Guid.Parse("375e39bc-1111-4222-8333-444455556666");
    private const string ProjectId = "job-changes-reader-test";
    private const string Branch = "mesh/job-375e39bc";
    private const string BaseBranch = "main";

    private const string BaseSha = "1111111111111111111111111111111111111111";
    private const string HeadSha = "2222222222222222222222222222222222222222";

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "job-changes-reader-" + Guid.NewGuid().ToString("N"));

    public JobChangesReaderTests()
    {
        // A directory containing a .git directory is the locator's entire precondition for offering a
        // candidate, and the reader's for shelling out. Created as a plain directory rather than with
        // `git init`: no test here executes git, so none should require it to be installed.
        Directory.CreateDirectory(Path.Combine(_workspace, ".git"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    // ── the fallback ladder ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetChangeSet_WhenTheCheckoutAnswers_UsesTheLocalSourceWithoutAskingTheRemote()
    {
        var git = new ScriptedGit(ForkPointGit);
        var remote = new StubPullRequests();

        var dto = await Build(git, remote).GetChangeSetAsync(JobId, ct: CancellationToken.None);

        dto!.Source.Kind.Should().Be(JobChangesSources.LocalGit);
        dto.Stats.FilesChanged.Should().Be(1);
        dto.Files[0].Path.Should().Be("src/keep.txt");
        remote.CallCount.Should().Be(0, "the checkout answered, so nothing should be spent on the remote");
    }

    [Fact]
    public async Task GetChangeSet_WhenTheCheckoutHasNoJobBranch_FallsBackToTheRemoteAndLabelsItAFallback()
    {
        // A checkout that does not have the ref must refuse rather than diff HEAD and label the result
        // with the job's branch. Refusing is what makes the fallback possible; labelling it is what
        // keeps a committed-files-only list from reading as the current state of the work.
        var git = new ScriptedGit(args => args[0] == "rev-parse"
            ? JobGitResult.Fail("not a valid object name '" + Branch + "'")
            : ForkPointGit(args));

        var remote = new StubPullRequests(Ok: Comparison());

        var dto = await Build(git, remote).GetChangeSetAsync(JobId, ct: CancellationToken.None);

        dto!.Source.Kind.Should().Be(JobChangesSources.GitHubCompare);
        dto.Source.Degraded.Should().BeTrue();
        dto.Notes.Should().Contain(n => n.Contains(JobChangeUnavailableReasons.UnknownRef),
            "the reason the better source was skipped belongs in front of the answer it was skipped for");
        dto.Source.Message.Should().Contain("could not be read",
            "a committed-files-only list must not read as the current state of the work");
    }

    [Fact]
    public async Task GetChangeSet_WhenNoCheckoutExists_IsAlsoMarkedDegradedAndSaysWhy()
    {
        // The Kubernetes topology, where the API has no view of a runner pod's filesystem — the same
        // distinction that makes open_pull_request report no_working_directory there. Only a checkout
        // that *refused* used to set the degraded flag, which let this deployment present a
        // committed-only list as if it were current.
        var service = Build(new ScriptedGit(_ => JobGitResult.Fail("no repo")),
            new StubPullRequests(Ok: Comparison()),
            locator: new EmptyLocator());

        var dto = await service.GetChangeSetAsync(JobId, ct: CancellationToken.None);

        dto!.Source.Kind.Should().Be(JobChangesSources.GitHubCompare);
        dto.Source.Degraded.Should().BeTrue();
        dto.Source.CommittedOnly.Should().BeTrue();
        dto.Notes.Should().Contain(n => n.Contains(JobChangeUnavailableReasons.NoWorkspace));
    }

    [Fact]
    public async Task GetChangeSet_RemoteNotesAreKeptAndOrderedAfterTheSkippedSource()
    {
        // Both notes are load-bearing and their order is the reading order: the reason first, then the
        // caveat about the answer. Replacing one with the other loses whichever the next feature needs.
        var service = Build(new ScriptedGit(_ => JobGitResult.Fail("fatal: bad revision")),
            new StubPullRequests(Ok: Comparison(behindBy: 4)),
            locator: new EmptyLocator());

        var dto = await service.GetChangeSetAsync(JobId, ct: CancellationToken.None);

        dto!.Notes.Should().NotBeNull();
        dto.Notes![0].Should().Contain("Checkout");
        dto.Notes.Should().Contain(n => n.Contains("4 commit(s) behind"));
    }

    [Fact]
    public async Task GetChangeSet_WhenEverySourceRefuses_IsNotReportedAsAnEmptyChangeSet()
    {
        // The single most misleading answer this feature could give: an empty list with no reason reads
        // as "the agent changed nothing", which is a claim about the work rather than about the
        // platform's ability to look at it.
        var service = Build(
            new ScriptedGit(_ => JobGitResult.Fail("fatal: bad revision")),
            new StubPullRequests(FailCode: JobChangeUnavailableReasons.RemoteUnavailable,
                FailMessage: "no credentials configured"));

        var dto = await service.GetChangeSetAsync(JobId, ct: CancellationToken.None);

        dto!.Source.Kind.Should().BeNull();
        dto.Source.ReasonCode.Should().NotBeNull();
        dto.Stats.FilesChanged.Should().Be(0);
        dto.Notes.Should().NotBeNullOrEmpty(
            "a reader must be able to tell a git failure from a missing token from an empty branch");
    }

    [Fact]
    public async Task GetChangeSet_WhenTheJobHasNoBranch_ReportsNoBranchWithoutTouchingAnySource()
    {
        using var db = NewDb(branch: "");

        var git = new ScriptedGit(_ => JobGitResult.Ok(""));
        var remote = new StubPullRequests(Ok: Comparison());

        var dto = await Build(git, remote, db: db).GetChangeSetAsync(JobId, ct: CancellationToken.None);

        dto!.Source.ReasonCode.Should().Be(JobChangeUnavailableReasons.NoBranch);
        git.Calls.Should().BeEmpty();
        remote.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetChangeSet_WhenTheJobDoesNotExist_ReturnsNullForTheRouteToTurnInto404()
    {
        var service = Build(new ScriptedGit(_ => JobGitResult.Ok("")), new StubPullRequests());

        (await service.GetChangeSetAsync(Guid.NewGuid(), ct: CancellationToken.None)).Should().BeNull();
    }

    // ── reuse ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetChangeSet_SecondCallWithinTheWindow_ReusesTheServerSideRead()
    {
        // One panel open renders one list and then a patch per click. Without reuse, a job with twelve
        // changed files costs sixty git invocations in a live checkout.
        var git = new ScriptedGit(ForkPointGit);
        var service = Build(git, new StubPullRequests(), cache: new JobChangesCache());

        await service.GetChangeSetAsync(JobId, ct: CancellationToken.None);
        var afterFirst = git.Invocations;
        afterFirst.Should().BeGreaterThan(0);

        await service.GetChangeSetAsync(JobId, ct: CancellationToken.None);

        git.Invocations.Should().Be(afterFirst);
    }

    [Fact]
    public async Task GetChangeSet_RefreshTrue_IsTheAnswerToAnAgentThatCommittedSecondsAgo()
    {
        var git = new ScriptedGit(ForkPointGit);
        var service = Build(git, new StubPullRequests(), cache: new JobChangesCache());

        await service.GetChangeSetAsync(JobId, ct: CancellationToken.None);
        var afterFirst = git.Invocations;

        await service.GetChangeSetAsync(JobId, refresh: true, ct: CancellationToken.None);

        git.Invocations.Should().BeGreaterThan(afterFirst,
            "the panel's refresh button must never be able to return a cached answer");
    }

    [Fact]
    public async Task GetChangeSet_ASkippedSourceIsNotCachedSoRefreshRecoversWithinOneCall()
    {
        // A refusal is a transient in the common case — a runner that has not pushed yet, a checkout
        // mid-write — and a panel stuck reporting "no changes" for seconds after the agent committed is
        // worse than the extra invocation.
        var service = Build(new ScriptedGit(_ => JobGitResult.Fail("fatal: bad revision")),
            new StubPullRequests(FailCode: JobChangeUnavailableReasons.RemoteUnavailable, FailMessage: "no token"));

        var first = await service.GetChangeSetAsync(JobId, ct: CancellationToken.None);
        first!.Source.Kind.Should().BeNull();

        var recovered = await Build(new ScriptedGit(ForkPointGit), new StubPullRequests(),
            cache: new JobChangesCache()).GetChangeSetAsync(JobId, ct: CancellationToken.None);

        recovered!.Source.Kind.Should().Be(JobChangesSources.LocalGit);
    }

    // ── patches ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPatch_UntrackedRowFromTheList_IsReadOneSidedAgainstTheEmptyFile()
    {
        // The defect this pins: an untracked file is in no tree, so a pathspec-limited committed diff
        // exits 0 with an empty body, and the panel renders "no changes" for the one file in the list
        // that certainly did change. The route is chosen from the status the list carried.
        var git = new ScriptedGit(args => args.Contains("--no-index")
            ? new JobGitResult(1,
                "diff --git a/src/fresh.txt b/src/fresh.txt\n--- /dev/null\n+++ b/src/fresh.txt\n@@ -0,0 +1 @@\n+hello\n", "")
            : ForkPointGit(args));

        var result = await Build(git, new StubPullRequests()).GetPatchAsync(
            JobId, "src/fresh.txt", status: JobFileChangeKinds.Untracked, ct: CancellationToken.None);

        result!.Found.Should().BeTrue();
        result.Patch!.Patch.Should().Contain("+hello");
        result.Patch.Status.Should().Be(JobFileChangeKinds.Untracked);
        git.Calls.Should().Contain(c => c.Contains("--no-index"));
    }

    [Fact]
    public async Task GetPatch_TrackedRow_IsNotRoutedThroughTheOneSidedRead()
    {
        // The other half of the pair above: deriving the route from the status means a modified file
        // keeps its real base→head diff instead of being rendered as an entirely new file.
        var git = new ScriptedGit(ForkPointGit);

        var result = await Build(git, new StubPullRequests()).GetPatchAsync(
            JobId, "src/keep.txt", status: JobFileChangeKinds.Modified, ct: CancellationToken.None);

        result!.Found.Should().BeTrue();
        git.Calls.Should().NotContain(c => c.Contains("--no-index"));
    }

    [Fact]
    public async Task GetPatch_RenameRow_ForwardsBothPathspecsWithThePreimageFromTheList()
    {
        // Without the preimage git prints the whole destination as additions — verified against 2.43,
        // where a two-line edit rendered as sixty added lines. The list already carries the preimage,
        // so the round trip through the client is what keeps the patch honest.
        var git = new ScriptedGit(ForkPointGit);

        await Build(git, new StubPullRequests()).GetPatchAsync(
            JobId, "src/renamed-big.txt", previousPath: "src/big.txt",
            status: JobFileChangeKinds.Renamed, ct: CancellationToken.None);

        var diffCall = git.Calls.Last(c => c[0] == "diff" && c.Contains("--"));
        diffCall.Should().Contain("src/renamed-big.txt");
        diffCall.Should().Contain("src/big.txt");
    }

    [Fact]
    public async Task GetPatch_WholeChangeSet_DiffsFromTheForkPointAgainstTheSamePairTheListReported()
    {
        var git = new ScriptedGit(ForkPointGit);
        var service = Build(git, new StubPullRequests());

        var list = await service.GetChangeSetAsync(JobId, ct: CancellationToken.None);
        var patch = await service.GetPatchAsync(JobId, path: null, ct: CancellationToken.None);

        patch!.Found.Should().BeTrue();
        patch.Patch!.BaseSha.Should().Be(list!.BaseSha);
        patch.Patch.HeadSha.Should().Be(list.HeadSha);

        var diffCall = git.Calls.Last(c => c[0] == "diff");
        diffCall.Should().Contain(list.BaseSha);
        diffCall.Should().Contain(list.HeadSha);
        diffCall.Should().NotContain(BaseBranch + "..." + Branch,
            "three-dot form would re-derive the merge base per call, so a base branch that moved in between would silently change what 'the patch' means");
    }

    [Fact]
    public async Task GetPatch_PinnedShas_AreHonouredInsteadOfReResolvingTheForkPoint()
    {
        var git = new ScriptedGit(ForkPointGit);
        const string pinnedBase = "3333333333333333333333333333333333333333";
        const string pinnedHead = "4444444444444444444444444444444444444444";

        await Build(git, new StubPullRequests()).GetPatchAsync(
            JobId, "src/keep.txt", baseSha: pinnedBase, headSha: pinnedHead, ct: CancellationToken.None);

        var diffCall = git.Calls.Last(c => c[0] == "diff");
        diffCall.Should().Contain(pinnedBase);
        diffCall.Should().Contain(pinnedHead);
        git.Calls.Should().NotContain(c => c[0] == "merge-base",
            "the caller already named the pair its counts came from; re-deriving could answer a different question than the one on screen");
    }

    [Fact]
    public async Task GetPatch_PinnedShasWithoutACheckout_AreRefusedRatherThanResolvedRemotely()
    {
        // GitHub resolves refs, not local commits. Honouring a pinned SHA remotely would print the
        // caller's SHAs above a diff computed from something else — the exact contradiction the
        // pinning rule exists to prevent, so the refusal is the feature working.
        var service = Build(new ScriptedGit(_ => JobGitResult.Ok("")),
            new StubPullRequests(Ok: Comparison()),
            locator: new EmptyLocator());

        var result = await service.GetPatchAsync(
            JobId, "src/keep.txt", baseSha: BaseSha, headSha: HeadSha, ct: CancellationToken.None);

        result!.Found.Should().BeFalse();
        result.Reason!.Code.Should().Be(JobChangeUnavailableReasons.NoWorkspace);
    }

    [Fact]
    public async Task GetPatch_EverySourceRefused_ExplainsThatTheCheckpointCannotProduceADiff()
    {
        var service = Build(new ScriptedGit(_ => JobGitResult.Fail("fatal: bad revision")),
            new StubPullRequests(FailCode: JobChangeUnavailableReasons.RemoteFailed,
                FailMessage: "branch never pushed"));

        var result = await service.GetPatchAsync(JobId, "src/keep.txt", ct: CancellationToken.None);

        result!.Found.Should().BeFalse();
        result.Reason!.Code.Should().Be(JobChangeUnavailableReasons.NoChanges);
        // The checkout refuses because the branch is absent from it — a different and more actionable
        // fact than "git is broken", and the one the panel should show.
        result.Notes.Should().Contain(n => n.Contains(JobChangeUnavailableReasons.UnknownRef));
        result.Notes.Should().Contain(n => n.Contains("branch never pushed"));
    }

    [Fact]
    public async Task GetPatch_RemoteComparisonSuppliesThePatchWhenThereIsNoCheckout()
    {
        // GitHub's compare payload carries a unified patch per file, so a per-file view on Kubernetes
        // costs no second request and still renders.
        var service = Build(new ScriptedGit(_ => JobGitResult.Fail("no repo")),
            new StubPullRequests(Ok: Comparison()),
            locator: new EmptyLocator());

        var result = await service.GetPatchAsync(JobId, "src/keep.txt", ct: CancellationToken.None);

        result!.Found.Should().BeTrue();
        result.Patch!.Patch.Should().Contain("+B changed");
        result.Patch.Source!.Kind.Should().Be(JobChangesSources.GitHubCompare);
        result.Patch.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task GetPatch_PathAbsentFromTheRemoteComparison_IsReportedAsNotInTheChangeSet()
    {
        var service = Build(new ScriptedGit(_ => JobGitResult.Fail("no repo")),
            new StubPullRequests(Ok: Comparison()),
            locator: new EmptyLocator());

        var result = await service.GetPatchAsync(JobId, "src/never-touched.txt", ct: CancellationToken.None);

        result!.Found.Should().BeFalse();
        result.Reason!.Code.Should().Be(JobChangeUnavailableReasons.PathNotInChangeSet);
    }

    // ── the local reader's own rules ────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_ReportsTheForkPointAsTheBaseNotTheBaseBranchTip()
    {
        // Diffing against the base branch's tip is how a job branch that has been alive for a week
        // shows a reviewer four hundred files of other people's merged work as its own change.
        var git = new ScriptedGit(ForkPointGit);

        var dto = await BuildGit(git).ReadAsync(_workspace, "test", Branch, BaseBranch,
            new JobChangesOptions(), CancellationToken.None);

        dto.BaseSha.Should().Be(BaseSha);
        dto.HeadSha.Should().Be(HeadSha);
    }

    [Fact]
    public async Task ReadAsync_PrefersTheRemoteTrackingBaseBecauseAForkPointAgainstAStaleLocalBaseDropsChanges()
    {
        // A runner's local base branch is exactly as current as its last fetch; its remote-tracking
        // ref is what it fetched for this turn. Taking a merge base against the stale local ref yields
        // a *later* commit than the true fork point, which silently omits real changes from the diff —
        // a wrong answer with no error, worth one extra rev-parse to avoid.
        var git = new ScriptedGit(args => args[0] switch
        {
            "rev-parse" when args.Count > 3 && args[3].StartsWith(Branch) => JobGitResult.Ok(HeadSha),
            "rev-parse" => JobGitResult.Ok(BaseSha),
            _ => ForkPointGit(args),
        });

        await BuildGit(git).ReadAsync(_workspace, "test", Branch, BaseBranch,
            new JobChangesOptions(), CancellationToken.None);

        // Every ref is probed as `<ref>{commit}`; the first probe that is not the job's own branch is
        // the first base candidate, and it must be the remote-tracking form of the *configured* base
        // rather than a hardcoded main/master or the runner's possibly-stale local ref.
        var baseLookups = git.Calls
            .Where(c => c[0] == "rev-parse" && c.Length > 3 && !c[3].StartsWith(Branch, StringComparison.Ordinal))
            .Select(c => c[3])
            .ToList();

        baseLookups.Should().NotBeEmpty();
        baseLookups[0].Should().Be($"origin/{BaseBranch}{{commit}}",
            "a fork point taken against a stale local base branch is a later commit than the true fork " +
            "point, which silently drops real changes from the diff with no error to show for it");

        // The resolution loop stops at the first ref that resolves, so a fixture where the
        // remote-tracking ref exists must probe it and *only* it. Asserting that the local ref is also
        // consulted here would be asserting that the reader keeps searching after it has its answer.
        baseLookups.Should().HaveCount(1,
            "the base is settled as soon as a candidate resolves; every later probe is work for nothing");
    }

    [Fact]
    public async Task ReadAsync_FallsBackToTheLocalBaseWhenNoRemoteTrackingRefExists()
    {
        // The pairing of the previous test: a bare clone or a repository whose remote was renamed has
        // no origin/<base> at all, so refusing to answer would be wrong — the local ref is the right
        // base there. The order is what matters, not the count.
        var git = new ScriptedGit(args => args[0] switch
        {
            "rev-parse" when args.Count > 3 && args[3].StartsWith(Branch, StringComparison.Ordinal) => JobGitResult.Ok(HeadSha),
            // Only the local main resolves in this topology.
            "rev-parse" when args.Count > 3 && args[3] == $"{BaseBranch}{{commit}}" => JobGitResult.Ok(BaseSha),
            "rev-parse" => JobGitResult.Fail(""),
            _ => ForkPointGit(args),
        });

        var dto = await BuildGit(git).ReadAsync(_workspace, "test", Branch, BaseBranch,
            new JobChangesOptions(), CancellationToken.None);

        var baseLookups = git.Calls
            .Where(c => c[0] == "rev-parse" && c.Length > 3 && !c[3].StartsWith(Branch, StringComparison.Ordinal))
            .Select(c => c[3])
            .ToList();

        baseLookups[0].Should().Be($"origin/{BaseBranch}{{commit}}", "the fresher candidate is tried first");
        baseLookups.Should().Contain($"{BaseBranch}{{commit}}",
            "and the local ref answers when there is no remote-tracking one to answer");
        dto.BaseSha.Should().Be(BaseSha);
    }

    [Fact]
    public async Task ReadAsync_UncommittedAndUntrackedWork_AreReportedAsNeitherBeingOnTheBranch()
    {
        var git = new ScriptedGit(args => args[0] == "status"
            ? JobGitResult.Ok(" M src/keep.txt\0?? src/fresh.txt\0")
            : ForkPointGit(args));

        var dto = await BuildGit(git).ReadAsync(_workspace, "test", Branch, BaseBranch,
            new JobChangesOptions(), CancellationToken.None);

        dto.HasUncommittedChanges.Should().BeTrue();
        dto.UncommittedCount.Should().Be(2);
        dto.UncommittedPaths.Should().Contain("src/fresh.txt");
        dto.Files.Should().Contain(f => f.Path == "src/fresh.txt" && f.Status == JobFileChangeKinds.Untracked);
        dto.Notes.Should().Contain(n => n.Contains("not committed on the branch"));
    }

    [Fact]
    public async Task ReadAsync_StatusFailure_StillAnswersFromTheCommittedDiffRatherThanErroring()
    {
        // Losing the uncommitted indicator is a smaller loss than losing the whole change set, and the
        // committed list is the part that will match what the reviewer sees on GitHub.
        var git = new ScriptedGit(args => args[0] == "status"
            ? JobGitResult.Fail("fatal: unable to read tree")
            : ForkPointGit(args));

        var dto = await BuildGit(git).ReadAsync(_workspace, "test", Branch, BaseBranch,
            new JobChangesOptions(), CancellationToken.None);

        dto.Stats.FilesChanged.Should().Be(1);
        dto.HasUncommittedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task ReadAsync_WhenNoBaseRefResolves_ExplainsThatThereIsNothingToCompareAgainst()
    {
        var git = new ScriptedGit(args => args[0] switch
        {
            "rev-parse" when args.Count > 3 && args[3].StartsWith(Branch) => JobGitResult.Ok(HeadSha),
            "rev-parse" => JobGitResult.Fail(""),
            _ => JobGitResult.Fail("unexpected"),
        });

        var act = () => BuildGit(git).ReadAsync(_workspace, "test", Branch, BaseBranch,
            new JobChangesOptions(), CancellationToken.None);

        (await act.Should().ThrowAsync<JobChangeUnavailableException>())
            .Which.ReasonCode.Should().Be(JobChangeUnavailableReasons.UnknownRef);
    }

    [Fact]
    public async Task ReadAsync_WhenTheBranchSharesNoHistoryWithAnyBase_ComparesFromTheEmptyTreeAndSaysSo()
    {
        // A shallow clone or an unrelated import. "Every file is added" is correct here, but only
        // because it is computed against the empty tree and annotated — otherwise the reader cannot
        // tell that the diff is comparing against nothing rather than against a real base.
        var git = new ScriptedGit(args => args[0] switch
        {
            "rev-parse" when args.Count > 3 && args[3].StartsWith(Branch) => JobGitResult.Ok(HeadSha),
            "rev-parse" => JobGitResult.Ok(BaseSha),
            "merge-base" => JobGitResult.Fail("no merge base"),
            _ => ForkPointGit(args),
        });

        var dto = await BuildGit(git).ReadAsync(_workspace, "test", Branch, BaseBranch,
            new JobChangesOptions(), CancellationToken.None);

        dto.BaseSha.Should().Be(JobChangesGitSource.EmptyTreeSha);
        dto.Notes.Should().Contain(n => n.Contains("no history"));
    }

    [Fact]
    public async Task ReadAsync_OversizedPayload_IsRefusedRatherThanReportedAsASmallerChange()
    {
        // A truncated numstat payload reads as a change with fewer files in it. Truncation must never
        // be able to produce a confident undercount.
        var git = new ScriptedGit(args => args[0] == "diff"
            ? JobGitResult.Ok(string.Empty, exitCode: 1) with { OutputTruncated = true }
            : ForkPointGit(args));

        var act = () => BuildGit(git).ReadAsync(_workspace, "test", Branch, BaseBranch,
            new JobChangesOptions(), CancellationToken.None);

        (await act.Should().ThrowAsync<JobChangeUnavailableException>())
            .Which.ReasonCode.Should().Be(JobChangeUnavailableReasons.GitUnavailable);
    }

    [Fact]
    public async Task ReadAsync_BranchAbsentFromThisCheckout_ReportsUnknownRefSoTheCallerCanFallBack()
    {
        var git = new ScriptedGit(args => args[0] == "rev-parse"
            ? JobGitResult.Fail("")
            : ForkPointGit(args));

        var act = () => BuildGit(git).ReadAsync(_workspace, "test", Branch, BaseBranch,
            new JobChangesOptions(), CancellationToken.None);

        (await act.Should().ThrowAsync<JobChangeUnavailableException>())
            .Which.ReasonCode.Should().Be(JobChangeUnavailableReasons.UnknownRef);
    }

    [Fact]
    public async Task Reader_EveryGitVerbItCanReach_IsTheReadOnlySet()
    {
        // The directory it inspects is a runner's live checkout, possibly mid-turn: a checkout, clean,
        // add or stash issued from a control-plane read would destroy work in progress with nothing to
        // undo. So this drives every flow the panel has and asserts the exact verb set that came out.
        //
        // Asserting the set rather than the absence of write verbs matters: "nothing here is a write"
        // also passes for a reader that ran nothing at all, which is how a regression that silently
        // stops producing diffs would keep this test green.
        var git = new ScriptedGit(ForkPointGit);
        var source = BuildGit(git);
        var options = new JobChangesOptions();

        await source.ReadAsync(_workspace, "test", Branch, BaseBranch, options, CancellationToken.None);
        await source.ReadPatchAsync(_workspace, "test", Branch, BaseBranch, null, null, false, 3,
            null, null, 4096, CancellationToken.None);
        await source.ReadPatchAsync(_workspace, "test", Branch, BaseBranch, "src/keep.txt", null, false, 3,
            null, null, 4096, CancellationToken.None);
        await source.ReadPatchAsync(_workspace, "test", Branch, BaseBranch, "src/fresh.txt", null, true, 3,
            null, null, 4096, CancellationToken.None);

        git.Invocations.Should().BeGreaterThan(0, "a reader that ran nothing would pass an absence test vacuously");
        git.Calls.Select(c => c[0]).Distinct().Should()
            .BeEquivalentTo(new[] { "diff", "log", "merge-base", "rev-parse", "status" },
                "a new verb here means the change reader can now mutate a runner's live checkout — a decision to take deliberately, not to discover in production");
    }

    [Fact]
    public async Task ReadPatchAsync_PathThatCouldEscapeTheCheckout_IsRefusedBeforeAnyProcessStarts()
    {
        // A pathspec is still an argument to a process running inside someone's repository, and git
        // resolves pathspecs from the repo root: "../outside.txt" would match tracked files outside the
        // intended directory and report another job's work as this job's change set.
        var git = new ScriptedGit(ForkPointGit);

        var act = async () => await BuildGit(git).ReadPatchAsync(
            _workspace, "test", Branch, BaseBranch, path: "../outside.txt", previousPath: null,
            untracked: false, contextLines: 3, baseSha: null, headSha: null,
            maxPatchBytes: 4096, ct: CancellationToken.None);

        (await act.Should().ThrowAsync<JobChangeUnavailableException>())
            .Which.ReasonCode.Should().Be(JobChangeUnavailableReasons.PathNotInChangeSet);

        git.Calls.Should().BeEmpty(
            "the pathspec is refused before anything spawns, so a malformed request cannot even pay for a fork-point resolution");
    }

    [Fact]
    public async Task ReadPatchAsync_BinaryFile_ReturnsNoBodyButSaysThereIsNoTextualDiff()
    {
        var git = new ScriptedGit(args => args[0] == "diff"
            ? new JobGitResult(1, "diff --git a/a.png b/a.png\nindex 111..222 100644\nBinary files a/a.png and b/a.png differ\n", "")
            : ForkPointGit(args));

        var patch = await BuildGit(git).ReadPatchAsync(_workspace, "test", Branch, BaseBranch,
            "a.png", null, false, 3, null, null, 4096, CancellationToken.None);

        patch.Binary.Should().BeTrue();
        patch.Patch.Should().BeEmpty("there is no textual patch to render, and git's own binary marker is not one");
        patch.Empty.Should().BeTrue();
    }

    [Fact]
    public async Task ReadPatchAsync_RenamedFile_ReportsThePreimageItWasGiven()
    {
        var git = new ScriptedGit(ForkPointGit);

        var patch = await BuildGit(git).ReadPatchAsync(_workspace, "test", Branch, BaseBranch,
            "src/renamed-big.txt", "src/big.txt", false, 3, null, null, 4096, CancellationToken.None);

        patch.Path.Should().Be("src/renamed-big.txt");
        patch.PreviousPath.Should().Be("src/big.txt");
    }

    [Fact]
    public async Task ReadPatchAsync_RequestedContextIsForwardedAsGitWouldReadIt()
    {
        var git = new ScriptedGit(ForkPointGit);

        await BuildGit(git).ReadPatchAsync(_workspace, "test", Branch, BaseBranch,
            "src/keep.txt", null, false, 0, null, null, 4096, CancellationToken.None);

        git.Calls.Last(c => c[0] == "diff").Should().Contain("-U0",
            "zero context is a real request (a hunks-only diff) and must not silently become git's default");
    }

    // ── the checkpoint as the last resort ───────────────────────────────────────

    [Fact]
    public async Task GetChangeSet_LastResort_IsTheCheckpoint_AndItOffersNoPatches()
    {
        // The checkpoint is the answer that used to be the whole feature ("modified files"), and it is
        // genuinely better than nothing — but it stores names, not content. Offering a patch for it
        // would return an empty body a reader would interpret as "the file did not really change".
        using var db = NewDb(
            checkpointFilesJson: System.Text.Json.JsonSerializer.Serialize(new[] { "src/a.txt", "src/b.txt" }),
            checkpointAt: new DateTimeOffset(2026, 11, 9, 22, 0, 0, TimeSpan.Zero));

        var service = Build(new ScriptedGit(_ => JobGitResult.Fail("fatal: bad revision")),
            new StubPullRequests(FailCode: JobChangeUnavailableReasons.RemoteUnavailable, FailMessage: "no credentials"),
            db: db);

        var dto = await service.GetChangeSetAsync(JobId, ct: CancellationToken.None);

        dto!.Source.Kind.Should().Be(JobChangesSources.Checkpoint);
        dto.Source.Degraded.Should().BeTrue();
        dto.Stats.FilesChanged.Should().Be(2);
        dto.Stats.Additions.Should().Be(0, "a checkpoint carries no line counts and must not claim any");
        dto.Files.Should().OnlyContain(f => f.PatchAvailable == false);
        dto.Notes.Should().Contain(n => n.Contains("no diff behind it"));
        dto.GeneratedAt.Should().Be(new DateTimeOffset(2026, 11, 9, 22, 0, 0, TimeSpan.Zero),
            "the answer is as old as the checkpoint, and the panel says so rather than implying it is live");
    }

    [Fact]
    public async Task GetChangeSet_CheckpointPastTheWritersCap_ReportsTheOmissionRatherThanListingTheMarker()
    {
        // Round-tripped through the repository's own serializer: a parser keyed to the marker's prose
        // starts listing a file named "… 3 more (truncated; count is exact)" the day somebody rewords it.
        var many = Enumerable.Range(0, WorkspaceStateRepository.MaxPersistedModifiedFiles + 3)
            .Select(i => $"src/gen/File{i:000}.cs")
            .ToList();

        using var db = NewDb(checkpointFilesJson: WorkspaceStateRepository.SerializeModifiedFiles(many));

        var service = Build(new ScriptedGit(_ => JobGitResult.Fail("nope")),
            new StubPullRequests(FailCode: JobChangeUnavailableReasons.RemoteFailed, FailMessage: "not pushed"),
            db: db);

        var dto = await service.GetChangeSetAsync(JobId, ct: CancellationToken.None);

        dto!.Source.Kind.Should().Be(JobChangesSources.Checkpoint);
        dto.Stats.FilesChanged.Should().Be(many.Count, "the count is exact even though the stored list is capped");
        dto.Stats.OmittedFiles.Should().Be(3);
        dto.Files.Should().NotContain(f => f.Path.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetChangeSet_UnsafePathStoredInACheckpoint_IsNotEscapedIntoTheResponse()
    {
        // The checkpoint column is written by a runner, and its contents flow into a list the UI
        // renders. A path with parent segments is dropped rather than normalized into something that
        // looks innocuous.
        using var db = NewDb(checkpointFilesJson:
            System.Text.Json.JsonSerializer.Serialize(new[] { "src/ok.txt", "a/../../etc/passwd" }));

        var service = Build(new ScriptedGit(_ => JobGitResult.Fail("nope")),
            new StubPullRequests(FailCode: JobChangeUnavailableReasons.RemoteFailed, FailMessage: "not pushed"),
            db: db);

        var dto = await service.GetChangeSetAsync(JobId, ct: CancellationToken.None);

        dto!.Files.Should().ContainSingle(f => f.Path == "src/ok.txt");
    }

    [Fact]
    public async Task GetChangeSet_LimitIsClampedAndStillReportsTheTrueTotals()
    {
        using var db = NewDb(checkpointFilesJson: System.Text.Json.JsonSerializer.Serialize(
            Enumerable.Range(0, 12).Select(i => $"src/f{i:00}.cs").ToList()));

        var service = Build(new ScriptedGit(_ => JobGitResult.Fail("nope")),
            new StubPullRequests(FailCode: JobChangeUnavailableReasons.RemoteFailed, FailMessage: "not pushed"),
            db: db);

        var dto = await service.GetChangeSetAsync(JobId, fileLimit: 5, ct: CancellationToken.None);

        dto!.Files.Should().HaveCount(5, "the caller asked for five");
        dto.Stats.FilesChanged.Should().Be(12, "the total is over every path, capped list or not");
        dto.Stats.OmittedFiles.Should().Be(7,
            "otherwise the panel's 'showing 5 of N' line has no N to show");
    }

    // ── fixtures ────────────────────────────────────────────────────────────────

    /// <summary>
    /// One scripted git answer for a branch with one committed change, one commit, and a clean tree.
    /// Payloads are the byte layout of the real <c>-z</c> plumbing output.
    /// </summary>
    private static JobGitResult ForkPointGit(IReadOnlyList<string> args) => args[0] switch
    {
        "rev-parse" when args.Count > 3 && args[3].StartsWith(Branch, StringComparison.Ordinal) => JobGitResult.Ok(HeadSha),
        "rev-parse" => JobGitResult.Ok(BaseSha),
        "merge-base" => JobGitResult.Ok(BaseSha),
        "diff" when args.Contains("--name-status") => JobGitResult.Ok("M\0src/keep.txt\0", exitCode: 1),
        "diff" when args.Contains("--numstat") => JobGitResult.Ok("4\t1\tsrc/keep.txt\0", exitCode: 1),
        "diff" => JobGitResult.Ok(
            "diff --git a/src/keep.txt b/src/keep.txt\nindex aaa..bbb 100644\n--- a/src/keep.txt\n+++ b/src/keep.txt\n@@ -1,3 +1,4 @@\n a\n-b\n+B changed\n+c\n+d\n", exitCode: 1),
        "status" => JobGitResult.Ok(""),
        "log" => JobGitResult.Ok(HeadSha + "\u001f2222222\u001f1794280318\u001fRook\u001frook@x\u001fwork: change stuff\u001e"),
        _ => JobGitResult.Fail("unexpected git command: " + string.Join(' ', args)),
    };

    /// <summary>A GitHub compare payload for the same branch: one commit, one modified file.</summary>
    private static string Comparison(int behindBy = 0) =>
        """
        {
          "merge_base_commit": { "sha": "BASESHA" },
          "status": "ahead",
          "ahead_by": 2,
          "behind_by": BEHIND,
          "total_commits": 2,
          "commits": [
            { "sha": "2222222222222222222222222222222222222222",
              "commit": { "author": { "name": "Rook", "email": "rook@x", "date": "2026-11-09T22:00:00Z" },
                          "message": "work: change stuff" } }
          ],
          "files": [
            { "filename": "src/keep.txt", "status": "modified", "additions": 4, "deletions": 1,
              "changes": 5, "patch": "@@ -1,3 +1,4 @@\n a\n-b\n+B changed\n+c\n+d" }
          ]
        }
        """.Replace("BASESHA", BaseSha)
            .Replace("BEHIND", behindBy.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private JobChangesService Build(
        IJobGitCommandRunner git,
        IGitHubPullRequestService remote,
        IJobWorkspaceLocator? locator = null,
        IJobChangesCache? cache = null,
        AgentMeshDbContext? db = null)
    {
        var context = db ?? NewDb();

        return new JobChangesService(
            context,
            locator ?? new FixedLocator(_workspace),
            new JobChangesGitSource(git, NullLogger<JobChangesGitSource>.Instance),
            remote,
            cache ?? new JobChangesCache(),
            logger: NullLogger<JobChangesService>.Instance);
    }

    private JobChangesGitSource BuildGit(IJobGitCommandRunner git) =>
        new(git, NullLogger<JobChangesGitSource>.Instance);

    /// <summary>
    /// A fresh in-memory context, with the row contents one scenario needs.
    /// </summary>
    /// <remarks>
    /// A unique store name per context: the EF InMemory provider enforces no isolation and xUnit runs
    /// classes in parallel, so two tests writing the same job id into one shared store would make each
    /// other's assertions order-dependent.
    /// </remarks>
    private AgentMeshDbContext NewDb(
        string? branch = Branch,
        string? checkpointFilesJson = null,
        DateTimeOffset? checkpointAt = null)
    {
        var db = TestDbContextFactory.Create(nameof(JobChangesReaderTests) + "-" + Guid.NewGuid().ToString("N"));

        db.Projects.Add(new Project
        {
            Id = ProjectId,
            Name = "Reader test",
            GitBaseBranch = BaseBranch,
            // A name that cannot resolve to a real conventional checkout, so the only candidate this
            // fixture can offer is the one the locator is explicitly told about.
            RepositoryName = "does-not-exist-" + Guid.NewGuid().ToString("N"),
        });

        db.ProjectJobs.Add(new ProjectJob
        {
            Id = JobId,
            ProjectId = ProjectId,
            GitBranchName = branch ?? string.Empty,
            WorkingDirectory = _workspace,
        });

        if (checkpointFilesJson != null)
        {
            db.JobWorkspaceStates.Add(new JobWorkspaceState
            {
                JobId = JobId,
                ProjectId = ProjectId,
                CheckpointBranch = branch ?? Branch,
                CheckpointModifiedFilesJson = checkpointFilesJson,
                // Stamping the timestamp is what makes HasCheckpoint true — the property keys on it
                // alone, because the writer always stamps it. Defaulting it to null would mean "a
                // checkpoint exists but was never written", and the reader would correctly skip the
                // source, leaving the scenario asserting against a fallback that never ran.
                CheckpointUpdatedAt = checkpointAt ?? DateTimeOffset.UtcNow,
                UpdatedAt = checkpointAt ?? DateTimeOffset.UtcNow,
            });
        }

        db.SaveChanges();
        return db;
    }

    /// <summary>Offers one directory, so the fallback order is what a real checkout would produce.</summary>
    private sealed class FixedLocator(string root) : IJobWorkspaceLocator
    {
        public IReadOnlyList<JobWorkspaceCandidate> Candidates(ProjectJob job, Project? project) =>
            [new JobWorkspaceCandidate(root, "testFixture")];
    }

    /// <summary>Models the Kubernetes topology, where the API cannot see a runner pod's filesystem.</summary>
    private sealed class EmptyLocator : IJobWorkspaceLocator
    {
        public IReadOnlyList<JobWorkspaceCandidate> Candidates(ProjectJob job, Project? project) =>
            Array.Empty<JobWorkspaceCandidate>();
    }

    /// <summary>Replays one canned answer per invocation while recording every argument vector.</summary>
    private sealed class ScriptedGit(Func<IReadOnlyList<string>, JobGitResult> responder) : IJobGitCommandRunner
    {
        private readonly List<string[]> _calls = new();

        public IReadOnlyList<string[]> Calls => _calls;
        public int Invocations { get; private set; }

        public Task<JobGitResult> RunAsync(
            string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            _calls.Add(arguments.ToArray());
            Invocations++;
            return Task.FromResult(responder(arguments));
        }
    }

    /// <summary>
    /// Stands in for GitHub's comparison reader.
    /// </summary>
    /// <remarks>
    /// Push and merge throw rather than return a value: the change reader must never be able to reach
    /// them, and a test that routed a push through this path should fail loudly rather than discover it
    /// in a code review of a merged branch.
    /// </remarks>
    private sealed class StubPullRequests(
        string? Ok = null,
        string? FailCode = null,
        string? FailMessage = null) : IGitHubPullRequestService
    {
        public int CallCount { get; private set; }

        public Task<GitHubPullRequestService.JobChangeRemoteRead> ReadBranchComparisonAsync(
            string headRef, string? baseRef, string? repositoryOverride = null, CancellationToken ct = default)
        {
            CallCount++;

            if (Ok is null)
            {
                return Task.FromResult(GitHubPullRequestService.JobChangeRemoteRead.Fail(
                    FailCode ?? JobChangeUnavailableReasons.RemoteUnavailable,
                    FailMessage ?? "stub refuses"));
            }

            return Task.FromResult(GitHubPullRequestService.JobChangeRemoteRead.Ok(RemoteSet(Ok), Ok));
        }

        private JobChangeSetDto RemoteSet(string payload)
        {
            // The files and commits are parsed by the production payload reader, so a shape change
            // that breaks it is caught here rather than only by a panel that renders an empty tab.
            // Files and commits go through the production payload reader, so a shape change that
            // breaks it fails here rather than only in a panel that renders an empty tab.
            var (files, _, _) = JobChangeMerge.Join(
                GitChangesParser.ReadGitHubFiles(payload), Array.Empty<ChangedFileEntry>());
            var commits = GitChangesParser.ReadGitHubCommits(payload);
            var notes = GitChangesParser.ReadComparisonNotes(payload).ToList();

            return new JobChangeSetDto(
                JobId, Branch, BaseBranch,
                HeadSha, GitChangesParser.ReadGitHubMergeBase(payload),
                new JobChangeStats(files.Count, 4, 1),
                files,
                new JobChangeSourceInfo(JobChangesSources.GitHubCompare, null, "stubbed remote comparison",
                    CommittedOnly: true, Repository: "octo/repo"),
                commits,
                Notes: notes.Count > 0 ? notes : null);
        }

        public Task<GitHubPullRequestResult> PushBranchAndCreatePrAsync(
            GitHubPullRequestRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("The change reader must never push.");

        public Task<GitHubMergeResult> MergePullRequestAsync(
            GitHubMergeRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("The change reader must never merge.");
    }
}
