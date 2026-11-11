using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Worker.Workflows;
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
    public async Task JobWorkflow_WithSevenArgConstructor_RunsTimelineLoopAndExecutesTasks()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities();
        var execution = new FakeExecutionActivities();

        var workerOptions = new TemporalWorkerOptions(Queue)
            .AddActivity((Func<CreateTimelineCardInput, Task<JobTimelineCard>>)timeline.CreateCardAsync)
            .AddActivity((Func<UpdateTimelineCardStatusInput, Task>)timeline.UpdateCardStatusAsync)
            .AddActivity((Func<DiscoveryQaActivityInput, Task<DiscoveryQaActivityResult>>)timeline.ExecuteDiscoveryQaActivityAsync)
            .AddActivity((Func<DecompositionActivityInput, Task<DecompositionActivityResult>>)timeline.ExecuteDecompositionActivityAsync)
            .AddActivity((Func<TurnInterruptedCardInput, Task<Guid>>)timeline.RecordTurnInterruptedAsync)
            .AddActivity((Func<TaskStepCardInput, Task<Guid>>)timeline.UpdateTaskStepCardAsync)
            .AddActivity((Func<TaskExecutionInput, ChatMessageSignal?, Task<TurnOutcome>>)execution.RunAgentCodingTurnAsync)
            .AddWorkflow<JobExecutionWorkflow>()
            .AddWorkflow<TaskExecutionWorkflow>();
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
            execution.CodingTurnCount.Should().Be(0);

            // 2. Submit Discovery Q&A answers
            var answers = new Dictionary<string, string> { ["q1"] = "A" };
            await handle.SignalAsync(wf => wf.SubmitDiscoveryAnswersAsync(
                new DiscoveryAnswersSignal(timeline.DiscoveryQaCardId, answers, null)));

            // 3. Wait until workflow parks at Plan Approval
            await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
            timeline.DecompositionInputs.Should().HaveCount(1);
            execution.CodingTurnCount.Should().Be(0);

            // 4. Approve plan via ReceivePlanApprovalAsync (tech_lead permitted under LeadOrHuman)
            await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(
                new PlanApprovedSignal("astra", "tech_lead", "Plan looks great. Proceed.", DateTimeOffset.UtcNow)));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            execution.CodingTurnCount.Should().Be(timeline.CustomTasks.Count);
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

        var workerOptions = new TemporalWorkerOptions(Queue)
            .AddActivity((Func<CreateTimelineCardInput, Task<JobTimelineCard>>)timeline.CreateCardAsync)
            .AddActivity((Func<UpdateTimelineCardStatusInput, Task>)timeline.UpdateCardStatusAsync)
            .AddActivity((Func<DiscoveryQaActivityInput, Task<DiscoveryQaActivityResult>>)timeline.ExecuteDiscoveryQaActivityAsync)
            .AddActivity((Func<DecompositionActivityInput, Task<DecompositionActivityResult>>)timeline.ExecuteDecompositionActivityAsync)
            .AddActivity((Func<TurnInterruptedCardInput, Task<Guid>>)timeline.RecordTurnInterruptedAsync)
            .AddActivity((Func<TaskStepCardInput, Task<Guid>>)timeline.UpdateTaskStepCardAsync)
            .AddActivity((Func<TaskExecutionInput, ChatMessageSignal?, Task<TurnOutcome>>)execution.RunAgentCodingTurnAsync)
            .AddWorkflow<JobExecutionWorkflow>()
            .AddWorkflow<TaskExecutionWorkflow>();
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
            execution.CodingTurnCount.Should().Be(0, "tech_lead approval must NOT release gate when policy is HumanOnly");

            // Approve as authenticated human operator
            await handle.SignalAsync(wf => wf.ReceivePlanApprovalAsync(
                new PlanApprovedSignal("admin-operator", "operator", "Approved by human operator", DateTimeOffset.UtcNow)));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            execution.CodingTurnCount.Should().Be(timeline.CustomTasks.Count);
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

    private sealed class FakeTimelineActivities : ITimelineActivities
    {
        public Guid DiscoveryQaCardId { get; set; } = Guid.NewGuid();
        public Guid PlanCardId { get; set; } = Guid.NewGuid();
        public List<DiscoveryQaActivityInput> DiscoveryQaInputs { get; } = [];
        public List<DecompositionActivityInput> DecompositionInputs { get; } = [];
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
            var questions = new List<DiscoveryQuestion> { new("q1", "Question 1?", new[] { "A", "B" }) };
            return Task.FromResult(new DiscoveryQaActivityResult(DiscoveryQaCardId, new DiscoveryPayload(questions, "Summary")));
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
    }

    private sealed class FakeExecutionActivities : IExecutionActivities
    {
        public int CodingTurnCount { get; private set; }

        [Activity]
        public Task<TurnOutcome> RunAgentCodingTurnAsync(TaskExecutionInput input, ChatMessageSignal? pendingReply)
        {
            CodingTurnCount++;
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
