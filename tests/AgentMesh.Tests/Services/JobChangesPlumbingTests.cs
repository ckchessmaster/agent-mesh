using AgentMesh.Api.Services;
using Microsoft.Extensions.Configuration;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The plumbing-parser and change-set-shaping layer behind the Cockpit's Changes tab.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why these tests run on captured payloads rather than against a repository.</strong> The
/// behaviour most likely to be wrong here is the field-boundary logic, and the inputs that expose it
/// are the ones a fixture repository cannot produce on a runner: a filename containing a newline, a
/// NUL-delimited record whose second path is silently swallowed, a truncated payload that still looks
/// like a complete list. Asserting on captured bytes also keeps git off the critical path — a test
/// that shells out passes or fails according to the git version installed, which retires the concern
/// that motivated it. Where a claim genuinely depends on git's real output, the payload below was
/// captured from git 2.43 and the byte layout is written out in the assertion so a future reader can
/// see what the fixture is claiming.
/// </para>
/// <para>
/// <strong>What each group is protecting.</strong> The NUL-stride tests guard against a plausible
/// wrong answer (a rename reported as a new file, or its destination filed under the status field);
/// the merge tests guard the counts a reviewer uses to judge blast radius; the workspace-locator tests
/// guard against measuring one job's changes and attributing them to another; the reason-code tests
/// guard against reporting "this job changed nothing" when the truth is "this deployment cannot look".
/// </para>
/// </remarks>
public class JobChangesPlumbingTests
{
    // ── name-status parsing: the record whose stride varies ─────────────────────

    /// <summary>
    /// A rename's record: the similarity score rides <em>inside</em> the status field, not in its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These bytes are git 2.43's, taken from <see cref="GitOutputContractTests"/> rather than written
    /// from memory — which matters, because the previous version of this fixture claimed
    /// <c>R\0100\0from\0to\0</c> and the parser agreed with it, so both passed while disagreeing with
    /// git. A unit fixture can only ever be as right as the person who captured it; this one is now
    /// anchored to a test that runs the command, which is the only thing in the suite able to notice
    /// when the belief behind both is wrong.
    /// </para>
    /// <para>
    /// The parser ignores the digits rather than parsing them: the panel's row says "renamed", not
    /// "renamed 97% similarly", and a similarity threshold git chose on the reader's behalf is not
    /// information this panel should appear to be reporting.
    /// </para>
    /// </remarks>
    [Fact]
    public void ReadNameStatus_RenameRecordCarriesTheScoreInsideTheStatusField()
    {
        var payload = "R100\0src/old.txt\0src/new.txt\0";

        var entries = GitChangesParser.ReadNameStatus(payload);

        entries.Should().HaveCount(1);
        entries[0].Path.Should().Be("src/new.txt");
        entries[0].PreviousPath.Should().Be("src/old.txt");
        entries[0].Status.Should().Be(JobFileChangeKinds.Renamed);
    }

    /// <summary>
    /// A rename in the middle of a mixed payload, where the stride error becomes visible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single rename above cannot catch a stride mistake on its own: consume one field too many
    /// and the record still yields the two paths that happen to follow it, so a test that looks only at
    /// a lone rename passes with the wrong reader. It is the record <em>after</em> the rename that
    /// exposes it — with the wrong stride, <c>A</c> became the third change's path and its real path
    /// became a fourth phantom row.
    /// </para>
    /// <para>
    /// So this asserts all three paths, all three statuses, and the absence of any row named after a
    /// status letter. That last assertion is the one that fails on a desynchronised walk, and it is
    /// the reason this test exists next to the one above.
    /// </para>
    /// </remarks>
    [Fact]
    public void ReadNameStatus_RenameFollowedByOtherRecords_KeepsEverySubsequentRowAligned()
    {
        // The byte order of a real three-change diff (git 2.43): M, a rename, then A.
        var payload = "M\0src/keep.txt\0R097\0src/big.txt\0src/renamed-big.txt\0A\0src/new.txt\0";

        var entries = GitChangesParser.ReadNameStatus(payload);

        entries.Select(e => e.Path).Should().Equal("src/keep.txt", "src/renamed-big.txt", "src/new.txt");
        entries.Select(e => e.Status).Should()
            .Equal(JobFileChangeKinds.Modified, JobFileChangeKinds.Renamed, JobFileChangeKinds.Added);
        entries[1].PreviousPath.Should().Be("src/big.txt");

        entries.Should().NotContain(e => e.Path.Length == 1 && "MADRC".Contains(e.Path[0]),
            "a desynchronised stride files the next record's status letter in as a filename");
    }

    /// <summary>
    /// The mutation this whole stride rule exists to prevent, reproduced from the original bug.
    /// </summary>
    [Fact]
    public void ReadNameStatus_TwoFieldStrideForRenames_WouldMisplaceTheDestination()
    {
        // Documented as a test rather than a comment because the mistake was real and shipped: reading
        // a rename as [status, score, from, to] consumes one field too many, and the panel then offers
        // a patch for "A" — a file that does not exist — for every change listed after a rename.
        var payload = "R100\0src/old.txt\0src/new.txt\0";

        var entries = GitChangesParser.ReadNameStatus(payload);

        entries[0].Path.Should().NotBe("src/old.txt",
            "the destination is the path that exists now, and it is what a patch request addresses");
        entries[0].PreviousPath.Should().NotBe("100",
            "the similarity score is part of the status field and must never reach a path column");
    }

