using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using Temporalio.Api.Enums.V1;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

[Workflow("JobExecutionWorkflow")]
public class JobExecutionWorkflow : IJobExecutionWorkflow
{
    private static readonly ActivityOptions TimelineActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromHours(1),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2.0f,
            MaximumInterval = TimeSpan.FromSeconds(30),
            MaximumAttempts = 3
        }
    };

    private readonly Queue<ChatMessageSignal> _chatInbox = new();
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private DiscoveryAnswersSignal? _discoveryAnswers;
    private PlanApprovalSignal? _timelinePlanApproval;
    private PrReviewSignal? _prReview;
    private AbortActiveTurnSignal? _pendingAbort;
    private TurnInterventionSignal? _pendingIntervention;
    private ChildWorkflowHandle<ITaskExecutionWorkflow, TaskExecutionResult>? _activeChildHandle;
    private string? _activeChildWorkflowId;
    private List<ChecklistTaskItem> _tasks = new();
    private int _currentTaskIndex;
    private int _totalTasks;
    private string _currentHeartbeat = "Initializing job";
    private PlanApprovalPolicy _approvalPolicy = PlanApprovalPolicy.LeadOrHuman;
    private string? _assignedAgentId;

    [WorkflowRun]
    public async Task<JobExecutionResult> ExecuteJobAsync(JobExecutionInput input)
    {
        _assignedAgentId = input.AssignedAgentId;
        if (Enum.TryParse<PlanApprovalPolicy>(input.ApprovalPolicy, ignoreCase: true, out var parsedPolicy))
        {
            _approvalPolicy = parsedPolicy;
        }
        else
        {
            _approvalPolicy = PlanApprovalPolicy.LeadOrHuman;
        }

        return await ExecuteTimelineLoopAsync(input);
    }

    public Task<JobExecutionResult> RunAsync(JobExecutionInput input) => ExecuteJobAsync(input);

    private async Task<JobExecutionResult> ExecuteTimelineLoopAsync(JobExecutionInput input)
    {
        _currentHeartbeat = "Initializing job";

        var initPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            jobId = input.JobId,
            projectId = input.ProjectId,
            workspacePath = input.WorkspacePath,
            targetBranch = input.TargetBranch,
            assignedAgentId = input.AssignedAgentId,
            initialPrompt = input.InitialPrompt,
            approvalPolicy = input.ApprovalPolicy
        });

        var initCard = await Workflow.ExecuteActivityAsync(
            (ITimelineActivities act) => act.CreateCardAsync(new CreateTimelineCardInput(
                input.JobId,
                null,
                TimelineCardType.JobInitialized,
                TimelineCardStatus.Active,
                $"Job Initialized: {input.JobId}",
                initPayload
            )),
            TimelineActivityOptions
        );

        // Stage 1A: Discovery Q&A activity -> wait on DiscoveryAnswersSignal if clarifying questions were asked
        var discoveryResult = await Workflow.ExecuteActivityAsync(
            (ITimelineActivities act) => act.ExecuteDiscoveryQaActivityAsync(new DiscoveryQaActivityInput(
                input.JobId,
                input.InitialPrompt,
                input.WorkspacePath
            )),
            TimelineActivityOptions
        );

        if (discoveryResult.Payload.Questions.Count > 0 && discoveryResult.CardId != Guid.Empty)
        {
            _currentHeartbeat = "Awaiting discovery Q&A answers";
            await Workflow.WaitConditionAsync(() => _discoveryAnswers != null);

            await Workflow.ExecuteActivityAsync(
                (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                    discoveryResult.CardId,
                    TimelineCardStatus.Approved
                )),
                TimelineActivityOptions
            );
        }

        // Stage 1B, Stage 2 & Stage 3: Decomposition + Approval + Child Task Execution (with Realign loop)
        string? planFeedback = null;

        while (true)
        {
            DecompositionActivityResult decompositionResult;

            while (true)
            {
                _timelinePlanApproval = null;
                _currentHeartbeat = planFeedback == null ? "Decomposing job tasks" : "Re-decomposing plan with feedback";
                var answers = _discoveryAnswers?.Answers ?? new Dictionary<string, string>();
                decompositionResult = await Workflow.ExecuteActivityAsync(
                    (ITimelineActivities act) => act.ExecuteDecompositionActivityAsync(new DecompositionActivityInput(
                        input.JobId,
                        input.InitialPrompt,
                        input.WorkspacePath,
                        planFeedback,
                        answers
                    )),
                    TimelineActivityOptions
                );

                _currentHeartbeat = "Awaiting plan approval";
                await Workflow.WaitConditionAsync(() => _timelinePlanApproval != null);

                if (_timelinePlanApproval!.Approved)
                {
                    await Workflow.ExecuteActivityAsync(
                        (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                            decompositionResult.CardId,
                            TimelineCardStatus.Approved
                        )),
                        TimelineActivityOptions
                    );
                    break;
                }

                planFeedback = _timelinePlanApproval.Feedback ?? "Plan rejected by approver";
                await Workflow.ExecuteActivityAsync(
                    (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                        decompositionResult.CardId,
                        TimelineCardStatus.Rejected
                    )),
                    TimelineActivityOptions
                );
            }

            var tasks = decompositionResult.Payload.Tasks;
            _totalTasks = tasks.Count;
            bool realigned = false;

            for (int idx = 0; idx < tasks.Count; idx++)
            {
                _currentTaskIndex = idx;
                var task = tasks[idx];
                _currentHeartbeat = $"Executing task {idx + 1}/{_totalTasks}: {task.Title}";

                _pendingAbort = null;
                _pendingIntervention = null;

                var childInput = new ChildTaskExecutionInput(
                    input.JobId,
                    task.TaskId,
                    idx,
                    task.Title,
                    input.AssignedAgentId,
                    input.WorkspacePath,
                    input.TargetBranch,
                    input.ProjectId
                );

                var childWorkflowId = $"job-{input.JobId}-task-{idx}";
                _activeChildWorkflowId = childWorkflowId;

                var childHandle = await Workflow.StartChildWorkflowAsync(
                    (ITaskExecutionWorkflow wf) => wf.ExecuteTaskAsync(childInput),
                    new ChildWorkflowOptions
                    {
                        Id = childWorkflowId,
                        TaskQueue = Workflow.Info.TaskQueue,
                        IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate
                    }
                );

                _activeChildHandle = childHandle;

                if (_pendingAbort != null)
                {
                    await childHandle.SignalAsync(wf => wf.AbortTurnAsync(_pendingAbort));
                    _pendingAbort = null;
                }
                if (_pendingIntervention != null)
                {
                    await childHandle.SignalAsync(wf => wf.InterveneTurnAsync(_pendingIntervention));
                    _pendingIntervention = null;
                }

                var result = await childHandle.GetResultAsync();
                _activeChildHandle = null;
                _activeChildWorkflowId = null;
                _pendingAbort = null;
                _pendingIntervention = null;

                if (!result.Success)
                {
                    var err = result.ErrorMessage ?? result.Summary ?? "Unknown error";
                    if (err.StartsWith("Realigned:", StringComparison.OrdinalIgnoreCase))
                    {
                        planFeedback = err;
                        realigned = true;
                        break;
                    }

                    _currentHeartbeat = $"Failed on task {idx + 1}";
                    return new JobExecutionResult(false, $"Task {idx} ({task.Title}) failed: {err}");
                }
            }

            if (realigned)
            {
                continue;
            }

            break;
        }

        if (initCard != null && initCard.Id != Guid.Empty)
        {
            await Workflow.ExecuteActivityAsync(
                (ITimelineActivities act) => act.UpdateCardStatusAsync(new UpdateTimelineCardStatusInput(
                    initCard.Id,
                    TimelineCardStatus.Resolved
                )),
                TimelineActivityOptions
            );
        }

        _currentHeartbeat = "Completed all tasks successfully";
        return new JobExecutionResult(true, "All tasks completed successfully.");
    }

    [WorkflowSignal]
    public Task SubmitDiscoveryAnswersAsync(DiscoveryAnswersSignal signal)
    {
        _discoveryAnswers = signal;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task SubmitPlanDecisionAsync(PlanApprovalSignal signal)
    {
        _timelinePlanApproval = signal;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public async Task AbortTurnAsync(AbortActiveTurnSignal signal)
    {
        if (_activeChildHandle != null)
        {
            await _activeChildHandle.SignalAsync(wf => wf.AbortTurnAsync(signal));
            _pendingAbort = null;
        }
        else if (_activeChildWorkflowId != null)
        {
            _pendingAbort = signal;
        }
        else
        {
            _pendingAbort = null;
        }
    }

    [WorkflowSignal]
    public async Task InterveneTurnAsync(TurnInterventionSignal signal)
    {
        _pendingAbort = null;
        if (_activeChildHandle != null)
        {
            await _activeChildHandle.SignalAsync(wf => wf.InterveneTurnAsync(signal));
            _pendingIntervention = null;
        }
        else if (_activeChildWorkflowId != null)
        {
            _pendingIntervention = signal;
        }
        else
        {
            _pendingIntervention = null;
        }
    }

    [WorkflowSignal]
    public async Task ReceiveChatMessageAsync(ChatMessageSignal signal)
    {
        if (signal is null || string.IsNullOrWhiteSpace(signal.Content))
        {
            return;
        }

        if (IsSameAgentIdentity(signal.SenderId, _assignedAgentId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(signal.MessageId) && !_seenMessageIds.Add(signal.MessageId))
        {
            return;
        }

        _chatInbox.Enqueue(signal);
        if (_activeChildHandle != null)
        {
            await _activeChildHandle.SignalAsync(wf => wf.ReceiveChatMessageAsync(signal));
        }
    }

    private static bool IsSameAgentIdentity(string? senderId, string? assignedAgentId)
    {
        if (string.IsNullOrWhiteSpace(senderId) || string.IsNullOrWhiteSpace(assignedAgentId))
        {
            return false;
        }

        var normSender = NormalizeIdentity(senderId);
        var normAssigned = NormalizeIdentity(assignedAgentId);
        return normSender.Length > 0 && string.Equals(normSender, normAssigned, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIdentity(string id)
    {
        var trimmed = id.Trim().TrimStart('@');
        if (trimmed.StartsWith("driver-", StringComparison.OrdinalIgnoreCase) && trimmed.Length > "driver-".Length)
        {
            trimmed = trimmed["driver-".Length..];
        }
        return trimmed;
    }

    [WorkflowSignal]
    public Task ReceivePlanApprovalAsync(PlanApprovedSignal signal)
    {
        if (_approvalPolicy == PlanApprovalPolicy.HumanOnly &&
            !string.Equals(signal.ApproverRole, "operator", StringComparison.OrdinalIgnoreCase))
        {
            _currentHeartbeat = $"Plan approval rejected: role '{signal.ApproverRole}' is not permitted by policy {_approvalPolicy}";
            return Task.CompletedTask;
        }

        _timelinePlanApproval = new PlanApprovalSignal(Guid.Empty, true, signal.Comments, signal.ApproverId);
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task ReceivePrReviewAsync(PrReviewSignal signal)
    {
        _prReview = signal;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task ReceiveSetChecklistAsync(List<ChecklistTaskItem> tasks)
    {
        _tasks = tasks ?? new();
        _totalTasks = _tasks.Count;
        _currentHeartbeat = _approvalPolicy == PlanApprovalPolicy.Autonomous
            ? "Checklist registered. Auto-approving plan."
            : "Checklist registered. Awaiting plan approval.";
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task UpdateHeartbeatAsync(string heartbeat)
    {
        _currentHeartbeat = heartbeat;
        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public JobWorkflowState GetCurrentState() =>
        new(_currentTaskIndex, _tasks.Count > 0 ? _tasks.Count : _totalTasks, _currentHeartbeat);
}
