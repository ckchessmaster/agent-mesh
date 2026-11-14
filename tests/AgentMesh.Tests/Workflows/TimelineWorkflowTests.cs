using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Worker.Activities;
using AgentMesh.Worker.Workflows;
using AgentMesh.Tests.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace AgentMesh.Tests.Workflows;

[Collection(SandboxTestCollections.Temporal)]
public class TimelineWorkflowTests
{
    private const string Queue = "timeline-workflow-test-queue";

    [Fact]
    public async Task JobWorkflow_WaitsOnDiscoveryAnswers_BeforeProceedingToDecomposition()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities();
        var execution = new FakeExecutionActivities();
        var git = new FakeGitActivities();

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

        var worker = new TemporalWorker(env.Client, workerOptions);
        await using var testWorker = new TestWorker(worker);

        var jobId = Guid.NewGuid().ToString("N");
        var input = new JobExecutionInput(
            JobId: jobId,
            ProjectId: "mesh-core",
            WorkspacePath: "/workspace",
            InitialPrompt: "Overhaul Temporal orchestration",
            TargetBranch: "main",
            AssignedAgentId: "lead-agent"
        );

        var handle = await env.Client.StartWorkflowAsync(
            (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(input),
            new WorkflowOptions($"job-{jobId}", Queue));

        // 1. Verify workflow waits on discovery answers
        await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("discovery", StringComparison.OrdinalIgnoreCase));
        timeline.DiscoveryQaInputs.Should().HaveCount(1);
        timeline.DecompositionInputs.Should().BeEmpty("Decomposition must NOT execute until discovery answers arrive");

        // 2. Submit discovery answers
        var cardId = timeline.DiscoveryQaCardId;
        var answers = new Dictionary<string, string>
        {
            ["env_target"] = "Local Development (Docker/Bare Metal)",
            ["data_preservation"] = "Strict zero-loss data preservation"
        };
        await handle.SignalAsync(wf => wf.SubmitDiscoveryAnswersAsync(
            new DiscoveryAnswersSignal(cardId, answers, "Target .NET 10 & PostgreSQL 15")));

        // 3. Workflow progresses to decomposition and waits on plan approval
        await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
        timeline.DecompositionInputs.Should().HaveCount(1);

        // 4. Approve plan -> tasks execute -> wait for job-level PR review gate -> approve PR
        await handle.SignalAsync(wf => wf.SubmitPlanDecisionAsync(
            new PlanApprovalSignal(timeline.PlanCardId, true, "Plan approved", "operator")));

