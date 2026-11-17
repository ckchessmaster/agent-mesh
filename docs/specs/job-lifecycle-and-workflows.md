# Job-First Autonomous Lifecycle & Temporal Workflows

## 1. Overview & Core Philosophy

AgentMesh coordinates autonomous coding agents through a **Job-First Autonomous Lifecycle**. Rather than treating agent interactions as disconnected, stateless prompt-response chats, AgentMesh models engineering work as structured, durable **Project Jobs** with strict alignment gates, living task checklists, multi-repo workspace isolation, and two-man review verification.

All asynchronous and long-running execution is governed durably by **Temporal** workflows, ensuring that network interruptions, pod restarts, runner timeouts, or days-long review delays never drop state or corrupt job progress.

---

## 2. The 4-Stage Autonomous Job Lifecycle

Every `ProjectJob` in AgentMesh progresses through four formal stages:

```mermaid
flowchart TD
    subgraph S1["Stage 1: Discovery & Planning"]
        D1["Agent Assigned in Read-Only Mode"] --> D2["Inspect Codebase (grep, find, read_file)"]
        D2 --> D3["Decompose into 3-6 Atomic Tasks"]
        D3 --> D4["Register Checklist via manage_task(set_checklist)"]
        D4 --> D5["Post Plan & Questions via post_job_message(requires_reply: true)"]
    end

    subgraph S2["Stage 2: Plan Alignment Gate"]
        G1["Temporal Workflow Enters Durable Wait State"] --> G2["Operator or Lead Reviews Plan in Cockpit"]
        G2 --> G3["Approval Signal Submitted via /api/jobs/{id}/approval"]
    end

    subgraph S3["Stage 3: Task Execution & Observability"]
        E1["Sequential Task Processing"] --> E2["Update Status: manage_task(update_task_status)"]
        E2 --> E3["Emit 1-Sentence Progress Note Heartbeats"]
        E3 --> E4["Enforce 3-Strike Rule on Edit/Test Failures"]
    end

    subgraph S4["Stage 4: Verification & Two-Man Review"]
        V1["Execute Full Verification & Test Suites"] --> V2["Call open_pull_request MCP Tool"]
        V2 --> V3["Turn Yielded; Self-Merging Strictly Prohibited"]
        V3 --> V4["Assigned Reviewer or Operator Signs Off"]
    end

    S1 --> S2
    S2 --> S3
    S3 --> S4
```

### Stage 1: Discovery & Planning
* **Execution Boundary**: When a job is in `pending_discovery` or lacks an approved checklist, the assigned persona operates strictly in read-only mode.
* **Prohibited Actions**: File edits, git commits, destructive branch resets, or code generation.
* **Required Output**:
  1. Decompose the job objective into **3 to 6 atomic, verifiable tasks**.
  2. Call `manage_task(action: "set_checklist", tasks: [...])`.
  3. Post the proposed plan and any architectural trade-offs to the Job Chat via `post_job_message(requires_reply: true)`.
  4. Conclude the turn immediately without attempting code modifications.

### Stage 2: Plan Alignment Gate
* **Enforcement**: The underlying `JobExecutionWorkflow` halts turn dispatching and parks the job in `blocked` or `waiting_for_approval`.
* **Escalation Notification**: SignalR emits a real-time event to the Web Cockpit, rendering interactive approval buttons and diff views for the human operator or assigned tech lead.
* **Resumption**: Upon approval via `POST /api/jobs/{id}/approval`, Temporal receives the `SubmitApproval` signal and transitions the job to `in_progress`.

### Stage 3: Task Execution & Observability
* **Task-by-Task Focus**: The agent executes checklist items sequentially, calling `manage_task(action: "update_task_status", task_id: "...", status: "in_progress")`.
* **Heartbeat Observability**: With every status transition, the agent provides a concise, 1-sentence `progress_note` so operators can follow real-time status without inspecting raw terminal dumps.
* **The 3-Strike Rule**: If an agent attempts 3 consecutive unsuccessful edit-test iterations to resolve an error:
  1. The agent must revert its dirty changes (`git checkout -- <files>`).
  2. It must halt execution and escalate the blocker via `post_job_message(requires_reply: true)` following the 3-R format (Result, Reasoning, Risk).
  3. The turn yields immediately to prevent runaway token spend and model hallucination spirals.

