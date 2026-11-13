using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Worker.Workflows;
using AgentMesh.Tests.Sandbox;
using FluentAssertions;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace AgentMesh.Tests.Workflows;

[Collection(SandboxTestCollections.Temporal)]
public class JobExecutionWorkflowTests
{
    private const string Queue = "job-execution-test-queue";

    private static TemporalWorker BuildWorker(
        WorkflowEnvironment env,
        FakeTimelineActivities timeline,
        FakeExecutionActivities execution,
        FakeGitActivities git)
    {
        var workerOptions = new TemporalWorkerOptions(Queue)
            .AddActivity((Func<CreateTimelineCardInput, Task<JobTimelineCard>>)timeline.CreateCardAsync)
            .AddActivity((Func<UpdateTimelineCardStatusInput, Task>)timeline.UpdateCardStatusAsync)
            .AddActivity((Func<DiscoveryQaActivityInput, Task<DiscoveryQaActivityResult>>)timeline.ExecuteDiscoveryQaActivityAsync)
            .AddActivity((Func<DecompositionActivityInput, Task<DecompositionActivityResult>>)timeline.ExecuteDecompositionActivityAsync)
            .AddActivity((Func<TurnInterruptedCardInput, Task<Guid>>)timeline.RecordTurnInterruptedAsync)
            .AddActivity((Func<TaskStepCardInput, Task<Guid>>)timeline.UpdateTaskStepCardAsync)
            .AddActivity((Func<AppendRemediationTasksInput, Task<IReadOnlyList<ProposedChecklistItem>>>)timeline.AppendRemediationTasksAsync)
            .AddActivity((Func<TaskExecutionInput, ChatMessageSignal?, Task<TurnOutcome>>)execution.RunAgentCodingTurnAsync)
            .AddActivity((Func<MergePullRequestInput, Task<string>>)git.MergeJobPullRequestAsync)
            .AddWorkflow<JobExecutionWorkflow>()
            .AddWorkflow<TaskExecutionWorkflow>();
        workerOptions.StickyQueueScheduleToStartTimeout = TimeSpan.FromSeconds(1);
        return new TemporalWorker(env.Client, workerOptions);
    }

    [Fact]
    public async Task JobWorkflow_WithSevenArgConstructor_RunsTimelineLoop_OpensSingleJobLevelPr_AndMergesWithApprovedSha()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities();
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();