        await WaitUntilAsync(handle, s => s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));
        await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
            new PrReviewSignal("operator", true, "LGTM", DateTimeOffset.UtcNow, 42, "abc1234567")));

        var result = await handle.GetResultAsync<JobExecutionResult>();
        result.Success.Should().BeTrue();
        timeline.TaskStepInputs.Should().NotBeEmpty("Child tasks must be dispatched after plan approval");
        execution.CodingInputs.Should().NotBeEmpty("Child tasks must invoke coding turn");
        execution.CodingInputs[0].PersonaId.Should().Be("lead-agent", "Child task must inherit AssignedAgentId as PersonaId");
    }

    [Fact]
    public void ChildTaskExecutionInput_CarriesAssignedAgentIdAsPersonaId()
    {
        var input = new ChildTaskExecutionInput(
            JobId: "job-123",
            TaskId: "task-1",
            TaskIndex: 0,
            TaskTitle: "First Task",
            AssignedAgentId: "rook",
            WorkspacePath: "/workspace",
            TargetBranch: "main"
        );

        input.PersonaId.Should().Be("rook");
        input.AgentId.Should().Be("rook");
    }

    [Fact]
    public void ExtractQuestionsFromText_WithAskQuestionJsonBlock_ExtractsStructuredQuestionsAndOptions()
    {
        var text = """
        I have inspected the repository.
        ```ask_question
        {
          "questions": [
            {
              "question": "Which database engine should be targeted?",
              "options": ["PostgreSQL", "SQLite", "MySQL"]
            },
            {
              "question": "Should migrations run automatically?",
              "options": ["Yes", "No"]
            }
          ]
        }
        ```
        """;

        var questions = TimelineActivities.ExtractQuestionsFromText(text);

        questions.Should().HaveCount(2);
        questions[0].Question.Should().Be("Which database engine should be targeted?");
        questions[0].Options.Should().ContainInOrder("PostgreSQL", "SQLite", "MySQL");
        questions[1].Question.Should().Be("Should migrations run automatically?");
        questions[1].Options.Should().ContainInOrder("Yes", "No");
    }

    [Fact]
    public void ExtractQuestionsFromText_WithNumberedQuestionsAndParentheses_ExtractsOptions()
    {
        var text = """
        Here are my questions before beginning implementation:
        1. Which test framework should we configure? (xUnit, NUnit, or MSTest)
        2. Should we support SQLite fallback? (yes/no)
        """;

        var questions = TimelineActivities.ExtractQuestionsFromText(text);

        questions.Should().HaveCount(2);
        questions[0].Question.Should().Contain("Which test framework should we configure?");
        questions[0].Options.Should().Contain("xUnit");
        questions[0].Options.Should().Contain("NUnit");
        questions[0].Options.Should().Contain("MSTest");
        questions[1].Question.Should().Contain("Should we support SQLite fallback?");
        questions[1].Options.Should().Contain("yes");
        questions[1].Options.Should().Contain("no");
    }

    [Fact]
    public void ExtractQuestionsFromText_WithDomainTradeoffsAndVs_ExtractsOptionsWithoutLiteralQuestionMark()
    {
        var text = """
        Questions to ask (Operator + Architect):
        - Storage: new 'project_threads' table (migration + Architect) vs. zero-migration reuse of 'AgentSession' + metadata. My default: reuse AgentSession.
        - Q2: Who answers? Any persona from project team / default lead persona, or the operator picks a persona per thread?
        - Q3: Read-only enforcement depth - hard refusal in the runner vs. prompt-only instruction (advisory).
        """;

        var questions = TimelineActivities.ExtractQuestionsFromText(text);

        questions.Should().HaveCount(3);
        questions[0].Question.Should().Contain("Storage: new 'project_threads' table");
        questions[0].Options.Should().NotBeNull();
        questions[0].Options!.Should().Contain("new 'project_threads' table");
        questions[0].Options!.Should().Contain("zero-migration reuse of 'AgentSession'");

        questions[1].Question.Should().Contain("Who answers?");

        questions[2].Question.Should().Contain("Read-only enforcement depth");
        questions[2].Options.Should().NotBeNull();
        questions[2].Options!.Should().Contain("hard refusal in the runner");
        questions[2].Options!.Should().Contain("prompt-only instruction");
    }

    [Fact]
    public async Task JobWorkflow_WhenDiscoveryHasZeroQuestions_SkipsDiscoveryQaAndAdvancesToPlanApproval()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities();
        timeline.CustomDiscoveryQuestions = []; // 0 discovery questions
        var execution = new FakeExecutionActivities();

        var git = new FakeGitActivities();
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

        using var worker = new TemporalWorker(env.Client, workerOptions);
        await worker.ExecuteAsync(async () =>
        {
            var jobId = Guid.NewGuid().ToString();
            var handle = await env.Client.StartWorkflowAsync(
                (IJobExecutionWorkflow wf) => wf.ExecuteJobAsync(new JobExecutionInput(
                    JobId: jobId,
                    AssignedAgentId: "lead-agent",
                    ProjectId: "proj-zero-q",
                    WorkspacePath: "/workspace",
                    InitialPrompt: "Create automated feature without clarifications",
                    TargetBranch: "main",
                    ApprovalPolicy: PlanApprovalPolicy.Autonomous
                )),
                new WorkflowOptions($"test-job-{jobId}", Queue)
            );

            // Wait for decomposition to complete and plan approval card to be active
            while (timeline.DecompositionInputs.Count == 0)
            {
                await Task.Delay(100);
            }

            // Verify Discovery Q&A was NOT waited on (no discovery answers signal required)
            timeline.DiscoveryQaInputs.Should().HaveCount(1);
            timeline.UpdatedCards.Should().NotContain(c => c.CardId == timeline.DiscoveryQaCardId);

            // Submit plan approval
            await handle.SignalAsync((IJobExecutionWorkflow wf) => wf.SubmitPlanDecisionAsync(
                new PlanApprovalSignal(timeline.PlanCardId, true, "Plan approved immediately", "operator")));

            var prDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < prDeadline)
            {
                var st = await handle.QueryAsync((IJobExecutionWorkflow wf) => wf.GetCurrentState());
                if (st.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase)) break;
                await Task.Delay(50);
            }
            await handle.SignalAsync((IJobExecutionWorkflow wf) => wf.ReceivePrReviewAsync(
                new PrReviewSignal("operator", true, "LGTM", DateTimeOffset.UtcNow, 42, "abc1234567")));

            var result = await handle.GetResultAsync<JobExecutionResult>();
            result.Success.Should().BeTrue();
            timeline.TaskStepInputs.Should().NotBeEmpty();
            execution.CodingInputs.Should().NotBeEmpty();
        });
    }

    [Fact]
    public async Task JobWorkflow_RejectionLoop_ReDecomposesWithFeedback()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities();
        var execution = new FakeExecutionActivities();

        var git = new FakeGitActivities();
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

        var worker = new TemporalWorker(env.Client, workerOptions);
        await using var testWorker = new TestWorker(worker);

        var jobId = Guid.NewGuid().ToString("N");
        var input = new JobExecutionInput(
            JobId: jobId,
            ProjectId: "mesh-core",
            WorkspacePath: "/workspace",
            InitialPrompt: "Overhaul Temporal orchestration",
            TargetBranch: "main",
            AssignedAgentId: "lead-agent"
        );

        var handle = await env.Client.StartWorkflowAsync(
            (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(input),
            new WorkflowOptions($"job-{jobId}", Queue));

        await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("discovery", StringComparison.OrdinalIgnoreCase));
        await handle.SignalAsync(wf => wf.SubmitDiscoveryAnswersAsync(
            new DiscoveryAnswersSignal(timeline.DiscoveryQaCardId, new(), null)));

        await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
        timeline.DecompositionInputs.Should().HaveCount(1);
        timeline.DecompositionInputs[0].Feedback.Should().BeNull();

        // Reject the plan with feedback
        await handle.SignalAsync(wf => wf.SubmitPlanDecisionAsync(
            new PlanApprovalSignal(timeline.PlanCardId, false, "Include process group kill tests", "operator")));

        // Wait for re-decomposition loop
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && timeline.DecompositionInputs.Count < 2)
        {
            await Task.Delay(50);
        }
        await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
        timeline.DecompositionInputs.Should().HaveCount(2, "Workflow must re-run decomposition on plan rejection");
        timeline.DecompositionInputs[1].Feedback.Should().Be("Include process group kill tests");

        // Approve on second round
        await handle.SignalAsync(wf => wf.SubmitPlanDecisionAsync(
            new PlanApprovalSignal(timeline.PlanCardId, true, "Revised plan approved", "operator")));

        await WaitUntilAsync(handle, s => s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));
        await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
            new PrReviewSignal("operator", true, "LGTM", DateTimeOffset.UtcNow, 42, "abc1234567")));

        var result = await handle.GetResultAsync<JobExecutionResult>();
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task TaskWorkflow_AbortTurn_TriggersCancellation_And_WaitsForIntervention()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities();
        var execution = new FakeExecutionActivities();

        var workerOptions = new TemporalWorkerOptions(Queue)
            .AddActivity((Func<TaskStepCardInput, Task<Guid>>)timeline.UpdateTaskStepCardAsync)
            .AddActivity((Func<TurnInterruptedCardInput, Task<Guid>>)timeline.RecordTurnInterruptedAsync)
            .AddActivity((Func<TaskExecutionInput, ChatMessageSignal?, Task<TurnOutcome>>)execution.RunAgentCodingTurnAsync)
            .AddWorkflow<TaskExecutionWorkflow>();
        workerOptions.StickyQueueScheduleToStartTimeout = TimeSpan.FromSeconds(1);

        var worker = new TemporalWorker(env.Client, workerOptions);
        await using var testWorker = new TestWorker(worker);

        var jobId = Guid.NewGuid().ToString("N");
        var input = new ChildTaskExecutionInput(
            JobId: jobId,
            TaskId: "task-01",
            TaskIndex: 0,
            TaskTitle: "Process group isolation",
            AssignedAgentId: "lead-agent",
            WorkspacePath: "/workspace",
            TargetBranch: "main"
        );

        // Configure execution activity to simulate a running turn that respects cancellation
        var turnStarted = new TaskCompletionSource<bool>();
        execution.CustomCodingHandler = async (taskIn, reply) =>
        {
            turnStarted.TrySetResult(true);
            var actContext = ActivityExecutionContext.Current;
            await Task.Delay(10_000, actContext.CancellationToken);
            return TurnOutcome.Completed("Finished");
        };

        var handle = await env.Client.StartWorkflowAsync(
            (TaskExecutionWorkflow wf) => wf.ExecuteTaskAsync(input),
            new WorkflowOptions($"child-task-{jobId}", Queue));

        await turnStarted.Task;

        // Signal abort turn
        await handle.SignalAsync(wf => wf.AbortTurnAsync(
            new AbortActiveTurnSignal("Stop active turn clicked by operator", "operator")));

        // Wait until TurnInterrupted card is recorded
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && timeline.TurnInterruptedInputs.Count == 0)
        {
            await Task.Delay(50);
        }

        timeline.TurnInterruptedInputs.Should().HaveCount(1, "TurnInterrupted card must be recorded on activity cancellation");
        timeline.TurnInterruptedInputs[0].Reason.Should().Be("Stop active turn clicked by operator");

        // Now provide intervention: resume
        execution.CustomCodingHandler = (taskIn, reply) => Task.FromResult(TurnOutcome.Completed("Completed after resume"));
        await handle.SignalAsync(wf => wf.InterveneTurnAsync(new TurnInterventionSignal("resume", "Resume after inspection")));

        var result = await handle.GetResultAsync<TaskExecutionResult>();
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task JobWorkflow_AbortAndResume_DoesNotLeakAbortToNextTask()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var timeline = new FakeTimelineActivities();
        var execution = new FakeExecutionActivities();

        timeline.CustomTasks =
        [
            new("task-1", "Task 1", "file1.cs", "test 1"),
            new("task-2", "Task 2", "file2.cs", "test 2")
        ];

        var task1Started = new TaskCompletionSource<bool>();
        var task2Started = new TaskCompletionSource<bool>();

        execution.CustomCodingHandler = async (taskIn, reply) =>
        {
            if (taskIn.TaskId == "task-1")
            {
                task1Started.TrySetResult(true);
                var actContext = ActivityExecutionContext.Current;
                await Task.Delay(10_000, actContext.CancellationToken);
                return TurnOutcome.Completed("Task 1 finished");
            }
            else
            {
                task2Started.TrySetResult(true);
                return TurnOutcome.Completed("Task 2 finished");
            }
        };

        var git = new FakeGitActivities();
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

        var worker = new TemporalWorker(env.Client, workerOptions);
        await using var testWorker = new TestWorker(worker);

        var jobId = Guid.NewGuid().ToString("N");
        var input = new JobExecutionInput(
            JobId: jobId,
            ProjectId: "mesh-core",
            WorkspacePath: "/workspace",
            InitialPrompt: "Multi-task workflow",
            TargetBranch: "main",
            AssignedAgentId: "lead-agent"
        );

        var handle = await env.Client.StartWorkflowAsync(
            (JobExecutionWorkflow wf) => wf.ExecuteJobAsync(input),
            new WorkflowOptions($"job-{jobId}", Queue));

        // Submit discovery & approve plan
        await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("discovery", StringComparison.OrdinalIgnoreCase));
        await handle.SignalAsync(wf => wf.SubmitDiscoveryAnswersAsync(
            new DiscoveryAnswersSignal(timeline.DiscoveryQaCardId, new(), null)));

        await WaitUntilAsync(handle, s => s.CurrentHeartbeat.Contains("Awaiting plan approval"));
        await handle.SignalAsync(wf => wf.SubmitPlanDecisionAsync(
            new PlanApprovalSignal(timeline.PlanCardId, true, "Plan approved", "operator")));

        // Wait for task 1 to begin execution
        await task1Started.Task;

        // Abort task 1
        await handle.SignalAsync(wf => wf.AbortTurnAsync(
            new AbortActiveTurnSignal("Abort task 1", "operator")));

        // Wait for TurnInterrupted card
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && timeline.TurnInterruptedInputs.Count == 0)
        {
            await Task.Delay(50);
        }

        timeline.TurnInterruptedInputs.Should().HaveCount(1, "TurnInterrupted recorded for task 1");
        timeline.TurnInterruptedInputs[0].TaskId.Should().Be("task-1");

        // Allow task 1 to complete upon resume
        execution.CustomCodingHandler = (taskIn, reply) =>
        {
            if (taskIn.TaskId == "task-2")
            {
                task2Started.TrySetResult(true);
            }
            return Task.FromResult(TurnOutcome.Completed($"{taskIn.TaskId} finished"));
        };

        // Intervene to resume
        await handle.SignalAsync(wf => wf.InterveneTurnAsync(new TurnInterventionSignal("resume", "Resume task 1")));

        // Task 2 should now execute and finish without being interrupted
        await task2Started.Task;

        await WaitUntilAsync(handle, s => s.CurrentHeartbeat.StartsWith("Awaiting PR review", StringComparison.OrdinalIgnoreCase));
        await handle.SignalAsync(wf => wf.ReceivePrReviewAsync(
            new PrReviewSignal("operator", true, "LGTM", DateTimeOffset.UtcNow, 42, "abc1234567")));

        var result = await handle.GetResultAsync<JobExecutionResult>();
        result.Success.Should().BeTrue("Job should complete successfully across both tasks");

        // Confirm task 2 was never interrupted
        timeline.TurnInterruptedInputs.Should().HaveCount(1, "Task 2 must not be interrupted by stale task 1 abort signal");
    }

    [Fact]
    public async Task ProcessGroup_ExecutionActivities_LinuxSetpgid_And_KillGroup()
    {
        var activities = new ExecutionActivities(NullLogger<ExecutionActivities>.Instance);

        using var cts = new CancellationTokenSource();
        var workspace = Path.GetTempPath();

        // Run a command and cancel it
        var runTask = activities.ExecuteShellCommandAsync(
            new CommandInput("sleep 15", workspace),
            cts.Token);

        // Cancel after 100ms
        await Task.Delay(100);
        cts.Cancel();

        Func<Task> act = async () => await runTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task WaitUntilAsync(
        WorkflowHandle<JobExecutionWorkflow, JobExecutionResult> handle,
        Func<JobWorkflowState, bool> predicate,
        int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string lastHeartbeat = "NONE";
        Exception? lastEx = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var state = await handle.QueryAsync(w => w.GetCurrentState());
                lastHeartbeat = state?.CurrentHeartbeat ?? "NULL";
                if (predicate(state!)) return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
            await Task.Delay(100);
        }

        throw new TimeoutException($"Workflow did not satisfy predicate within {timeoutMs}ms. Last heartbeat: '{lastHeartbeat}', Last ex: {lastEx?.GetType().Name}: {lastEx?.Message}");
    }

    private sealed class FakeTimelineActivities : ITimelineActivities
    {
        public Guid DiscoveryQaCardId { get; set; } = Guid.NewGuid();
        public Guid PlanCardId { get; set; } = Guid.NewGuid();
        public List<CreateTimelineCardInput> CreatedCards { get; } = [];
        public List<UpdateTimelineCardStatusInput> UpdatedCards { get; } = [];
        public List<DiscoveryQaActivityInput> DiscoveryQaInputs { get; } = [];
        public List<DecompositionActivityInput> DecompositionInputs { get; } = [];
        public List<TurnInterruptedCardInput> TurnInterruptedInputs { get; } = [];
        public List<TaskStepCardInput> TaskStepInputs { get; } = [];

        [Activity]
        public Task<JobTimelineCard> CreateCardAsync(CreateTimelineCardInput input)
        {
            CreatedCards.Add(input);
            return Task.FromResult(new JobTimelineCard
            {
                Id = Guid.NewGuid(),
                JobId = input.JobId,
                TaskId = input.TaskId,
                CardType = input.CardType,
                Status = input.Status,
                Title = input.Title,
                StructuredPayloadJson = input.StructuredPayloadJson
            });
        }

        [Activity]
        public Task UpdateCardStatusAsync(UpdateTimelineCardStatusInput input)
        {
            UpdatedCards.Add(input);
            return Task.CompletedTask;
        }

        public List<DiscoveryQuestion>? CustomDiscoveryQuestions { get; set; }

        [Activity]
        public Task<DiscoveryQaActivityResult> ExecuteDiscoveryQaActivityAsync(DiscoveryQaActivityInput input)
        {
            DiscoveryQaInputs.Add(input);
            var questions = CustomDiscoveryQuestions ?? new List<DiscoveryQuestion> { new("q1", "Question 1", new[] { "A", "B" }) };
            var payload = new DiscoveryPayload(questions, "Discovery summary");
            var cardId = questions.Count > 0 ? DiscoveryQaCardId : Guid.Empty;
            return Task.FromResult(new DiscoveryQaActivityResult(cardId, payload));
        }

        public List<ProposedChecklistItem> CustomTasks { get; set; } =
        [
            new("task-1", "Task 1", "file1.cs", "test 1")
        ];

        [Activity]
        public Task<DecompositionActivityResult> ExecuteDecompositionActivityAsync(DecompositionActivityInput input)
        {
            DecompositionInputs.Add(input);
            var payload = new PlanApprovalPayload(CustomTasks, "Decomp reasoning");
            return Task.FromResult(new DecompositionActivityResult(PlanCardId, payload));
        }

        [Activity]
        public Task<Guid> RecordTurnInterruptedAsync(TurnInterruptedCardInput input)
        {
            TurnInterruptedInputs.Add(input);
            return Task.FromResult(Guid.NewGuid());
        }

        [Activity]
        public Task<Guid> UpdateTaskStepCardAsync(TaskStepCardInput input)
        {
            TaskStepInputs.Add(input);
            return Task.FromResult(Guid.NewGuid());
        }

        [Activity]
        public Task<IReadOnlyList<ProposedChecklistItem>> AppendRemediationTasksAsync(AppendRemediationTasksInput input) =>
            Task.FromResult(input.Tasks);
    }

    private sealed class FakeGitActivities : IGitActivities
    {
        public List<MergePullRequestInput> MergeInputs { get; } = [];

        [Activity]
        public Task<string> MergePullRequestAsync(string prUrl) =>
            Task.FromResult($"Merged {prUrl}");

        [Activity("MergeJobPullRequest")]
        public Task<string> MergeJobPullRequestAsync(MergePullRequestInput input)
        {
            MergeInputs.Add(input);
            return Task.FromResult($"Merged {input.PrUrl}");
        }
    }

    private sealed class FakeExecutionActivities : IExecutionActivities
    {
        public List<TaskExecutionInput> CodingInputs { get; } = new();
        public Func<TaskExecutionInput, ChatMessageSignal?, Task<TurnOutcome>>? CustomCodingHandler { get; set; }

        [Activity]
        public Task<TurnOutcome> RunAgentCodingTurnAsync(TaskExecutionInput input, ChatMessageSignal? pendingReply)
        {
            CodingInputs.Add(input);
            if (input.TaskId.StartsWith("stage-4-pr-open", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TurnOutcome.AwaitingReview(
                    "https://github.com/ckchessmaster/agent-mesh/pull/42",
                    42,
                    "Opened job-level PR #42",
                    "abc1234567"));
            }
            if (CustomCodingHandler != null)
            {
                return CustomCodingHandler(input, pendingReply);
            }
            return Task.FromResult(TurnOutcome.Completed("Task completed successfully"));
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

    private sealed class TestWorker : IAsyncDisposable
    {
        private readonly CancellationTokenSource cts = new();
        private readonly TemporalWorker worker;
        private readonly Task running;

        public TestWorker(TemporalWorker worker)
        {
            this.worker = worker;
            this.running = worker.ExecuteAsync(cts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            try { await running; } catch (OperationCanceledException) { }
            worker.Dispose();
            cts.Dispose();
        }
    }
}