### Stage 4: Verification & Two-Man Review
* **Final Verification**: Once all checklist tasks are marked `completed`, the agent executes full verification (unit tests, integration tests, linters).
* **PR Creation**: The agent invokes `open_pull_request(title: "...", body: "...")`.
* **Strict Anti-Self-Merge Policy**: Agents are strictly forbidden from self-approving or self-merging their own pull requests. Calling `open_pull_request` transitions the job to `needs_review` and durably yields the turn to the designated human operator or review persona.

---

## 3. Temporal Workflow Architecture

AgentMesh isolates durable execution into dedicated workflow definitions registered on the Temporal cluster:

### `JobExecutionWorkflow`
* **Purpose**: Manages the overarching lifecycle of a `ProjectJob` across days or weeks.
* **Key Signals**:
  * `SubmitApproval(bool approved, string? note)`: Unblocks the Stage 2 plan alignment gate or human escalation.
  * `TaskCompleted(string taskId, string summary)`: Signals completion of an individual checklist item.
  * `TurnResumed(string personaId)`: Signals that a blocked agent has received human/peer input in Job Chat.
* **Durable Wait Handlers**: Uses `Workflow.WaitConditionAsync(...)` with configurable timeouts (up to 7 days) without holding active HTTP or database connection pools.

### `TaskExecutionWorkflow`
* **Purpose**: Coordinates an individual execution turn or task batch assigned to a specific persona.
* **Turn Session Tracking**: Generates a cryptographically unique `TurnSessionId` for each invocation, recording execution boundaries in `task_working_checkpoints`.
* **Retry & Failure Recovery**: Applies exponential backoff retry policies for transient network or container faults while surfacing non-retryable domain rejections to the Job Chat.

---

## 4. Macro-Turns & Turn Dispatching

Execution turns are dispatched through the control plane via `JobTurnDispatcher` and `HttpAgentTurnRunner`:

```mermaid
sequenceDiagram
    autonumber
    participant W as Temporal Worker (JobExecutionWorkflow)
    participant D as JobTurnDispatcher
    participant R as Runner Pod / CLI Daemon (agentmesh)
    participant API as AgentMesh API
    participant DB as PostgreSQL

    W->>D: DispatchAgentTurnAsync(jobId, taskId, personaId)
    D->>DB: Resolve layered prompt stack & workspace state
    D->>R: POST /macro-turn { jobId, personaId, turnSessionId, promptStack }
    Note over R: Runner spawns sandboxed execution loop (CLI/LLM)
    R->>API: Calls MCP Tools (manage_task, post_job_message)
    R-->>D: HTTP 200 { turnOutcome, exitReason }
    D->>DB: Save TaskWorkingCheckpoint & JobWorkspaceState
    D-->>W: Return TurnOutcome
```

### Turn Timeout & Output Spillover Protection
* **Command Timeouts**: Shell executions inside runner environments are bounded by a strict 180-second timeout. Runaway processes are terminated via process-group `SIGTERM` followed by `SIGKILL`.
* **Spillover Redirection**: Any stdout or stderr output exceeding 60 lines or 4 KB is automatically redirected to `/tmp/agentmesh/runs/last_run.log`, preventing context window bloat while preserving full logs on disk for debugging.
* **The clamp does not cover subprocess drivers**: the 180-second bound applies to commands the *platform* executes. A driver such as `agy` or `copilot` runs its own tools inside its own process, so nothing above it can bound what it runs. When one of those drivers wedges, the failure is silence, not a timeout.

### Stalled Turns: Detection, Settlement & Recovery

A turn is *stalled* when its session is still reporting `thinking` or `busy` and nothing is advancing it. This is strictly worse than a failure: the transcript's last row is an unanswered `tool_call`, no terminal status is ever written, and the cockpit renders a lie until a human notices.

`TurnStallPolicy` is the single definition, and the **ordering of its three thresholds is load-bearing** — the worker, the API sweeper and the CLI ship as separate images, and a sweeper tuned tighter than the worker's ceiling would settle live turns mid-command:

| Threshold | Value | Owner |
| --- | --- | --- |
| Driver stream idle bound | 20 min (`AGENTMESH_DRIVER_IDLE_TIMEOUT_MINUTES`) | CLI adapter, per subprocess driver |
| Observation ceiling | 110 min (`MaxTurnDuration`) | `HttpAgentTurnRunner` |
| Orphan sweep floor | 130 min (ceiling + 20 min grace) | `OrphanedTurnSweeperService` |

