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
            .AddActivity(execution.RunAgentCodingTurnAsync)
            .AddActivity((Func<MergePullRequestInput, Task<string>>)git.MergePullRequestAsync)
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
            .AddActivity(execution.RunAgentCodingTurnAsync)
            .AddActivity((Func<MergePullRequestInput, Task<string>>)git.MergePullRequestAsync)
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

        [Activity]
        public Task<TurnOutcome> RunAgentDiscoveryTurnAsync(DiscoveryTurnInput input)
        {
            DiscoveryTurnCount++;
            DiscoveryInputs.Add(input);
            return Task.FromResult(TurnOutcome.Completed("Discovery completed. Checklist prepared."));
        }

        [Activity]
        public Task<TurnOutcome> RunAgentCodingTurnAsync(CodingTurnInput input)
        {
            CodingTurnCount++;
            CodingInputs.Add(input);
            return Task.FromResult(TurnOutcome.Completed("Task completed"));
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

        public Task<string> MergePullRequestAsync(string prUrl)
        {
            MergeUrls.Add(prUrl);
            return Task.FromResult($"merged {prUrl}");
        }

        [Activity]
        public Task<string> MergePullRequestAsync(MergePullRequestInput input)
        {
            MergeUrls.Add($"job-{input.JobId}");
            return Task.FromResult($"merged job-{input.JobId}");
        }
    }
}