        using var worker = BuildWorker(env, timeline, execution, git);
        using var cts = new CancellationTokenSource();
        var workerTask = worker.ExecuteAsync(cts.Token);

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var input = new JobExecutionInput(
                JobId: jobId,
                AssignedAgentId: "driver-implementer",
                ProjectId: "mesh-core",
                WorkspacePath: "/workspace/repos/agent-mesh",
                InitialPrompt: "Implement timeline lifecycle",
                TargetBranch: "main",
                ApprovalPolicy: PlanApprovalPolicy.LeadOrHuman
            );

            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(input),
                new WorkflowOptions($"job-{jobId}", Queue));

            // 1. Wait until workflow parks at Discovery Q&A
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("discovery", StringComparison.OrdinalIgnoreCase));
            timeline.DiscoveryQaInputs.Should().HaveCount(1);
            execution.TaskTurnCount.Should().Be(0);

            // 2. Submit Discovery Q&A answers
            var answers = new Dictionary<string, string> { ["q1"] = "A" };
            await handle.SignalAsync(wf => wf.SubmitDiscoveryAnswersAsync(
                new DiscoveryAnswersSignal(timeline.DiscoveryQaCardId, answers, null)));

            // 3. Wait until workflow parks at Plan Approval
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
            timeline.DecompositionInputs.Should().HaveCount(1);
            execution.TaskTurnCount.Should().Be(0);

            // 4. Approve plan -> all 3 tasks run without per-task PRs -> Stage 4 opens single job-level PR
            await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(
                new PlanApprovedSignal("astra", "tech_lead", "Plan looks great. Proceed.", DateTimeOffset.UtcNow)));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));
            execution.TaskTurnCount.Should().Be(timeline.CustomTasks.Count, "each checklist task ran once without per-task PRs");
            execution.PrStageTurnCount.Should().Be(1, "exactly one Stage 4 job-level PR turn ran after all tasks finished");
            git.MergeCalls.Should().BeEmpty("nothing merges before external PR approval");

            // 5. Independent reviewer approves -> MergeJobPullRequest receives exact approved HEAD SHA
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("astra", IsApproved: true, "Clean diff. Ship it.", DateTimeOffset.UtcNow, PrNumber: 101)));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            git.MergeCalls.Should().ContainSingle();
            git.MergeCalls[0].PrUrl.Should().Be("https://github.com/ckchessmaster/agent-mesh/pull/101");
            git.MergeCalls[0].ApprovedHeadSha.Should().Be("sha-round-0", "approved HEAD SHA must be passed into MergeJobPullRequest");
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task JobWorkflow_RespectsHumanOnlyApprovalPolicy_OnReceivePlanApprovalSignal()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities();
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();

        using var worker = BuildWorker(env, timeline, execution, git);
        using var cts = new CancellationTokenSource();
        var workerTask = worker.ExecuteAsync(cts.Token);

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var input = new JobExecutionInput(
                JobId: jobId,
                AssignedAgentId: "driver-implementer",
                ProjectId: "mesh-core",
                WorkspacePath: "/workspace",
                InitialPrompt: "Implement mission-critical feature",
                TargetBranch: "main",
                ApprovalPolicy: PlanApprovalPolicy.HumanOnly
            );

            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(input),
                new WorkflowOptions($"job-{jobId}", Queue));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("discovery", StringComparison.OrdinalIgnoreCase));
            await handle.SignalAsync(wf => wf.SubmitDiscoveryAnswersAsync(
                new DiscoveryAnswersSignal(timeline.DiscoveryQaCardId, new(), null)));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));

            // Attempt approval with tech_lead (MUST NOT release gate under HumanOnly)
            await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(
                new PlanApprovedSignal("astra", "tech_lead", "Looks good from lead perspective", DateTimeOffset.UtcNow)));

            await Task.Delay(300);
            execution.TaskTurnCount.Should().Be(0, "tech_lead approval must NOT release gate when policy is HumanOnly");

            // Approve as authenticated human operator
            await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(
                new PlanApprovedSignal("admin-operator", "operator", "Approved by human operator", DateTimeOffset.UtcNow)));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("admin-operator", true, "Approved PR", DateTimeOffset.UtcNow, 101)));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            execution.TaskTurnCount.Should().Be(timeline.CustomTasks.Count);
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task JobWorkflow_TwoManRule_DropsSelfReviewFromAuthorAndWaitsForIndependentReviewer()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities { CustomDiscoveryQuestions = [] };
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();

        using var worker = BuildWorker(env, timeline, execution, git);
        using var cts = new CancellationTokenSource();
        var workerTask = worker.ExecuteAsync(cts.Token);

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(new JobExecutionInput(
                    JobId: jobId,
                    AssignedAgentId: "driver-rook",
                    ProjectId: "mesh-core",
                    WorkspacePath: "/workspace/repos/agent-mesh",
                    InitialPrompt: "Implement Two-Man Rule",
                    TargetBranch: "main",
                    ApprovalPolicy: PlanApprovalPolicy.LeadOrHuman)),
                new WorkflowOptions($"job-{jobId}", Queue));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
            await handle.SignalAsync(wf => wf.SubmitPlanDecisionAsync(
                new PlanApprovalSignal(timeline.PlanCardId, true, "Go", "astra")));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));

            // Self-approval attempts under persona, @handle, and driver-* forms must all be rejected
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("rook", true, "Self approve 1", DateTimeOffset.UtcNow, 101)));
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("@ROOK", true, "Self approve 2", DateTimeOffset.UtcNow, 101)));
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("driver-rook", true, "Self approve 3", DateTimeOffset.UtcNow, 101)));

            await WaitUntilAsync(handle, s => s.IgnoredSelfReviewCount == 3);
            git.MergeCalls.Should().BeEmpty("authoring persona must never be able to merge its own PR");

            // Independent reviewer releases the gate
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("sorren", true, "Verified independently.", DateTimeOffset.UtcNow, 101)));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            git.MergeCalls.Should().ContainSingle();
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task JobWorkflow_ChangesRequested_CreatesRemediationTasks_ReEntersTaskLoop_AndMergesAfterFixes()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities
        {
            CustomDiscoveryQuestions = [],
            CustomTasks = [new("task-1", "Initial implementation", "src/Api.cs", "dotnet test")]
        };
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();

        using var worker = BuildWorker(env, timeline, execution, git);
        using var cts = new CancellationTokenSource();
        var workerTask = worker.ExecuteAsync(cts.Token);

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(new JobExecutionInput(
                    JobId: jobId,
                    AssignedAgentId: "rook",
                    ProjectId: "mesh-core",
                    WorkspacePath: "/workspace/repos/agent-mesh",
                    InitialPrompt: "Implement endpoint",
                    TargetBranch: "main",
                    ApprovalPolicy: PlanApprovalPolicy.LeadOrHuman)),
                new WorkflowOptions($"job-{jobId}", Queue));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
            await handle.SignalAsync(wf => wf.SubmitPlanDecisionAsync(
                new PlanApprovalSignal(timeline.PlanCardId, true, "Approved", "astra")));

            // Round 0 PR opened
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));

            // Request changes with 2 bulleted findings -> must create 2 remediation tasks and loop back into Stage 3
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(new PrReviewSignal(
                ReviewerId: "astra",
                IsApproved: false,
                ReviewComments: "- Fix null check in `src/Api/Auth.cs`\n- Add regression test in `tests/AuthTests.cs`",
                Timestamp: DateTimeOffset.UtcNow,
                PrNumber: 101)));

            // Wait for workflow to complete the 2 remediation tasks, push updated PR (round 1), and park for review again
            await WaitUntilAsync(handle, s =>
                s.ReviewRound == 1 &&
                s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));

            timeline.AppendedRemediationBatches.Should().ContainSingle();
            timeline.AppendedRemediationBatches[0].Tasks.Should().HaveCount(2);
            execution.TaskTurnCount.Should().Be(3, "1 initial task + 2 remediation tasks");
            execution.PrStageTurnCount.Should().Be(2, "initial PR open + updated PR push after remediation");
            git.MergeCalls.Should().BeEmpty();

            // Approve updated PR (round 1 HEAD SHA)
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(new PrReviewSignal(
                ReviewerId: "astra",
                IsApproved: true,
                ReviewComments: "Both findings resolved. LGTM!",
                Timestamp: DateTimeOffset.UtcNow,
                PrNumber: 101)));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            git.MergeCalls.Should().ContainSingle();
            git.MergeCalls[0].ApprovedHeadSha.Should().Be("sha-round-1");
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task JobWorkflow_MergeConflictOnApprovedMerge_RoutesBackToTaskLoopAsBaseMergeRemediationTask()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities
        {
            CustomDiscoveryQuestions = [],
            CustomTasks = [new("task-1", "Feature work", "src/Feature.cs", "dotnet test")]
        };
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();
        git.EnqueueMergeFailure(new ApplicationFailureException(
            "MERGE_CONFLICT: src/Feature.cs, src/Program.cs",
            nonRetryable: true,
            errorType: "merge_conflict"));

        using var worker = BuildWorker(env, timeline, execution, git);
        using var cts = new CancellationTokenSource();
        var workerTask = worker.ExecuteAsync(cts.Token);

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(new JobExecutionInput(
                    JobId: jobId,
                    AssignedAgentId: "rook",
                    ProjectId: "mesh-core",
                    WorkspacePath: "/workspace/repos/agent-mesh",
                    InitialPrompt: "Feature with base conflict",
                    TargetBranch: "main",
                    ApprovalPolicy: PlanApprovalPolicy.LeadOrHuman)),
                new WorkflowOptions($"job-{jobId}", Queue));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
            await handle.SignalAsync(wf => wf.SubmitPlanDecisionAsync(
                new PlanApprovalSignal(timeline.PlanCardId, true, "Approved", "astra")));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));

            // First approval hits merge_conflict -> must NOT halt; must create a conflict remediation task & re-request review!
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("sorren", true, "LGTM", DateTimeOffset.UtcNow, 101)));

            await WaitUntilAsync(handle, s =>
                s.ReviewRound == 1 &&
                s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));

            timeline.AppendedRemediationBatches.Should().ContainSingle();
            timeline.AppendedRemediationBatches[0].Source.Should().Be("merge_conflict");
            timeline.AppendedRemediationBatches[0].Tasks[0].TargetFile.Should().Contain("src/Feature.cs");
            execution.TaskTurnCount.Should().Be(2, "initial task + base-merge conflict remediation task");

            // Second approval merges cleanly
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("sorren", true, "Re-approved after base merge", DateTimeOffset.UtcNow, 101)));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            git.MergeCalls.Should().HaveCount(2);
            git.MergeCalls[1].ApprovedHeadSha.Should().Be("sha-round-1");
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task JobWorkflow_ShaMovedOnApprovedMerge_FailsClosedBackToReviewerGateWithoutCodingTurn()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities
        {
            CustomDiscoveryQuestions = [],
            CustomTasks = [new("task-1", "Feature work", "src/Feature.cs", "dotnet test")]
        };
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();
        git.EnqueueMergeFailure(new ApplicationFailureException(
            "Pull request #101 head is sha-unreviewed, not the approved sha-round-0; re-review required.",
            nonRetryable: true,
            errorType: "sha_moved"));

        using var worker = BuildWorker(env, timeline, execution, git);
        using var cts = new CancellationTokenSource();
        var workerTask = worker.ExecuteAsync(cts.Token);

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(new JobExecutionInput(
                    JobId: jobId,
                    AssignedAgentId: "rook",
                    ProjectId: "mesh-core",
                    WorkspacePath: "/workspace/repos/agent-mesh",
                    InitialPrompt: "Feature where SHA moved",
                    TargetBranch: "main",
                    ApprovalPolicy: PlanApprovalPolicy.LeadOrHuman)),
                new WorkflowOptions($"job-{jobId}", Queue));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
            await handle.SignalAsync(wf => wf.SubmitPlanDecisionAsync(
                new PlanApprovalSignal(timeline.PlanCardId, true, "Approved", "astra")));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));

            // First approval hits sha_moved -> must park back at reviewer gate with ZERO new coding turns!
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("sorren", true, "Approve initial SHA", DateTimeOffset.UtcNow, 101)));

            // Wait for merge attempt #1 to fail closed and re-park at PR review
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && git.MergeCalls.Count < 1)
            {
                await Task.Delay(50);
            }
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));

            execution.TaskTurnCount.Should().Be(1, "sha_moved must fail closed to reviewer without running an unreviewed agent turn");
            timeline.AppendedRemediationBatches.Should().BeEmpty();

            // Reviewer inspects new SHA and re-approves explicitly
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("sorren", true, "Inspected new commit; re-approved", DateTimeOffset.UtcNow, 101, ApprovedHeadSha: "sha-reapproved")));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            git.MergeCalls.Should().HaveCount(2);
            git.MergeCalls[1].ApprovedHeadSha.Should().Be("sha-reapproved");
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task JobWorkflow_TransientMergeFailure_IsRetriedThreeTimesAndFailsVisibly()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities
        {
            CustomDiscoveryQuestions = [],
            CustomTasks = [new("task-1", "Feature work", "src/Feature.cs", "dotnet test")]
        };
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities
        {
            PersistentFailure = new InvalidOperationException("Merge failed transiently (api_error): GitHub returned 503.")
        };

        using var worker = BuildWorker(env, timeline, execution, git);
        using var cts = new CancellationTokenSource();
        var workerTask = worker.ExecuteAsync(cts.Token);

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(new JobExecutionInput(
                    JobId: jobId,
                    AssignedAgentId: "rook",
                    ProjectId: "mesh-core",
                    WorkspacePath: "/workspace/repos/agent-mesh",
                    InitialPrompt: "Transient 503 test",
                    TargetBranch: "main",
                    ApprovalPolicy: PlanApprovalPolicy.LeadOrHuman)),
                new WorkflowOptions($"job-{jobId}", Queue));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
            await handle.SignalAsync(wf => wf.SubmitPlanDecisionAsync(
                new PlanApprovalSignal(timeline.PlanCardId, true, "Approved", "astra")));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("sorren", true, "LGTM", DateTimeOffset.UtcNow, 101)));

            var act = async () => await handle.GetResultAsync<JobExecutionResult>();
            await act.Should().ThrowAsync<WorkflowFailedException>();
            git.MergeCalls.Should().HaveCount(3, "MergeActivityOptions retries transient failures 3 times");
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task JobWorkflow_UnboundedReviewRounds_ContinuesAsNewAndPreservesStateAcrossMultipleRounds()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities
        {
            CustomDiscoveryQuestions = [],
            CustomTasks = [new("task-1", "Initial task", "src/Core.cs", "dotnet test")]
        };
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();

        using var worker = BuildWorker(env, timeline, execution, git);
        using var cts = new CancellationTokenSource();
        var workerTask = worker.ExecuteAsync(cts.Token);

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var wfId = $"job-{jobId}";
            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(new JobExecutionInput(
                    JobId: jobId,
                    ProjectId: "mesh-core",
                    WorkspacePath: "/workspace/repos/agent-mesh",
                    InitialPrompt: "Unbounded review loop via ContinueAsNew",
                    TargetBranch: "main",
                    AssignedAgentId: "rook",
                    ApprovalPolicy: "LeadOrHuman",
                    Continuation: null,
                    ContinueAsNewAfterRounds: 1)),
                new WorkflowOptions(wfId, Queue));

            var initialRunId = handle.FirstExecutionRunId;

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
            await handle.SignalAsync(wf => wf.SubmitPlanDecisionAsync(
                new PlanApprovalSignal(timeline.PlanCardId, true, "Approved", "astra")));

            var latestHandle = env.Client.GetWorkflowHandle<JobExecutionWorkflow>(wfId);

            // Round 0 -> Request changes -> triggers ContinueAsNew #1 -> Round 1
            await WaitUntilLatestAsync(latestHandle, s =>
                s.ReviewRound == 0 && s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));
            await latestHandle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("astra", false, "- Rename helper in `src/Core.cs`", DateTimeOffset.UtcNow, 101)));

            // Round 1 -> Request changes -> triggers ContinueAsNew #2 -> Round 2
            await WaitUntilLatestAsync(latestHandle, s =>
                s.ReviewRound == 1 && s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));
            var secondRunDesc = await latestHandle.DescribeAsync();
            secondRunDesc.RunId.Should().NotBe(initialRunId, "ContinueAsNew must roll over to a fresh Temporal run");

            await latestHandle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("astra", false, "- Add XML doc comment in `src/Core.cs`", DateTimeOffset.UtcNow, 101)));

            // Round 2 -> Approve -> merges and completes!
            await WaitUntilLatestAsync(latestHandle, s =>
                s.ReviewRound == 2 && s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));
            var thirdRunDesc = await latestHandle.DescribeAsync();
            thirdRunDesc.RunId.Should().NotBe(secondRunDesc.RunId, "each ContinueAsNew boundary starts a fresh run");

            await latestHandle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("astra", true, "All rounds addressed. Ship it!", DateTimeOffset.UtcNow, 101)));

            // GetResultAsync follows ContinueAsNew chains automatically by default
            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            timeline.DecompositionInputs.Should().HaveCount(1, "resumed ContinueAsNew runs must NOT re-run Stage 1/2 decomposition");
            execution.TaskTurnCount.Should().Be(3, "1 initial task + 2 rounds of remediation tasks");
            git.MergeCalls.Should().ContainSingle();
            git.MergeCalls[0].ApprovedHeadSha.Should().Be("sha-round-2");
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    private static async Task WaitUntilAsync(
        WorkflowHandle<JobExecutionWorkflow, JobExecutionResult> handle,
        Func<JobWorkflowState, bool> predicate,
        int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var state = await handle.QueryAsync(w => w.GetCurrentState());
                if (predicate(state)) return;
            }
            catch
            {
                // Workflow may still be starting or rolling over ContinueAsNew
            }
            await Task.Delay(100);
        }

        throw new TimeoutException($"Workflow did not satisfy predicate within {timeoutMs}ms");
    }

    private static async Task WaitUntilLatestAsync(
        WorkflowHandle<JobExecutionWorkflow> handle,
        Func<JobWorkflowState, bool> predicate,
        int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var state = await handle.QueryAsync(w => w.GetCurrentState());
                if (predicate(state)) return;
            }
            catch
            {
                // Workflow may be rolling over ContinueAsNew
            }
            await Task.Delay(100);
        }

        throw new TimeoutException($"Latest workflow run did not satisfy predicate within {timeoutMs}ms");
    }

    private sealed class FakeTimelineActivities : ITimelineActivities
    {
        public Guid DiscoveryQaCardId { get; set; } = Guid.NewGuid();
        public Guid PlanCardId { get; set; } = Guid.NewGuid();
        public List<DiscoveryQaActivityInput> DiscoveryQaInputs { get; } = [];
        public List<DecompositionActivityInput> DecompositionInputs { get; } = [];
        public List<AppendRemediationTasksInput> AppendedRemediationBatches { get; } = [];
        public List<DiscoveryQuestion>? CustomDiscoveryQuestions { get; set; }
        public List<ProposedChecklistItem> CustomTasks { get; set; } =
        [
            new("task-1", "Task 1: Core logic", "src/Core.cs", "dotnet test"),
            new("task-2", "Task 2: Worker integration", "src/Worker.cs", "dotnet test"),
            new("task-3", "Task 3: Integration tests", "tests/Integration.cs", "dotnet test")
        ];

        [Activity]
        public Task<JobTimelineCard> CreateCardAsync(CreateTimelineCardInput input) =>
            Task.FromResult(new JobTimelineCard
            {
                Id = Guid.NewGuid(),
                JobId = input.JobId,
                TaskId = input.TaskId,
                CardType = input.CardType,
                Status = input.Status,
                Title = input.Title,
                StructuredPayloadJson = input.StructuredPayloadJson
            });

        [Activity]
        public Task UpdateCardStatusAsync(UpdateTimelineCardStatusInput input) => Task.CompletedTask;

        [Activity]
        public Task<DiscoveryQaActivityResult> ExecuteDiscoveryQaActivityAsync(DiscoveryQaActivityInput input)
        {
            DiscoveryQaInputs.Add(input);
            var questions = CustomDiscoveryQuestions ?? [new("q1", "Question 1?", ["A", "B"])];
            var cardId = questions.Count > 0 ? DiscoveryQaCardId : Guid.Empty;
            return Task.FromResult(new DiscoveryQaActivityResult(cardId, new DiscoveryPayload(questions, "Summary")));
        }

        [Activity]
        public Task<DecompositionActivityResult> ExecuteDecompositionActivityAsync(DecompositionActivityInput input)
        {
            DecompositionInputs.Add(input);
            return Task.FromResult(new DecompositionActivityResult(PlanCardId, new PlanApprovalPayload(CustomTasks, "Reasoning")));
        }

        [Activity]
        public Task<Guid> RecordTurnInterruptedAsync(TurnInterruptedCardInput input) => Task.FromResult(Guid.NewGuid());

        [Activity]
        public Task<Guid> UpdateTaskStepCardAsync(TaskStepCardInput input) => Task.FromResult(Guid.NewGuid());

        [Activity]
        public Task<IReadOnlyList<ProposedChecklistItem>> AppendRemediationTasksAsync(AppendRemediationTasksInput input)
        {
            AppendedRemediationBatches.Add(input);
            return Task.FromResult(input.Tasks);
        }
    }

    private sealed class FakeGitActivities : IGitActivities
    {
        private readonly Queue<Exception> _queuedFailures = new();
        public Exception? PersistentFailure { get; set; }
        public List<MergePullRequestInput> MergeCalls { get; } = [];

        public void EnqueueMergeFailure(Exception ex) => _queuedFailures.Enqueue(ex);

        [Activity]
        public Task<string> MergePullRequestAsync(string prUrl) =>
            Task.FromResult($"Merged {prUrl}");

        [Activity("MergeJobPullRequest")]
        public Task<string> MergeJobPullRequestAsync(MergePullRequestInput input)
        {
            MergeCalls.Add(input);
            if (PersistentFailure != null)
            {
                throw PersistentFailure;
            }
            if (_queuedFailures.Count > 0)
            {
                throw _queuedFailures.Dequeue();
            }
            return Task.FromResult($"Merged {input.PrUrl} at {input.ApprovedHeadSha}");
        }
    }

    private sealed class FakeExecutionActivities : IExecutionActivities
    {
        public int TaskTurnCount { get; private set; }
        public int PrStageTurnCount { get; private set; }

        [Activity]
        public Task<TurnOutcome> RunAgentCodingTurnAsync(TaskExecutionInput input, ChatMessageSignal? pendingReply)
        {
            if (input.TaskId.StartsWith("stage-4-pr-open-", StringComparison.OrdinalIgnoreCase))
            {
                var roundSuffix = input.TaskId["stage-4-pr-open-".Length..];
                PrStageTurnCount++;
                return Task.FromResult(TurnOutcome.AwaitingReview(
                    "https://github.com/ckchessmaster/agent-mesh/pull/101",
                    101,
                    $"Job PR #101 ready (round {roundSuffix})",
                    headSha: $"sha-round-{roundSuffix}"));
            }

            TaskTurnCount++;
            return Task.FromResult(TurnOutcome.Completed($"Completed {input.TaskTitle}"));
        }

        [Activity]
        public Task<TurnOutcome> RunAgentDiscoveryTurnAsync(DiscoveryTurnInput input) =>
            Task.FromResult(TurnOutcome.Completed("Discovery completed"));

        [Activity]
        public Task<TurnOutcome> RunJobCodingTurnAsync(CodingTurnInput input) =>
            Task.FromResult(TurnOutcome.Completed("Job coding turn completed"));

        [Activity]
        public Task<ExecutionOutput> ExecuteShellCommandAsync(CommandInput input) =>
            Task.FromResult(new ExecutionOutput(0, ""));

        public Task<string> ExecuteSubsystemAction(string targetService, string actionVerb, string payloadJson, Guid? jobId = null) =>
            Task.FromResult(string.Empty);

        public Task<CommandExecutionResult> ExecuteCommand(
            RunCommandArgs args,
            Guid? sessionId = null,
            string? commandId = null,
            string? taskWorkspacePath = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandExecutionResult(true, 0, "mock output"));
    }
}
