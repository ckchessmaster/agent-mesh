using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Tests.Sandbox;
using FluentAssertions;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace AgentMesh.Tests.Workflows;

[Collection(SandboxTestCollections.Temporal)]
public class JobExecutionWorkflowTests
{
    private const string Queue = "job-execution-test-queue";

    [Fact]
    public async Task JobWorkflow_StaysInDiscovery_UntilChecklistRegistered()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();

        var workerOptions = new TemporalWorkerOptions(Queue)
            .AddActivity(execution.RunAgentDiscoveryTurnAsync)
            .AddActivity(execution.RunJobCodingTurnAsync)
            .AddActivity((Func<string, Task<string>>)git.MergePullRequestAsync)
            .AddActivity((Func<MergePullRequestInput, Task<string>>)git.MergeJobPullRequestAsync)
            .AddWorkflow<JobExecutionWorkflow>();
        workerOptions.StickyQueueScheduleToStartTimeout = TimeSpan.FromSeconds(1);

        using var worker = new TemporalWorker(env.Client, workerOptions);
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
                InitialPrompt: "Implement autonomous lifecycle",
                TargetBranch: "main",
                ApprovalPolicy: PlanApprovalPolicy.LeadOrHuman
            );

            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(input),
                new WorkflowOptions($"job-{jobId}", Queue));

            // Wait until workflow completes discovery and parks at Stage 2 (Awaiting plan approval)
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));

            execution.DiscoveryTurnCount.Should().Be(1, "discovery turn must be executed during Stage 1");
            execution.CodingTurnCount.Should().Be(0, "coding turns must NOT execute while awaiting plan approval");

            // Set checklist with 3 tasks
            var tasks = new List<ChecklistTaskItem>
            {
                new("Task 1: Add policy schema", "src/Models.cs", "dotnet test"),
                new("Task 2: Add temporal workflow", "src/Workflow.cs", "dotnet test"),
                new("Task 3: Add unit tests", "tests/Tests.cs", "dotnet test")
            };
            await handle.SignalAsync(wf => wf.ReceiveSetChecklistAsync(tasks));

            // Verify coding turns still do not run until plan approval is registered
            await Task.Delay(200);
            execution.CodingTurnCount.Should().Be(0, "coding turns must still be blocked until approval signal arrives");

            // Approve plan as tech_lead (allowed under LeadOrHuman policy)
            await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(
                new PlanApprovedSignal("astra", "tech_lead", "Plan looks great. Proceed.", DateTimeOffset.UtcNow)));

            // Provide PR review approval so workflow can complete all stages
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("sorren", true, "LGTM", DateTimeOffset.UtcNow, 101)));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();

            execution.CodingTurnCount.Should().Be(3, "all 3 tasks must be sequentially executed after plan approval");
            git.MergeUrls.Should().ContainSingle();
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task JobWorkflow_RespectsHumanOnlyApprovalPolicy()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();

        var workerOptions = new TemporalWorkerOptions(Queue)
            .AddActivity(execution.RunAgentDiscoveryTurnAsync)
            .AddActivity(execution.RunJobCodingTurnAsync)
            .AddActivity((Func<string, Task<string>>)git.MergePullRequestAsync)
            .AddActivity((Func<MergePullRequestInput, Task<string>>)git.MergeJobPullRequestAsync)
            .AddWorkflow<JobExecutionWorkflow>();
        workerOptions.StickyQueueScheduleToStartTimeout = TimeSpan.FromSeconds(1);

        using var worker = new TemporalWorker(env.Client, workerOptions);
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
                ApprovalPolicy: PlanApprovalPolicy.HumanOnly // HumanOnly gate
            );

            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(input),
                new WorkflowOptions($"job-{jobId}", Queue));

            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));

            var tasks = new List<ChecklistTaskItem>
            {
                new("Task 1: Core logic", "src/Core.cs", "dotnet test"),
                new("Task 2: Worker integration", "src/Worker.cs", "dotnet test"),
                new("Task 3: Integration tests", "tests/Integration.cs", "dotnet test")
            };
            await handle.SignalAsync(wf => wf.ReceiveSetChecklistAsync(tasks));

            // Attempt approval with tech_lead (MUST BE REJECTED / NOT RELEASE GATE under HumanOnly)
            await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(
                new PlanApprovedSignal("astra", "tech_lead", "Looks good from lead perspective", DateTimeOffset.UtcNow)));

            // Wait and verify workflow did NOT advance
            await Task.Delay(300);
            execution.CodingTurnCount.Should().Be(0, "tech_lead approval must NOT release gate when policy is HumanOnly");

            // Now approve as authenticated human operator
            await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(
                new PlanApprovedSignal("admin-operator", "operator", "Approved by human operator", DateTimeOffset.UtcNow)));

            // Provide PR review approval
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("admin-operator", true, "PR approved", DateTimeOffset.UtcNow, 202)));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            execution.CodingTurnCount.Should().Be(3, "workflow proceeds to coding turns once human operator approves");
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task JobWorkflow_SupportsIterativeDiscoveryAndQuestionRounds()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();

        var workerOptions = new TemporalWorkerOptions(Queue)
            .AddActivity(execution.RunAgentDiscoveryTurnAsync)
            .AddActivity(execution.RunJobCodingTurnAsync)
            .AddActivity((Func<string, Task<string>>)git.MergePullRequestAsync)
            .AddActivity((Func<MergePullRequestInput, Task<string>>)git.MergeJobPullRequestAsync)
            .AddWorkflow<JobExecutionWorkflow>();
        workerOptions.StickyQueueScheduleToStartTimeout = TimeSpan.FromSeconds(1);

        using var worker = new TemporalWorker(env.Client, workerOptions);
        using var cts = new CancellationTokenSource();
        var workerTask = worker.ExecuteAsync(cts.Token);

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var input = new JobExecutionInput(
                JobId: jobId,
                AssignedAgentId: "rook",
                ProjectId: "mesh-core",
                WorkspacePath: "/workspace",
                InitialPrompt: "Add document upload support",
                TargetBranch: "main",
                ApprovalPolicy: PlanApprovalPolicy.LeadOrHuman
            );

            // Handler for discovery turns: Turn 1 asks questions; Turn 2 asks follow-up; Turn 3 registers checklist
            execution.CustomDiscoveryHandler = (turnInput) =>
            {
                if (execution.DiscoveryTurnCount == 1)
                {
                    return Task.FromResult(TurnOutcome.Awaiting("operator", "Should we use Option A or Option B for persistence?"));
                }
                if (execution.DiscoveryTurnCount == 2)
                {
                    turnInput.PendingMessage.Should().NotBeNull();
                    turnInput.PendingMessage!.Content.Should().Contain("Option A");
                    return Task.FromResult(TurnOutcome.Awaiting("operator", "Should we include React UI in this PR?"));
                }
                turnInput.PendingMessage.Should().NotBeNull();
                turnInput.PendingMessage!.Content.Should().Contain("Full stack");
                return Task.FromResult(TurnOutcome.Completed("Decomposition finished and checklist registered."));
            };

            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(input),
                new WorkflowOptions($"job-{jobId}", Queue));

            // Wait until turn 1 finishes and workflow parks awaiting user input
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting user input"));
            execution.DiscoveryTurnCount.Should().Be(1);
            execution.CodingTurnCount.Should().Be(0);

            // User replies to first question round
            await handle.SignalAsync(wf => wf.ReceiveChatMessageAsync(
                new ChatMessageSignal("operator", "Please use Option A (project_documents table)", DateTimeOffset.UtcNow)));

            // Wait until turn 2 finishes and workflow parks on second question round
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting user input") && execution.DiscoveryTurnCount == 2);
            execution.DiscoveryTurnCount.Should().Be(2);

            // User replies to second question round
            await handle.SignalAsync(wf => wf.ReceiveChatMessageAsync(
                new ChatMessageSignal("operator", "Yes, Full stack: include React UI", DateTimeOffset.UtcNow)));

            // Register checklist before turn 3 completes (simulating manage_task(set_checklist))
            var tasks = new List<ChecklistTaskItem>
            {
                new("Task 1: Add DB migration", "src/Migration.cs", "dotnet test"),
                new("Task 2: Add API endpoints", "src/Controller.cs", "dotnet test"),
                new("Task 3: Add React UI", "src/Dashboard.tsx", "npm test")
            };
            await handle.SignalAsync(wf => wf.ReceiveSetChecklistAsync(tasks));

            // Wait until Stage 1 discovery completes and workflow parks at Stage 2
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
            execution.DiscoveryTurnCount.Should().Be(3);
            execution.CodingTurnCount.Should().Be(0);

            // Tech lead approves plan
            await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(
                new PlanApprovedSignal("astra", "tech_lead", "Approved. Proceed with implementation.", DateTimeOffset.UtcNow)));

            // PR Review approval
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("sorren", true, "LGTM", DateTimeOffset.UtcNow, 42)));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            execution.CodingTurnCount.Should().Be(3);
            git.MergeUrls.Should().ContainSingle();
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task JobWorkflow_EndToEnd_FullLifecycle_ChatDiscoveryApprovalCodingRejectRecodeApproveMerge()
    {
        // End-to-end lifecycle:
        // 1. Chat input starts workflow
        // 2. Discovery executes and registers checklist
        // 3. Approval gate releases
        // 4. Tasks execute sequentially
        // 5. PR review is rejected with feedback
        // 6. Workflow re-enters coding turn to address feedback without merging
        // 7. PR review is approved
        // 8. Server-side merge succeeds
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();

        var workerOptions = new TemporalWorkerOptions(Queue)
            .AddActivity(execution.RunAgentDiscoveryTurnAsync)
            .AddActivity(execution.RunJobCodingTurnAsync)
            .AddActivity((Func<string, Task<string>>)git.MergePullRequestAsync)
            .AddActivity((Func<MergePullRequestInput, Task<string>>)git.MergeJobPullRequestAsync)
            .AddWorkflow<JobExecutionWorkflow>();
        workerOptions.StickyQueueScheduleToStartTimeout = TimeSpan.FromSeconds(1);

        using var worker = new TemporalWorker(env.Client, workerOptions);
        using var cts = new CancellationTokenSource();
        var workerTask = worker.ExecuteAsync(cts.Token);

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var input = new JobExecutionInput(
                JobId: jobId,
                AssignedAgentId: "driver-implementer",
                ProjectId: "mesh-core",
                WorkspacePath: "/workspace/repos/mesh-core",
                InitialPrompt: "Build full lifecycle end to end",
                TargetBranch: "main",
                ApprovalPolicy: PlanApprovalPolicy.LeadOrHuman
            );

            // Step 1: Start workflow (simulating chat trigger)
            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(input),
                new WorkflowOptions($"job-{jobId}", Queue));

            // Step 2: Discovery turn executes
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
            execution.DiscoveryTurnCount.Should().Be(1);
            execution.CodingTurnCount.Should().Be(0);

            // Decompose into 3 atomic checklist tasks (simulating manage_task set_checklist)
            var tasks = new List<ChecklistTaskItem>
            {
                new("1. Discovery & Models", "src/Models.cs", "dotnet test"),
                new("2. Temporal Workflow", "src/Workflow.cs", "dotnet test"),
                new("3. Verification Gate", "src/Gate.cs", "dotnet test")
            };
            await handle.SignalAsync(wf => wf.ReceiveSetChecklistAsync(tasks));

            // Step 3: Approve plan as tech_lead
            await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(
                new PlanApprovedSignal("astra", "tech_lead", "Plan approved. Proceed with implementation.", DateTimeOffset.UtcNow)));

            // Step 4: Coding turns execute for each task
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting pull request review"));
            execution.CodingTurnCount.Should().Be(3, "all 3 tasks must run to completion");
            git.MergeUrls.Should().BeEmpty("merge must NOT execute while awaiting PR review");

            // Step 5: Reject PR review with requested changes
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("sorren", false, "Please handle null check in router before merge.", DateTimeOffset.UtcNow, 77)));

            // Step 6: Verify workflow loops back to re-code the review feedback
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting pull request review") && execution.CodingTurnCount == 4);
            execution.CodingTurnCount.Should().Be(4, "an extra coding turn must execute to address rejected review feedback");
            git.MergeUrls.Should().BeEmpty("rejected review MUST NEVER fall through to merge");

            // Verify the feedback message was passed to the fix turn
            execution.CodingInputs.Last().PendingMessage.Should().NotBeNull();
            execution.CodingInputs.Last().PendingMessage!.Content.Should().Contain("Please handle null check in router");

            // Step 7: Now approve the revised PR
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("sorren", true, "Fix confirmed, approved for merge.", DateTimeOffset.UtcNow, 77)));

            // Step 8: PR merged successfully
            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            git.MergeUrls.Should().ContainSingle();
            git.MergeUrls[0].Should().Contain("77");
        }
        finally
        {
            cts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task JobWorkflow_WhenPrOpenedDuringDiscovery_FastTracksDirectlyToReviewGate()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();

        var workerOptions = new TemporalWorkerOptions(Queue)
            .AddActivity(execution.RunAgentDiscoveryTurnAsync)
            .AddActivity(execution.RunJobCodingTurnAsync)
            .AddActivity((Func<string, Task<string>>)git.MergePullRequestAsync)
            .AddActivity((Func<MergePullRequestInput, Task<string>>)git.MergeJobPullRequestAsync)
            .AddWorkflow<JobExecutionWorkflow>();
        workerOptions.StickyQueueScheduleToStartTimeout = TimeSpan.FromSeconds(1);

        using var worker = new TemporalWorker(env.Client, workerOptions);
        using var cts = new CancellationTokenSource();
        var workerTask = worker.ExecuteAsync(cts.Token);

        try
        {
            var jobId = Guid.NewGuid().ToString("N");
            var input = new JobExecutionInput(
                JobId: jobId,
                AssignedAgentId: "rook",
                ProjectId: "mesh-core",
                WorkspacePath: "/workspace",
                InitialPrompt: "Job Artifact Support",
                TargetBranch: "main",
                ApprovalPolicy: PlanApprovalPolicy.LeadOrHuman
            );

            // Simulate agent creating PR directly during discovery (e.g. out-of-band PR #41)
            execution.CustomDiscoveryHandler = _ => Task.FromResult(
                TurnOutcome.AwaitingReview(
                    "https://github.com/ckchessmaster/agent-mesh/pull/41",
                    41,
                    "PR opened during discovery turn"));

            var handle = await env.Client.StartWorkflowAsync(
                (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(input),
                new WorkflowOptions($"job-{jobId}", Queue));

            // Fast-tracks past Stage 2 (Plan Approval) & Stage 3 (Task Iteration) straight to Stage 4 (Review Gate)
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting pull request review"));
            execution.DiscoveryTurnCount.Should().Be(1);
            execution.CodingTurnCount.Should().Be(0, "tasks should be bypassed when PR was already opened in discovery");

            // Operator approves PR
            await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
                new PrReviewSignal("operator", true, "PR #41 looks good", DateTimeOffset.UtcNow, 41)));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            git.MergeUrls.Should().ContainSingle();
            git.MergeUrls[0].Should().Contain("41");
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
                // Workflow may still be starting
            }
            await Task.Delay(100);
        }

        throw new TimeoutException($"Workflow did not satisfy predicate within {timeoutMs}ms");
    }

    private sealed class FakeExecutionActivities : IExecutionActivities
    {
        public int DiscoveryTurnCount { get; private set; }
        public int CodingTurnCount { get; private set; }
        public List<DiscoveryTurnInput> DiscoveryInputs { get; } = [];
        public List<CodingTurnInput> CodingInputs { get; } = [];
        public Func<DiscoveryTurnInput, Task<TurnOutcome>>? CustomDiscoveryHandler { get; set; }

        [Activity]
        public Task<TurnOutcome> RunAgentDiscoveryTurnAsync(DiscoveryTurnInput input)
        {
            DiscoveryTurnCount++;
            DiscoveryInputs.Add(input);
            if (CustomDiscoveryHandler is not null)
            {
                return CustomDiscoveryHandler(input);
            }
            return Task.FromResult(TurnOutcome.Completed("Discovery completed. Checklist prepared."));
        }

        [Activity]
        public Task<TurnOutcome> RunJobCodingTurnAsync(CodingTurnInput input)
        {
            CodingTurnCount++;
            CodingInputs.Add(input);
            return Task.FromResult(TurnOutcome.Completed(
                $"Task completed: {input.CurrentTask.Title}",
                prUrl: "https://github.com/ckchessmaster/agent-mesh/pull/77",
                prNumber: 77));
        }

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

    private sealed class FakeGitActivities : IGitActivities
    {
        public List<string> MergeUrls { get; } = [];

        [Activity]
        public Task<string> MergePullRequestAsync(string prUrl)
        {
            MergeUrls.Add(prUrl);
            return Task.FromResult($"merged {prUrl}");
        }

        [Activity("MergeJobPullRequest")]
        public Task<string> MergeJobPullRequestAsync(MergePullRequestInput input)
        {
            var url = input.PrUrl ?? $"job-{input.JobId}";
            MergeUrls.Add(url);
            return Task.FromResult($"merged {url}");
        }
    }
}