* **Settlement writes two things, always**: the session moves to `stopped`, *and* a `[turn-stalled]` transcript row records why and at what instant. A bare status flip is indistinguishable from an operator pressing Stop, and leaves the next investigation starting from zero.
* **Only live statuses are ever rewritten.** A completed turn, a session parked `waiting_for_input` for a human, and an operator-stopped session are facts about what happened; settlement is a repair, not an override. A wrong settle is survivable because the next real event overwrites the status.
* **The sweeper is the backstop for turns no workflow observes** — cockpit chat, a handoff, a worker pod deleted mid-poll. It runs in the API because the API owns `AgentMeshHub`: settling without broadcasting `SessionUpdated` is correct in the database and wrong on the screen. `TurnReconciler:OrphanMinutes` may only be raised above the floor, never lowered.
* **The job carries the same truth**: its `ProgressHeartbeat` is prefixed `Stalled:`, which is what the dashboard's lifecycle derivation checks *before* the `"Executing task"` text it was writing when the turn died. Stalled is a heartbeat marker, not a new `ProjectJobStatus`.
* **Recovery is an endpoint, not a procedure.** `POST /api/project-jobs/{id}/recover` settles the abandoned turns, rewinds the workspace checkpoint they left parked at `in_progress`, then either aborts the turn of a still-open run (never starting a second run onto the same workspace lease) or starts one run resuming at the first incomplete task — the same rule the workflow applies to its own continue-as-new. `resume: false` erases the phantom turns without starting anything. No Temporal cluster access is required, and nothing is lost by retrying: the settle is idempotent.

### The Pull-Request Gate: Reviews, CI, and One Card Per PR

Stage 4 parks on a review gate, and everything it waits for arrives as the same `PrReviewSignal`. Three producers feed it — the two review endpoints, the GitHub-side review poller, and CI — and the gate's merge, two-man and approved-SHA semantics do not know which one spoke.

* **Reviewers work on GitHub, so the platform watches GitHub.** `PrReviewPollingService` finds `PrReview` cards the workflow parked in `PendingInput`, reads the pull request's reviews, and delivers a decision as the signal the REST endpoints already send. An approval counts only for the commit it saw; a review older than the current park is treated as one this park already acted on.
* **Failed CI is a request-changes review, and only that.** A red build used to sit on the pull request informing nobody: the author had stopped working and a human had to notice and type into a review box what GitHub had already said. The poller reads the head commit's check runs and statuses and delivers a failure with `IsApproved: false`, which takes the workflow's existing changes-requested branch — round incremented, one remediation task per failing check, author sent back, gate re-parked on the new head. Nothing in the workflow was forked to admit a machine reviewer.
* **Green CI approves nothing.** A passing build is not the second pair of eyes the gate exists to obtain, and delivering it as an approval would let a machine authorise its own author's code and merge it. CI moves a job backwards or not at all.
* **The head commit is the epoch, which is what stops the loop.** A human stops talking when satisfied; CI fails a commit forever. A failure is therefore reported once per head commit per job, and that record survives the gate momentarily leaving `PendingInput` during remediation — dropping it there would turn one broken build into a remediation round every few minutes. A new complaint requires a new commit, giving automated review the same property a human has: the author must do something first.
* **Only an explicit `failure` is a failure.** `cancelled`, `timed_out`, `action_required`, `stale`, `neutral` and `skipped` describe the runner or a human who stopped a run more often than they describe the code, and a commit status of `error` usually means the publisher broke. Checks that are still `queued` or `in_progress` are never a verdict: CI is almost always mid-flight when a gate parks.
* **One card per pull request.** The workflow's gate and the CLI's `pr open` both write the review card, each deduplicating against its own idea of "the card for this PR", so every job that reached Stage 4 posted two cards that looked identical in the cockpit and left the poller guessing which timestamp was the real park. Identity is now shared (`PrReviewCardPayload`: pull-request number, else URL) and the second writer adopts and merges instead of appending, so each writer keeps its own fields — a gate status write no longer deletes the summary the CLI recorded. Adoption lives inside `CreateCardAsync`, which keeps the workflow's Temporal command sequence untouched and so stays safe for runs parked mid-review across a deploy.

---

## 5. Job Handoffs & Multi-Persona Collaboration

When a task requires cross-functional expertise (e.g. an Engineer escalating a schema change to an Architect, or yielding to a QA Engineer):
1. **Handoff Record**: A `JobHandoff` entity is created, capturing the source persona, target persona, active git branch, checkpoint hash, and handoff reason.
2. **Context Continuity**: The target persona resumes execution with the exact branch and workspace state recorded in the handoff.
3. **Reaper Background Service**: The `JobHandoffReaper` periodic background service monitors stale handoffs, re-queuing orphaned jobs or notifying operators if a designated reviewer remains unresponsive.