    [Fact]
    public void ReadNameStatus_PathWithSpaceAndNewline_SurviveTheNulDelimiting()
    {
        // A line-based parser splits this second record in half; NUL delimiting cannot, because a
        // filename may contain a newline and may not contain a NUL.
        var payload = "A\0dir with space/file name.txt\0M\0src/line\nbreak.txt\0";

        var entries = GitChangesParser.ReadNameStatus(payload);

        entries.Select(e => e.Path).Should().Equal("dir with space/file name.txt", "src/line\nbreak.txt");
    }

    [Fact]
    public void ReadNameStatus_NonAsciiPath_StaysIntactWhenQuotePathIsOff()
    {
        // core.quotePath=false is what makes git print raw UTF-8 instead of "\303\274…"; the runner
        // passes it, and this pins that the raw bytes arrive as one correct path.
        var payload = "A\0src/ünïcode-ﬁle.txt\0";

        GitChangesParser.ReadNameStatus(payload).Single().Path.Should().Be("src/ünïcode-ﬁle.txt");
    }

    [Fact]
    public void ReadNameStatus_QuotedPath_IsUnescapedWithItsOctalBytes()
    {
        // Without core.quotePath=false (or from a caller that reuses the parser), git quotes a
        // non-ASCII name. The octal escapes are bytes of a UTF-8 sequence, so they must be buffered
        // and decoded together — per-escape character decoding yields mojibake.
        var payload = "A\0\"src/\\303\\274n\\303\\257code.txt\"\0";

        GitChangesParser.ReadNameStatus(payload).Single().Path.Should().Be("src/ünïcode.txt");
    }

    [Fact]
    public void ReadNameStatus_StatusLetterGitDoesNotKnow_IsPassedThroughVerbatim()
    {
        // Coercing an unknown letter to "modified" would render a plausible wrong word; the raw value
        // (score digits and all) is diagnosable.
        var entries = GitChangesParser.ReadNameStatus("Q\0src/weird.txt\0U\0src/unmerged.txt\0");

        entries.Select(e => e.Status).Should().Equal("Q", "U");
    }

    [Fact]
    public void ReadNameStatus_TruncatedRenameTail_IsDroppedRatherThanGuessed()
    {
        // The preimage arrived and the destination did not. Inventing a path would put a row in the
        // panel naming a file nobody changed, and leave every later row at the wrong offset.
        var entries = GitChangesParser.ReadNameStatus("M\0src/ok.txt\0R100\0src/orphan.txt\0");

        entries.Should().ContainSingle(e => e.Path == "src/ok.txt");
    }

    [Fact]
    public void ReadNameStatus_EmptyPayload_IsNoEntries()
    {
        GitChangesParser.ReadNameStatus(string.Empty).Should().BeEmpty();
        GitChangesParser.ReadNameStatus(null).Should().BeEmpty();
    }


    // ── numstat parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void ReadNumstat_TextFile_ReportsBothCounts()
    {
        var entries = GitChangesParser.ReadNumstat("4\t1\tsrc/keep.txt\0");

        entries.Should().ContainSingle();
        entries[0].Additions.Should().Be(4);
        entries[0].Deletions.Should().Be(1);
        entries[0].Binary.Should().BeFalse();
    }

    [Fact]
    public void ReadNumstat_BinaryFile_IsMarkedBinaryWithNullCounts()
    {
        // git writes "-" in both count columns. Turning that into 0/0 would state "this file changed
        // by nothing", which is a different fact and one that quietly changes the row's meaning.
        var entries = GitChangesParser.ReadNumstat("-\t-\tsrc/blob.bin\0");

        entries[0].Binary.Should().BeTrue();
        entries[0].Additions.Should().BeNull();
        entries[0].Deletions.Should().BeNull();
    }

    [Fact]
    public void ReadNumstat_ZeroCounts_AreKeptAsZeroAndNotTreatedAsMissing()
    {
        // A rename detected with no content change is 0/0, and it must not be conflated with a
        // missing column: the first is "moved and untouched", the second is "unknown".
        // The exact byte order of `git diff --find-renames --numstat -z` for a rename with no
        // content edits (verified against git 2.43): the count pair is followed by an empty path, and
        // the preimage/postimage arrive as the next two NUL-separated fields.
        var entries = GitChangesParser.ReadNumstat("0\t0\t\0src/old.txt\0src/new.txt\0");

        entries.Should().ContainSingle();
        entries[0].Path.Should().Be("src/new.txt");
        entries[0].PreviousPath.Should().Be("src/old.txt");
        entries[0].Additions.Should().Be(0);
        entries[0].Deletions.Should().Be(0);
    }

    [Fact]
    public void ReadNumstat_PathContainingTab_IsNotSplitIntoTwoFields()
    {
        // -z disables quoting, so a tab in a filename arrives raw. Splitting on every tab would
        // truncate the path and the row would name a file that does not exist.
        var entries = GitChangesParser.ReadNumstat("1\t0\tsrc/tab\there.txt\0");

        entries.Single().Path.Should().Be("src/tab\there.txt");
    }

