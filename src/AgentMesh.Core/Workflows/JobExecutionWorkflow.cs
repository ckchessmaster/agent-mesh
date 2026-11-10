using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace AgentMesh.Core.Workflows;

[Workflow]
public class JobExecutionWorkflow : IJobExecutionWorkflow
{
    private static readonly ActivityOptions TurnActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromHours(2),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(5),
            BackoffCoefficient = 2.0f,
            MaximumInterval = TimeSpan.FromMinutes(1),
            MaximumAttempts = 3,
            NonRetryableErrorTypes = ["deliberate_halt", "invalid_input", "unauthorized"]
        }
    };

    private static readonly ActivityOptions GitActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(2),
            BackoffCoefficient = 2.0f,
            MaximumInterval = TimeSpan.FromSeconds(30),
            MaximumAttempts = 3,
            NonRetryableErrorTypes = ["merge_failed", "no_pr_url"]
        }
    };

    private readonly Queue<ChatMessageSignal> _chatInbox = new();
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private PlanApprovedSignal? _planApproval;
    private PrReviewSignal? _prReview;
    private List<ChecklistTaskItem> _tasks = new();
    private int _currentTaskIndex = 0;
    private string _currentHeartbeat = "Initializing job";
    private PlanApprovalPolicy _approvalPolicy = PlanApprovalPolicy.Autonomous;
    private string? _assignedAgentId;

    [WorkflowRun]
    public async Task<JobExecutionResult> ExecuteJobAsync(JobExecutionInput input)
    {
        _approvalPolicy = input.ApprovalPolicy;
        _assignedAgentId = input.AssignedAgentId;

        // 1. Stage 1: Discovery & Planning Loop
        _currentHeartbeat = "Exploring codebase and generating checklist";
        int discoveryTurns = 0;
        const int maxDiscoveryTurns = 15;
        string? activePrUrl = null;

        while (_tasks.Count == 0 && discoveryTurns < maxDiscoveryTurns)
        {
            discoveryTurns++;
            ChatMessageSignal? pendingDiscoveryMessage = _chatInbox.Count > 0 ? _chatInbox.Dequeue() : null;

            var discoveryOutcome = await Workflow.ExecuteActivityAsync(
                (IExecutionActivities act) => act.RunAgentDiscoveryTurnAsync(new DiscoveryTurnInput(
                    input.JobId,
                    input.AssignedAgentId,
                    input.WorkspacePath,
                    input.InitialPrompt,
                    input.ProjectId,
                    input.TargetBranch,
                    pendingDiscoveryMessage)),
                TurnActivityOptions);

            if (discoveryOutcome.Status == TurnOutcomeStatus.Failed)
            {
                _currentHeartbeat = $"Discovery failed: {discoveryOutcome.Message}";
                return new JobExecutionResult(Success: false, Summary: $"Discovery failed: {discoveryOutcome.Message}");
            }

            if (!string.IsNullOrWhiteSpace(discoveryOutcome.PrUrl) || discoveryOutcome.Status == TurnOutcomeStatus.PrOpenedAwaitingReview)
            {
                activePrUrl = discoveryOutcome.PrUrl;
                _currentHeartbeat = "Pull request active; awaiting code review";
                break;
            }

            if (_tasks.Count > 0)
            {
                break;
            }

            if (discoveryOutcome.Status == TurnOutcomeStatus.AwaitingInput)
            {
                _currentHeartbeat = string.IsNullOrWhiteSpace(discoveryOutcome.Message)
                    ? "Awaiting user input on planning questions"
                    : $"Awaiting user input: {discoveryOutcome.Message}";

                await Workflow.WaitConditionAsync(() => _chatInbox.Count > 0 || _tasks.Count > 0);
                _currentHeartbeat = "Resuming discovery with user feedback";
                continue;
            }

            if (discoveryOutcome.Status == TurnOutcomeStatus.StuckLoopDetected)
            {
                _currentHeartbeat = $"Stuck loop detected during discovery: {discoveryOutcome.Message}";
                await Workflow.WaitConditionAsync(() => _chatInbox.Count > 0 || _tasks.Count > 0);
                continue;
            }

            if (discoveryOutcome.Status == TurnOutcomeStatus.Continue)
            {
                _currentHeartbeat = $"Continuing discovery turn {discoveryTurns + 1}/{maxDiscoveryTurns}";
                continue;
            }

            // Completed or other status without setting checklist yet
            break;
        }

        if (_tasks.Count == 0 && string.IsNullOrWhiteSpace(activePrUrl))
        {
            // If discovery completed without setting a checklist, provide a fallback task
            // so the workflow proceeds to implementation rather than stalling at the review gate.
            _tasks.Add(new ChecklistTaskItem(
                Title: !string.IsNullOrWhiteSpace(input.InitialPrompt) ? input.InitialPrompt : "Execute job implementation",
                TargetFile: null,
                VerificationMethod: null));
        }

        if (!string.IsNullOrWhiteSpace(activePrUrl))
        {
            // Fast-track: if a pull request was already opened during discovery, skip planning approval
            // and task execution loops to enter the review gate directly.
            _currentHeartbeat = "Pull request active; awaiting code review";
        }
        else
        {
            if (input.ApprovalPolicy == PlanApprovalPolicy.Autonomous)
            {
                _currentHeartbeat = "Plan auto-approved. Starting implementation";
            }
            else
            {
                if (_currentHeartbeat.StartsWith("Exploring", StringComparison.OrdinalIgnoreCase) ||
                    _currentHeartbeat.StartsWith("Awaiting user input", StringComparison.OrdinalIgnoreCase) ||
                    _currentHeartbeat.StartsWith("Resuming", StringComparison.OrdinalIgnoreCase))
                {
                    _currentHeartbeat = "Awaiting plan approval";
                }

                // 2. Stage 2: Plan Approval Gate
                await Workflow.WaitConditionAsync(() =>
                {
                    if (_tasks.Count == 0 || _planApproval == null) return false;
                    var isApproved = input.ApprovalPolicy switch
                    {
                        PlanApprovalPolicy.LeadOrHuman =>
                            _planApproval.ApproverRole is "operator" or "tech_lead",
                        PlanApprovalPolicy.HumanOnly =>
                            _planApproval.ApproverRole == "operator",
                        _ => false
                    };

                    if (!isApproved)
                    {
                        // Explicitly discard rejected approval signal so it does not linger
                        var rejectedRole = _planApproval.ApproverRole;
                        _planApproval = null;
                        _currentHeartbeat = $"Plan approval rejected: role '{rejectedRole}' is not permitted by policy {input.ApprovalPolicy}";
                        return false;
                    }

                    return true;
                });

                _currentHeartbeat = "Plan approved. Starting implementation";
            }

            // 3. Stage 3: Autonomous Task Iteration Loop
            while (_currentTaskIndex < _tasks.Count)
            {
                var currentTask = _tasks[_currentTaskIndex];
                _currentHeartbeat = $"Executing task {_currentTaskIndex + 1}/{_tasks.Count}: {currentTask.Title}";

                // Check for inbound chat interjections
                ChatMessageSignal? pendingMessage = _chatInbox.Count > 0 ? _chatInbox.Dequeue() : null;

                var turnOutcome = await Workflow.ExecuteActivityAsync(
                    (IExecutionActivities act) => act.RunJobCodingTurnAsync(new CodingTurnInput(
                        input.JobId,
                        currentTask,
                        pendingMessage,
                        input.AssignedAgentId,
                        input.ProjectId,
                        input.WorkspacePath,
                        input.TargetBranch)),
                    TurnActivityOptions);

                if (!string.IsNullOrWhiteSpace(turnOutcome.PrUrl))
                {
                    activePrUrl = turnOutcome.PrUrl;
                }

                if (turnOutcome.Status == TurnOutcomeStatus.AwaitingInput)
                {
                    _currentHeartbeat = $"Awaiting input for task: {currentTask.Title}";
                    await Workflow.WaitConditionAsync(() => _chatInbox.Count > 0);
                    continue;
                }

                if (turnOutcome.Status == TurnOutcomeStatus.StuckLoopDetected)
                {
                    _currentHeartbeat = $"Stuck loop detected on task: {currentTask.Title}";
                    await Workflow.WaitConditionAsync(() => _chatInbox.Count > 0);
                    continue;
                }

                if (turnOutcome.Status == TurnOutcomeStatus.Failed)
                {
                    _currentHeartbeat = $"Task failed: {turnOutcome.Message ?? currentTask.Title}";
                    return new JobExecutionResult(Success: false, Summary: $"Task failed: {turnOutcome.Message}");
                }

                if (turnOutcome.Status is TurnOutcomeStatus.TaskCompleted or TurnOutcomeStatus.PrOpenedAwaitingReview)
                {
                    _currentHeartbeat = $"Completed task {_currentTaskIndex + 1}/{_tasks.Count}: {currentTask.Title}";
                    _currentTaskIndex++;
                }
                else if (turnOutcome.Status == TurnOutcomeStatus.Continue)
                {
                    _currentHeartbeat = $"Continuing task {_currentTaskIndex + 1}/{_tasks.Count}: {currentTask.Title}";
                }
            }
        }

        // 4. Stage 4: Pull Request & Mandatory Review Gate
        bool prApproved = false;
        while (!prApproved)
        {
            _currentHeartbeat = "Awaiting pull request review";
            await Workflow.WaitConditionAsync(() => _prReview != null);

            if (!_prReview!.IsApproved)
            {
                // Changes requested; re-enter coding turn
                _currentHeartbeat = "Changes requested on PR; resuming coding turn";
                ChatMessageSignal? reviewFeedback = new ChatMessageSignal(
                    _prReview.ReviewerId,
                    $"Review feedback on pull request:\n{_prReview.ReviewComments ?? "Changes requested."}",
                    Workflow.UtcNow
                );
                _prReview = null;

                var reviewFixTask = new ChecklistTaskItem(
                    Title: "Address PR review feedback",
                    TargetFile: null,
                    VerificationMethod: "dotnet test"
                );

                while (true)
                {
                    var fixOutcome = await Workflow.ExecuteActivityAsync(
                        (IExecutionActivities act) => act.RunJobCodingTurnAsync(new CodingTurnInput(
                            input.JobId,
                            reviewFixTask,
                            reviewFeedback,
                            input.AssignedAgentId,
                            input.ProjectId,
                            input.WorkspacePath,
                            input.TargetBranch)),
                        TurnActivityOptions);

                    if (!string.IsNullOrWhiteSpace(fixOutcome.PrUrl))
                    {
                        activePrUrl = fixOutcome.PrUrl;
                    }

                    if (fixOutcome.Status == TurnOutcomeStatus.AwaitingInput)
                    {
                        _currentHeartbeat = "Awaiting input on PR review fixes";
                        await Workflow.WaitConditionAsync(() => _chatInbox.Count > 0 || _prReview != null);
                        if (_prReview != null) break;
                        reviewFeedback = _chatInbox.Dequeue();
                        continue;
                    }

                    if (fixOutcome.Status == TurnOutcomeStatus.Continue)
                    {
                        _currentHeartbeat = "Continuing PR review fixes";
                        reviewFeedback = _chatInbox.Count > 0 ? _chatInbox.Dequeue() : null;
                        continue;
                    }

                    break;
                }

                continue;
            }

            prApproved = true;
        }

        // 5. Final Server-Side Merge Activity (reached ONLY when prApproved == true)
        _currentHeartbeat = "Merging pull request";
        var mergePrRef = activePrUrl;
        if (string.IsNullOrWhiteSpace(mergePrRef) && _prReview?.PrNumber != null)
        {
            mergePrRef = _prReview.PrNumber.Value.ToString();
        }

        if (string.IsNullOrWhiteSpace(mergePrRef))
        {
            throw new ApplicationFailureException(
                "Merge requested without a pull request URL or number.", nonRetryable: true, errorType: "no_pr_url");
        }

        var mergeSummary = await Workflow.ExecuteActivityAsync(
            (IGitActivities act) => act.MergePullRequestAsync(mergePrRef),
            GitActivityOptions);

        _currentHeartbeat = "Job completed and merged";
        return new JobExecutionResult(Success: true, Summary: mergeSummary ?? "Job completed and merged.");
    }

    [WorkflowSignal]
    public Task ReceiveChatMessageAsync(ChatMessageSignal signal)
    {
        if (signal is null || string.IsNullOrWhiteSpace(signal.Content))
        {
            return Task.CompletedTask;
        }

        // Self-sent messages (e.g. when the assigned agent calls post_job_message(requires_reply: true))
        // arrive while the turn is still running and must not be queued into _chatInbox, or they will
        // immediately unblock the subsequent WaitConditionAsync without waiting for the human/lead reply.
        if (IsSameAgentIdentity(signal.SenderId, _assignedAgentId))
        {
            return Task.CompletedTask;
        }

        if (!string.IsNullOrWhiteSpace(signal.MessageId) && !_seenMessageIds.Add(signal.MessageId))
        {
            return Task.CompletedTask;
        }

        _chatInbox.Enqueue(signal);
        return Task.CompletedTask;
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
        _planApproval = signal;
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
        new(_currentTaskIndex, _tasks.Count, _currentHeartbeat);
}