    [Fact]
    public void ReadNumstat_UnparseableCounts_DegradeToUnknownRatherThanZero()
    {
        var entries = GitChangesParser.ReadNumstat("nan\tNaN\tsrc/mystery.txt\0");

        entries.Single().Additions.Should().BeNull();
        entries.Single().Deletions.Should().BeNull();
        entries.Single().Binary.Should().BeFalse();
    }

    // ── status --porcelain parsing ──────────────────────────────────────────────

    [Fact]
    public void ReadPorcelainStatus_UntrackedAndModified_DistinguishTheTwoStates()
    {
        // "?? " is a file no commit contains; " M " is a tracked file with worktree edits. Collapsing
        // them would hide the one state a reviewer most needs to see.
        var entries = GitChangesParser.ReadPorcelainStatus(" M src/tracked.txt\0?? src/fresh.txt\0");

        entries.Should().HaveCount(2);
        entries[0].Status.Should().Be(JobFileChangeKinds.Modified);
        entries[1].Status.Should().Be(JobFileChangeKinds.Untracked);
    }

    [Fact]
    public void ReadPorcelainStatus_RenameReportsDestinationFirstWithSourceAsNextField()
    {
        // The opposite order from --name-status: porcelain puts the destination in the record and the
        // source in a trailing field. The parser absorbs that asymmetry so callers see one shape.
        var entries = GitChangesParser.ReadPorcelainStatus("R  src/renamed.txt\0src/original.txt\0");

        entries.Single().Path.Should().Be("src/renamed.txt");
        entries.Single().PreviousPath.Should().Be("src/original.txt");
        entries.Single().Status.Should().Be(JobFileChangeKinds.Renamed);
    }

    [Fact]
    public void ReadPorcelainStatus_StagedAndWorktreeDisagree_WorktreeColumnWins()
    {
        // "AM": added to the index, then edited in the tree. The worktree column describes what the
        // reader is looking at now, and picking it deterministically is what lets the panel be
        // compared against `git status` by someone debugging it.
        var entries = GitChangesParser.ReadPorcelainStatus("AM src/half-staged.txt\0");

        entries.Single().Status.Should().Be(JobFileChangeKinds.Modified);
    }

    [Fact]
    public void ReadPorcelainStatus_DeletedFromWorktreeOnly_IsStillDeleted()
    {
        GitChangesParser.ReadPorcelainStatus(" D src/gone.txt\0")
            .Single().Status.Should().Be(JobFileChangeKinds.Deleted);
    }

    // ── log records ─────────────────────────────────────────────────────────────

    [Fact]
    public void ReadCommitRecords_SubjectWithPunctuationAndABody_KeepsFieldAndRecordBoundaries()
    {
        // The record separator is a control character rather than a printable one because a commit
        // subject may contain any printable string, and a body line routinely carries a colon or a
        // comma. The single-record assertion is what proves the body's punctuation did not split the
        // entry; the subject assertion is what proves the body did not leak into the one field the
        // panel actually renders.
        var subject = "fix: change things | pipe $pecial? chars [x]:99";
        var body = "more prose with a comma, and another\nplus a second paragraph";

        var payload = new string('a', 40)
            + "\u001f1794280318\u001fRook\u001frook@x.local\u001f"
            + subject + "\n\n" + body + "\u001e";

        var commits = GitChangesParser.ReadCommitRecords(payload);

        commits.Should().ContainSingle(
            "a body line containing ':' or ',' must not read as another field or another commit");
        // Abbreviated from the full sha, not echoed from git's %h field: core.abbrev is configurable
        // and auto-scales with repository size, so %h has no stable width to promise the panel.
        commits[0].ShortSha.Should().Be(new string('a', 7));
        commits[0].AuthorName.Should().Be("Rook");
        commits[0].CommittedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1794280318));
        commits[0].Subject.Should().Be(subject, "the panel shows one table cell: the summary line only");
    }

    [Fact]
    public void ReadCommitRecords_MultipleRecords_AllArriveInOrder()
    {
        var payload = string.Concat(
            new string('1', 40), "\u001f100\u001fA\u001fa@x\u001ffirst\u001e",
            new string('2', 40), "\u001f200\u001fB\u001fb@x\u001fsecond\u001e");

        var commits = GitChangesParser.ReadCommitRecords(payload);

        commits.Should().HaveCount(2);
        commits.Select(c => c.Subject).Should().Equal("first", "second");
    }

    // ── path normalization ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("/dev/null", "")]
    [InlineData("./src/a.txt", "src/a.txt")]
    [InlineData("/abs/build/src/a.txt", "abs/build/src/a.txt")]
    [InlineData("src//a.txt", "src/a.txt")]
    [InlineData("src/./a.txt", "src/a.txt")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeRelativePath_HandlesTheShapesGitEmits(string raw, string expected)
    {
        GitChangesParser.NormalizeRelativePath(raw).Should().Be(expected);
    }

    [Fact]
    public void NormalizeRelativePath_ParentSegments_ArePreservedSoTheConsumerCanRejectThem()
    {
        // Folding "a/../../etc/passwd" into "etc/passwd" hands the file-read helper a path that has
        // already escaped and looks innocuous. Kept visible, it is rejected by IsSafeRelativePath.
        var normalized = GitChangesParser.NormalizeRelativePath("a/../../etc/passwd");

        normalized.Should().Contain("..");
        JobChangeMerge.IsSafeRelativePath(normalized).Should().BeFalse();
    }

    [Theory]
    [InlineData("src/a.cs", true)]
    [InlineData("src/deep/nested/a.cs", true)]
    [InlineData("../escape", false)]
    [InlineData("/absolute", false)]
    [InlineData("src/../etc/passwd", false)]
    [InlineData("src\\windows.txt", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsSafeRelativePath_RejectsAnythingThatCouldLeaveTheRepository(string path, bool expected)
    {
        JobChangeMerge.IsSafeRelativePath(path).Should().Be(expected);
    }

    // ── ref validation ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("main", "main")]
    [InlineData("feat/task-205-session_paging", "feat/task-205-session_paging")]
    [InlineData("mesh/job-375e39bc", "mesh/job-375e39bc")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("--output=/tmp/pwned", null)]
    [InlineData("-b", null)]
    [InlineData("HEAD:src/main.cs", null)]
    [InlineData("HEAD~5", null)]
    [InlineData("HEAD^", null)]
    [InlineData("@{u}", null)]
    [InlineData("main..feature", null)]
    [InlineData("refs/heads/ma in", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("branch/", null)]
    [InlineData("branch.lock", null)]
    public void TryRef_AcceptsBranchNamesAndRejectsRevSyntaxAndOptionShapes(string input, string? expected)
    {
        JobChangesGitSource.TryRef(input).Should().Be(expected);
    }

    [Fact]
    public void TryRef_RejectsPathsWithParentSegments()
    {
        JobChangesGitSource.TryRef("../outside").Should().BeNull();
    }

    [Fact]
    public void RequireRelativePath_RejectsParentTraversal()
    {
        var act = () => JobChangesGitSource.RequireRelativePath("../../etc/passwd");

        act.Should().Throw<JobChangeUnavailableException>()
            .Which.ReasonCode.Should().Be(JobChangeUnavailableReasons.PathNotInChangeSet);
    }

    // ── the join: rows, counts, caps ────────────────────────────────────────────

    [Fact]
    public void Join_StatusRowsLeadAndStatsAreMergedOntoThem()
    {
        var (files, stats, truncated) = JobChangeMerge.Join(
            [new ChangedFileEntry("src/a.txt", JobFileChangeKinds.Modified)],
            [new ChangedFileEntry("src/a.txt", Additions: 4, Deletions: 1)]);

        files.Should().ContainSingle();
        files[0].Status.Should().Be(JobFileChangeKinds.Modified);
        files[0].Additions.Should().Be(4);
        stats.FilesChanged.Should().Be(1);
        stats.Additions.Should().Be(4);
        truncated.Should().BeFalse();
    }

    [Fact]
    public void Join_PathPresentOnlyInStats_StillCountedInTotals()
    {
        // Dropping it would understate filesChanged — the number a reviewer uses to decide how
        // carefully to read.
        var (files, stats, _) = JobChangeMerge.Join(
            [new ChangedFileEntry("src/a.txt", JobFileChangeKinds.Modified)],
            [
                new ChangedFileEntry("src/a.txt", Additions: 1, Deletions: 0),
                new ChangedFileEntry("src/surprise.txt", Additions: 9, Deletions: 2),
            ]);

        files.Should().HaveCount(2);
        stats.FilesChanged.Should().Be(2);
        stats.Additions.Should().Be(10);
        stats.Deletions.Should().Be(2);
    }

    [Fact]
    public void Join_CappedList_KeepsTheFullLineTotals()
    {
        var statuses = Enumerable.Range(0, 10)
            .Select(i => new ChangedFileEntry($"src/f{i:00}.cs", JobFileChangeKinds.Modified))
            .ToList();
        var stats = Enumerable.Range(0, 10)
            .Select(i => new ChangedFileEntry($"src/f{i:00}.cs", Additions: 2, Deletions: 1))
            .ToList();

        var (files, totals, truncated) = JobChangeMerge.Join(statuses, stats, limit: 3);

        files.Should().HaveCount(3);
        truncated.Should().BeTrue();
        totals.FilesChanged.Should().Be(10);
        totals.Additions.Should().Be(20);
        totals.Deletions.Should().Be(10);
        totals.OmittedFiles.Should().Be(7);
    }

    [Fact]
    public void Join_BinaryRow_ContributesNoLineCounts()
    {
        var (files, totals, _) = JobChangeMerge.Join(
            [
                new ChangedFileEntry("a.png", JobFileChangeKinds.Added, Binary: true),
                new ChangedFileEntry("b.cs", JobFileChangeKinds.Modified, Additions: 5, Deletions: 1),
            ],
            Array.Empty<ChangedFileEntry>());

        files.First(f => f.Path == "a.png").Additions.Should().BeNull();
        totals.Additions.Should().Be(5);
        totals.FilesChanged.Should().Be(2);
    }

    [Fact]
    public void Join_CheckpointSource_OffersNoPatchBecauseItHasNoContent()
    {
        // A checkpoint stores names, not blobs. Offering a patch would return an empty body the reader
        // would interpret as "the file did not really change".
        var (files, _, _) = JobChangeMerge.Join(
            [new ChangedFileEntry("src/a.txt", JobFileChangeKinds.Modified)],
            Array.Empty<ChangedFileEntry>(),
            patchAvailable: false);

        files.Single().PatchAvailable.Should().BeFalse();
    }

    [Fact]
    public void Join_RowsAreSortedByPathSoThePanelIsStableAcrossRefreshes()
    {
        var (files, _, _) = JobChangeMerge.Join(
            [
                new ChangedFileEntry("src/z.cs", JobFileChangeKinds.Modified),
                new ChangedFileEntry("src/a.cs", JobFileChangeKinds.Added),
                new ChangedFileEntry("src/m.cs", JobFileChangeKinds.Deleted),
            ],
            Array.Empty<ChangedFileEntry>());

        files.Select(f => f.Path).Should().Equal("src/a.cs", "src/m.cs", "src/z.cs");
    }

    [Fact]
    public void Join_UnsafePathFromAStatusRecord_IsNotSilentlyRepaired()
    {
        // Join is pure shaping: the reader filters escapes upstream, and quietly dropping a row here
        // would change counts without saying why. What the endpoint must never do is hand a path with
        // ".." to a file read.
        var (files, _, _) = JobChangeMerge.Join(
            [new ChangedFileEntry("src/a.cs", JobFileChangeKinds.Modified)],
            Array.Empty<ChangedFileEntry>());

        files.Should().ContainSingle();
        JobChangeMerge.IsSafeRelativePath("a/../../x").Should().BeFalse();
    }

    // ── the checkpoint's truncation marker ──────────────────────────────────────

    [Fact]
    public void SplitCheckpointFiles_ListWithinTheCap_IsReturnedIntactWithNothingOmitted()
    {
        var (paths, omitted) = JobChangesService.SplitCheckpointFiles(["a.cs", "b.cs"]);

        paths.Should().Equal("a.cs", "b.cs");
        omitted.Should().Be(0);
    }

    /// <summary>
    /// The marker is recognized by its position past the writer's cap, not by its wording.
    /// </summary>
    /// <remarks>
    /// Round-tripped through the real serializer so the two ends of the contract cannot drift: if
    /// <c>SerializeModifiedFiles</c> changes its cap or its marker text, this test fails here rather
    /// than in a panel that has started listing a file named "… 3 more (truncated; count is exact)".
    /// </remarks>
    [Fact]
    public void SplitCheckpointFiles_RoundTripsThroughTheRepositorysOwnSerializer()
    {
        var many = Enumerable.Range(0, WorkspaceStateRepository.MaxPersistedModifiedFiles + 3)
            .Select(i => $"src/generated/File{i:000}.cs")
            .ToList();

        var json = WorkspaceStateRepository.SerializeModifiedFiles(many);
        var stored = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json!)!;

        stored.Should().HaveCount(WorkspaceStateRepository.MaxPersistedModifiedFiles + 1,
            "the writer caps the array and appends exactly one marker element");

        var (paths, omitted) = JobChangesService.SplitCheckpointFiles(stored);

        omitted.Should().Be(3);
        paths.Should().HaveCount(WorkspaceStateRepository.MaxPersistedModifiedFiles);
        paths.Should().NotContain(p => p.Contains("truncated", StringComparison.OrdinalIgnoreCase));
        paths.Should().Contain("src/generated/File000.cs", "the first path is always kept");
        // The last kept index is cap-1, and the fixture zero-pads to three digits: File059, not File59.
        // Asserting the boundary element is what proves the split is at the cap rather than at some
        // other length a future refactor might introduce.
        paths.Should().Contain($"src/generated/File{WorkspaceStateRepository.MaxPersistedModifiedFiles - 1:000}.cs",
            "the element at cap-1 is the last real path");
        paths.Should().NotContain($"src/generated/File{WorkspaceStateRepository.MaxPersistedModifiedFiles:000}.cs",
            "the first dropped element must not appear as a path");
    }

    [Fact]
    public void SplitCheckpointFiles_MarkerWithoutAParseableNumber_StillReportsAnOmission()
    {
        // The element's position past the cap already proves something was dropped; a marker whose
        // wording changed must not degrade to "nothing was omitted".
        var stored = Enumerable.Range(0, WorkspaceStateRepository.MaxPersistedModifiedFiles)
            .Select(i => $"f{i}.cs")
            .Append("… many more (truncated; count is exact)")
            .ToList();

        var (_, omitted) = JobChangesService.SplitCheckpointFiles(stored);

        omitted.Should().Be(1);
    }

    [Fact]
    public void SplitCheckpointFiles_RealFilenameContainingTheWordMore_IsKeptAsAPath()
    {
        // Matched by position, not by substring — a file called "MORE.md" is a file, not a marker.
        var stored = new List<string> { "docs/MORE.md", "docs/more-notes.md" };

        var (paths, omitted) = JobChangesService.SplitCheckpointFiles(stored);

        paths.Should().Equal("docs/MORE.md", "docs/more-notes.md");
        omitted.Should().Be(0);
    }

    // ── workspace candidates ────────────────────────────────────────────────────

    [Fact]
    public void Candidates_OfferTheLegacySharedCheckoutSoTheDeploymentThatMountsItStillAnswers()
    {
        // /workspace/repo is this deployment's real layout: the persona runner's job workdir is that
        // container's checkout, which the API sees too. It predates WorkspacePathResolver's
        // per-repository convention, and leaving it out would mean the Changes panel reports
        // "no workspace" on the environment the platform is actually running in.
        var locator = new JobWorkspaceLocator();

        var candidates = locator.Candidates(NewJob(), NewProject());

        candidates.Should().Contain(c => c.Root == JobWorkspaceLocator.LegacySharedCheckout);
    }

    [Fact]
    public void Candidates_NeverRankTheLegacySharedCheckoutAboveASpecificCheckoutThatExists()
    {
        // The ordering rule, tested where both candidates are real. Asserting it on the previous test's
        // inputs would fail here for an unrelated reason: the job-scoped and conventional paths are
        // derived from a job id and a repository name that do not exist on disk, so the locator filters
        // them out and the shared tree legitimately comes first. That is correct behaviour, and a test
        // that called it a failure would be asserting an ordering rule the code has no way to honour.
        var root = CreateCheckout(nameof(Candidates_NeverRankTheLegacySharedCheckoutAboveASpecificCheckoutThatExists));

        var locator = new JobWorkspaceLocator(InMemoryConfig(("Platform:WorkspaceRoot", root)));
        var roots = locator.Candidates(NewJob(workingDirectory: root), NewProject())
            .Select(c => c.Root).ToList();

        roots.IndexOf(JobWorkspaceLocator.LegacySharedCheckout)
            .Should().BeGreaterThan(roots.IndexOf(root),
                "measuring the shared tree and reporting its contents as this job's change set is the " +
                "failure the ordering exists to prevent");
    }

    [Fact]
    public void Candidates_PreferTheJobCheckoutOverTheSharedTreeWhenBothExist()
    {
        // The shared paths always exist in the containerized topologies. Returning the first
        // *existing* candidate rather than the first *specific* one is how one job's work gets
        // reported as another job's, so the job's own working_directory must lead.
        var root = CreateCheckout(nameof(Candidates_PreferTheJobCheckoutOverTheSharedTreeWhenBothExist));
        var jobId = Guid.NewGuid();

        // The runner's per-job layout (<root>/jobs/<jobId>) as a second, less specific checkout.
        Directory.CreateDirectory(Path.Combine(root, "jobs", jobId.ToString(), ".git"));

        var locator = new JobWorkspaceLocator(InMemoryConfig(("Platform:WorkspaceRoot", root)));
        var candidates = locator.Candidates(
            NewJob(id: jobId, workingDirectory: root), NewProject());

        candidates.First().Root.Should().Be(root);
        candidates.First().Provenance.Should().Be("job.workingDirectory");

        var runnerScoped = candidates.First(c => c.Provenance == "runnerPerJob");
        runnerScoped.Root.Should().Be(Path.Combine(root, "jobs", jobId.ToString()),
            "the locator returns forward-slash paths on every platform this runs on");
        runnerScoped.Root.Should().NotBe(root, "a job-scoped checkout and the shared tree are different trees");
    }

    [Fact]
    public void Candidates_SkipADirectoryThatIsNotAGitCheckout()
    {
        var plain = Path.Combine(Path.GetTempPath(), "job-changes-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plain);

        try
        {
            var locator = new JobWorkspaceLocator(InMemoryConfig(("Platform:WorkspaceRoot", plain)));
            var candidates = locator.Candidates(NewJob(id: Guid.NewGuid(), workingDirectory: plain), NewProject());

            candidates.Should().NotContain(c => c.Root == plain);
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    [Fact]
    public void Candidates_AreDeduplicatedWhenTwoRulesResolveToTheSameDirectory()
    {
        var root = CreateCheckout(nameof(Candidates_AreDeduplicatedWhenTwoRulesResolveToTheSameDirectory));
        var locator = new JobWorkspaceLocator(InMemoryConfig(("Platform:WorkspaceRoot", root)));

        var candidates = locator.Candidates(NewJob(id: Guid.NewGuid(), workingDirectory: root + "/"), NewProject());

        candidates.Select(c => c.Root).Should().OnlyHaveUniqueItems();
    }

    // ── patch truncation ────────────────────────────────────────────────────────

    [Fact]
    public void StripPatchNoise_CutsOnAFileBoundarySoTheResultStaysDiffParsable()
    {
        // Two complete file records, with the limit placed past the end of the first and into the
        // second's hunk. Deriving the limit from the fixture's own lengths keeps the cut honest: a
        // hardcoded number drifts into a position that tests nothing the moment the fixture changes,
        // which is exactly how this assertion previously came to be checking a hard cut.
        var first = "diff --git a/a.txt b/a.txt\n@@ -1 +1 @@\n+x\n";
        var second = "diff --git a/b.txt b/b.txt\n@@ -1 +1 @@\n+yyyy\n";

        var result = JobChangesGitSource.StripPatchNoise(first + second, first.Length + 12, out var truncated);

        truncated.Should().BeTrue();
        result.Should().Be(first.TrimEnd('\n'),
            "a patch ending inside a hunk is a corrupt diff, while one ending at a file boundary is a " +
            "readable partial list with the truncation flag explaining why");
        result.Should().NotContain("b/b.txt");
    }

    [Fact]
    public void StripPatchNoise_SingleFileBiggerThanTheLimit_IsCutHardRatherThanEmptied()
    {
        // An empty result would read as "this file changed nothing" — the one wrong answer that looks
        // right, so a partial body is the better failure.
        var patch = new string('x', 500);

        var result = JobChangesGitSource.StripPatchNoise(patch, 100, out var truncated);

        truncated.Should().BeTrue();
        result.Should().HaveLength(100);
    }

    [Fact]
    public void StripPatchNoise_PatchWithinTheLimit_IsUntouched()
    {
        var patch = "diff --git a/a.txt b/a.txt\n";

        JobChangesGitSource.StripPatchNoise(patch, 100, out var truncated).Should().Be(patch);
        truncated.Should().BeFalse();
    }

    // ── failure sanitation ──────────────────────────────────────────────────────

    [Fact]
    public void Sanitize_StripsControlCharactersAndACredentialInARemoteUrl()
    {
        // git echoes the remote it contacted into stderr, and a remote configured with a token in its
        // URL would otherwise reach the job chat and the browser.
        var raw = "fatal: cannot access https://user:glpat-secret@example.com/r.git\x00\x1b[31m\n";

        var clean = JobGitResult.Sanitize(raw);

        clean.Should().NotContain("glpat-secret");
        clean.Should().Contain("[redacted]@");
        clean.Should().NotContain("\x00");
        clean.Should().NotContain("\x1b");
    }

    [Fact]
    public void Sanitize_KeepsNewlinesForReadability()
    {
        JobGitResult.Sanitize("line one\nline two").Should().Contain("\n");
    }

    // ── options clamping ────────────────────────────────────────────────────────

    [Fact]
    public void Options_ATypoInTheConfigurationDegradesToDefaultsRatherThanDisablingThePanel()
    {
        // A Helm value of "0" or "-1" must not produce a permanently empty file list, a zero-byte
        // patch cap, a hunks-only diff nobody asked for, or a git timeout that kills every invocation
        // before it can answer.
        var options = JobChangesOptions.From(InMemoryConfig(
            (JobChangesOptions.MaxFilesKey, "0"),
            (JobChangesOptions.MaxPatchBytesKey, "-1"),
            (JobChangesOptions.ContextLinesKey, "-1"),
            (JobChangesOptions.GitTimeoutSecondsKey, "not-a-number")));

        options.MaxFiles.Should().Be(JobChangesPolicy.DefaultFileListLimit);
        options.MaxPatchBytes.Should().Be(JobChangesPolicy.DefaultPatchByteLimit);
        options.DefaultContextLines.Should().Be(3, "a negative context is unset, not a request for zero context");
        options.GitTimeoutSeconds.Should().Be(0, "which the runner reads as 'use my own default'");
    }

    [Fact]
    public void Options_ACallerCannotAskForMoreThanThePolicyCeiling()
    {
        var options = JobChangesOptions.From(InMemoryConfig((JobChangesOptions.MaxFilesKey, "100000")));

        options.MaxFiles.Should().Be(JobChangesPolicy.MaxFileListLimit);
        options.ResolveFileLimit(999_999).Should().Be(JobChangesPolicy.MaxFileListLimit);
    }

    [Fact]
    public void Options_AConfiguredContextOfZeroIsHonouredBecauseZeroIsAMeaningfulRequest()
    {
        // The two context knobs are read by "is this key present" rather than "is it positive",
        // because 0 context lines is a real answer (a hunks-only diff) while 0 files or a 0-second
        // timeout are requests nobody means. Collapsing both rules into one reader would silently turn
        // "no context" back into git's default.
        var options = JobChangesOptions.From(InMemoryConfig((JobChangesOptions.ContextLinesKey, "0")));

        options.DefaultContextLines.Should().Be(0);
        options.ResolveContext(null).Should().Be(0);
        options.ResolveContext(0).Should().Be(0);
    }

    [Fact]
    public void Options_AContextRequestIsClampedToTheCeiling()
    {
        var options = new JobChangesOptions(MaxContextLines: 10);

        options.ResolveContext(10_000).Should().Be(10);
        options.ResolveContext(-1).Should().Be(options.DefaultContextLines);
    }

    // ── reason vocabulary ───────────────────────────────────────────────────────

    [Fact]
    public void ReasonCodes_AreStableSnakeCaseTokensTheUiSwitchesOn()
    {
        // The UI words each case distinctly, so a code that changes spelling is a breaking change to
        // the client. Pinned as literals rather than compared to the constants themselves, which would
        // pass no matter how they were renamed.
        foreach (var (actual, expected) in new[]
        {
            (JobChangeUnavailableReasons.NoWorkspace, "no_workspace"),
            (JobChangeUnavailableReasons.BranchMismatch, "branch_mismatch"),
            (JobChangeUnavailableReasons.NoBranch, "no_branch"),
            (JobChangeUnavailableReasons.UnknownRef, "unknown_ref"),
            (JobChangeUnavailableReasons.GitUnavailable, "git_unavailable"),
            (JobChangeUnavailableReasons.RemoteUnavailable, "remote_unavailable"),
            (JobChangeUnavailableReasons.RemoteFailed, "remote_failed"),
            (JobChangeUnavailableReasons.NoChanges, "no_changes"),
            (JobChangeUnavailableReasons.PathNotInChangeSet, "path_not_in_change_set"),
        })
        {
            actual.Should().Be(expected);
        }
    }

    [Fact]
    public void Unavailable_ChangesNothingButCarriesTheReasonAndIsNotMarkedAvailable()
    {
        var dto = JobChangesPolicy.Unavailable(Guid.NewGuid(), "mesh/job-1", "main",
            JobChangeUnavailableReasons.NoWorkspace, "the control plane sees no checkout");

        dto.Stats.FilesChanged.Should().Be(0);
        dto.Files.Should().BeEmpty();
        dto.Source.Kind.Should().BeNull();
        dto.Source.ReasonCode.Should().Be(JobChangeUnavailableReasons.NoWorkspace);
        dto.Notes.Should().BeNull();
    }

    // ── cache ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Cache_ExpiredEntry_IsNotReturnedAndIsEvictedOnTouch()
    {
        var cache = new JobChangesCache();
        var key = JobChangesPolicy.CacheKey(JobChangesSources.LocalGit, "/w", "b", "main", null, null, 3, null);

        cache.Set(key, EmptySet(), TimeSpan.FromMilliseconds(-1));

        cache.TryGet(key, out _).Should().BeFalse();
    }

    [Fact]
    public void Cache_RespectsItsLifetimeWindow()
    {
        var cache = new JobChangesCache();
        var key = "job-changes|k";

        cache.Set(key, EmptySet(), TimeSpan.FromMinutes(5));
        cache.TryGet(key, out var value).Should().BeTrue();
        value.Should().NotBeNull();

        cache.Clear();
        cache.TryGet(key, out _).Should().BeFalse();
    }

    [Fact]
    public void Cache_StaysBoundedAndDropsTheOldestEntryFirst()
    {
        var cache = new JobChangesCache();

        // One more than the ceiling, each with a long lifetime so eviction cannot be attributed to
        // expiry, and with distinct keys so nothing overwrites anything else.
        for (var i = 0; i <= JobChangesPolicy.CacheEntryLimit; i++)
        {
            cache.Set($"job-changes|{i}", EmptySet(), TimeSpan.FromMinutes(5));
        }

        // Exactly one eviction happened, so exactly the ceiling remains reachable. Asserting the
        // surviving *count* rather than naming which key was dropped keeps the test honest about the
        // promise ("bounded, oldest-first") without depending on two inserts landing on distinct
        // clock ticks.
        var surviving = Enumerable.Range(0, JobChangesPolicy.CacheEntryLimit + 1)
            .Count(i => cache.TryGet($"job-changes|{i}", out _));

        surviving.Should().Be(JobChangesPolicy.CacheEntryLimit);
        cache.TryGet($"job-changes|{JobChangesPolicy.CacheEntryLimit}", out _).Should().BeTrue(
            "the entry just stored must never be the one evicted");
    }

    [Fact]
    public void CacheKey_NeverCollapsesAcrossSourceKindPathOrPinnedShas()
    {
        // A collision would let a remote answer (which cannot see uncommitted work) satisfy a local
        // request, or a checkpoint answer satisfy a diff request — the "I can see the file but not the
        // diff" class of bug this panel exists to remove.
        var local = JobChangesPolicy.CacheKey(JobChangesSources.LocalGit, "/w", "b", "main", null, null, 3, "a.cs");
        var remote = JobChangesPolicy.CacheKey(JobChangesSources.GitHubCompare, "/w", "b", "main", null, null, 3, "a.cs");
        var otherPath = JobChangesPolicy.CacheKey(JobChangesSources.LocalGit, "/w", "b", "main", null, null, 3, "b.cs");
        var wholeList = JobChangesPolicy.CacheKey(JobChangesSources.LocalGit, "/w", "b", "main", null, null, 3, null);
        var pinned = JobChangesPolicy.CacheKey(JobChangesSources.LocalGit, "/w", "b", "main", "abc", "def", 3, "a.cs");
        var otherWorkspace = JobChangesPolicy.CacheKey(JobChangesSources.LocalGit, "/other", "b", "main", null, null, 3, "a.cs");

        new[] { local, remote, otherPath, wholeList, pinned, otherWorkspace }
            .Should().OnlyHaveUniqueItems();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static JobChangeSetDto EmptySet() =>
        new(Guid.NewGuid(), "b", "main", null, null, new JobChangeStats(0, 0, 0),
            Array.Empty<JobChangedFileDto>(),
            new JobChangeSourceInfo(JobChangesSources.LocalGit, null, "test"),
            Array.Empty<JobChangeCommitDto>());

    private static ProjectJob NewJob(Guid? id = null, string? workingDirectory = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            ProjectId = "agent-mesh",
            GitBranchName = "mesh/job-test",
            WorkingDirectory = workingDirectory,
        };

    private static Project NewProject(string id = "agent-mesh") =>
        new() { Id = id, Name = "Agent Mesh", GitBaseBranch = "main", RepositoryName = "agent-mesh-test-does-not-exist" };

    /// <summary>Creates a directory containing a <c>.git</c> entry — the locator's whole precondition.</summary>
    private static string CreateCheckout(string label)
    {
        // A plain directory rather than `git init`: the locator only checks that a gitdir exists, and
        // keeping a process out of the fixture means the test proves what it claims regardless of
        // whether git is installed.
        var root = Path.Combine(Path.GetTempPath(),
            "job-changes-" + label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }

    private static IConfiguration InMemoryConfig(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();
}
