# Complete Stabilization & Job-Level PR/Review Loop Redesign Spec

**Target Audience**: Autonomous Cloud Implementer  
**Execution Order**: Execute **Part 1 through Part 6 sequentially**. Each part must compile cleanly and pass scoped tests before proceeding to the next.  
**Pre-Flight Hygiene**:
- Run `git worktree prune` before starting and verify `git status` is clean.
- Inspect `git branch -a` and `origin` before writing Part 1: if any commits from `mesh/job-84e00028` (`a5186af..390246c`) or `mesh-job-84e00028.patch` already exist, cherry-pick or build upon verified work rather than duplicating it[cite: 4].
- Always run scoped tests using `--filter` during iteration; run the full suite once at the end[cite: 4].
- Always emit concise visible assistant text during turns and alongside/before `task_complete`[cite: 4].

---

## Part 1: Unbounded Prompt Columns, Timeline Unification, Persona/Driver Split & `OpenAiAdapter` Hardening

### Objective
1. Widen job prompt/description columns to `text` via EF Core migration and drop unused legacy `job_messages` code/tables while preserving `session_messages`[cite: 4, 6].
2. Separate `PersonaId` (e.g., `rook`) from `DriverId` (e.g., `spence-ai`) for accurate attribution.
3. Route `post_job_message` to `JobTimelineCard` + SignalR (`ReceiveTimelineCardUpdated`) and remove dead Dashboard flat-chat components.
4. Update `OpenAiAdapter` for unclamped 512k (`524288`) context, 75% percentage-based compaction, and a `120s` post-first-byte SSE chunk idle watchdog[cite: 7].

### Required Changes
1. **Database Schema & Cleanup (`AgentMesh.Core`, `AgentMesh.Infrastructure`)**:
   - Create an EF Core migration widening `agent_jobs.message_text`, `agent_jobs.initial_prompt`, and `project_jobs.description` to unbounded `text`.
   - Drop any unused `job_messages` table while keeping `public.session_messages` and `public.job_workspace_states` completely untouched[cite: 4, 6].
   - Remove legacy `JobMessage` backend models, repositories, and controllers superseded by `JobTimelineCard` / `JobCardComment`.
2. **Driver vs. Persona Identity Separation (`AgentMesh.Api` & `AgentMesh.Cli`)**:
   - Audit session creation, `PlatformCallContext`, and `PlatformCallContextResolver` so `AgentSession` and `PlatformCallContext` carry both `DriverId` (execution harness/model runner) and `PersonaId` (role identity).
   - Attribute UI timeline cards, card comments, and task notes to `PersonaId`, while keeping `DriverId` in execution telemetry.
3. **Timeline Card Projection (`src/AgentMesh.Api/Tools/PlatformMcpTools.cs` & `HttpAgentTurnRunner.cs`)**:
   - Update `post_job_message` to persist a `JobTimelineCard` in `job_timeline_cards` and broadcast `ReceiveTimelineCardUpdated` to the job's SignalR group.
   - Map `requires_reply == true` to `TimelineCardType.Escalation3R` + `TimelineCardStatus.PendingInput` (and transition session status to `waiting_for_input` so Temporal pauses cleanly).
   - Map `requires_reply == false` to `TimelineCardType.TaskStep` (or `TimelineCardType.InternalConsultation` when targeting a peer persona) with `TimelineCardStatus.Active`.
   - Cut over `HttpAgentTurnRunner` and PR linkage lookups to `JobTimelineCard` while keeping `open_pull_request` working until Part 5 replaces it.
4. **Dashboard Flat-Chat Cleanup (`src/AgentMesh.Dashboard`)**:
   - Remove dead flat-chat components, legacy `JobMessage` state, and unused chat API client methods superseded by `JobTimelineCard` / `JobCardComment`.
5. **`OpenAiAdapter` 512k Context, 75% Compaction & Streaming Watchdog (`src/AgentMesh.Cli/Adapters/OpenAiAdapter.cs` & `OpenAISettings.cs`)**:
   - Respect `524288` (512k) context when configured on the agent/driver profile without clamping to `131072`[cite: 7].
   - Trigger history compaction dynamically at `75%` of the resolved context window (configurable via `CompactionThresholdPercent`), retaining emergency compaction fallback on HTTP 400 or stream drop. Do **not** cap reasoning/thinking tokens.
   - Rely on the outer HTTP timeout (`timeout`, e.g., `600s`) for initial connection and prompt prefill (TTFT)[cite: 7]. Once the **first SSE data chunk** arrives, enforce a linked per-chunk idle timeout (`120s` default, configurable via `StreamChunkIdleTimeoutSeconds`) that cancels and retries stalled streams.

### Verification
- `dotnet test AgentMesh.slnx --configuration Release --filter "FullyQualifiedName~Schema|FullyQualifiedName~Migration|FullyQualifiedName~Timeline|FullyQualifiedName~PlatformMcpTools|FullyQualifiedName~OpenAiAdapter|FullyQualifiedName~ModelContext"`
- `cd src/AgentMesh.Dashboard && npm test -- --run && npm run build`

---

## Part 2: Fix `CopilotAdapter` Raw JSONL Fallback `400 (Bad Request)` & Duplicate Tool Events

### Root Cause
In `src/AgentMesh.Cli/Adapters/AgentAdapters.cs` inside `CopilotAdapter.RunCopilotSubprocessAsync` (`--output-format json --stream on`)[cite: 4]:
1. `streamedResponseBuilder` only accumulates `assistant.message` text[cite: 4]. When a Copilot CLI session performs only tool calls + `task_complete` with no plain-text `assistant.message`, `streamedResponseBuilder` is empty when `/usr/bin/copilot` exits[cite: 4].
2. `CopilotAdapter` falls back to `rawStdOut` (`output = !string.IsNullOrWhiteSpace(rawStdOut) ? rawStdOut : stdErr;`), sending the multi-megabyte raw JSONL event stream (e.g., 2.8+ MB `events.jsonl`) to `POST /api/sessions/{id}/events` (`MeshClient.PostTrajectoryEventAsync`), causing `AgentMesh.Api` to reject it with `HTTP 400 (Bad Request)`[cite: 4].
3. In `TryParseCopilotJsonLine`:
   - `session.task_complete` / `task_complete` events are not parsed for their `summary` field[cite: 4].
   - Both `tool.execution_start` and `tool.execution_complete` match the `tool_call` branch (`lower.Contains("tool") && (lower.Contains("call") || lower.Contains("execution") || lower.Contains("start") || lower.Contains("use"))`) because `tool.execution_complete` contains `"execution"`, emitting duplicate `tool_call` events instead of `tool_result`[cite: 4].
4. In `src/AgentMesh.Cli/Services/MeshClient.cs`, `EnsureSuccessStatusCode()` hides the response body on non-2xx responses, making `400` errors opaque in the timeline[cite: 4].

### Required Changes
1. **`src/AgentMesh.Cli/Adapters/AgentAdapters.cs` (`CopilotAdapter`)**:
   - **Capture `task_complete` summary as fallback text**:
     - Track `string? taskCompleteSummary = null;` in `RunCopilotSubprocessAsync`.
     - In `TryParseCopilotJsonLine`, recognize `session.task_complete` / `task_complete` (and `tool.execution_complete` for `task_complete`) and extract `data.summary` (or `result.content` / `arguments.summary`)[cite: 4].
     - When `streamedResponseBuilder` is empty on process exit, use `taskCompleteSummary` before falling back to raw output[cite: 4].
   - **Never send unbounded `rawStdOut` to `POST /api/sessions/{id}/events`**:
     - If both `streamedResponseBuilder` and `taskCompleteSummary` are empty, extract only non-JSON plain-text lines or cap the raw fallback (`rawStdOut` / `stdErr`) to a bounded tail preview (`<= 16,384` characters, keeping the tail where exit errors appear) so `TrajectoryEventPayload.Content` never exceeds API body limits[cite: 4].
   - **Fix `TryParseCopilotJsonLine` event classification order**:
     - Evaluate `tool_result` (`result` / `output` / `finish` / `done` / `complete`) **before** `tool_call` (`call` / `execution` / `start` / `use`) so `tool.execution_complete` maps to `tool_result` rather than emitting a second `tool_call`[cite: 4].
2. **`src/AgentMesh.Cli/Services/MeshClient.cs`**:
   - On non-2xx responses in `PostTrajectoryEventAsync` (and adjacent event/upsert calls), read and include a truncated snippet (`<= 500` chars) of the HTTP response body in the thrown exception message alongside the status code[cite: 4].
3. **Constraints**:
   - Do not alter `AgyAdapter` or the `OpenAiAdapter` behavior established in Part 1.

### Verification
- Add unit tests in `tests/AgentMesh.Tests/Cli/` covering:
  1. Tool-only Copilot JSONL stream ending with `session.task_complete` (`summary`) + zero `assistant.message` text produces the `task_complete` summary instead of multi-MB `rawStdOut`[cite: 4].
  2. Large `rawStdOut` fallback (>100 KB of JSONL) with no `assistant.message` or `task_complete` is bounded to `<= 16 KB`[cite: 4].
  3. `tool.execution_start` maps to `tool_call` and `tool.execution_complete` maps to `tool_result`[cite: 4].
- Run `dotnet test AgentMesh.slnx --configuration Release --filter "FullyQualifiedName~Copilot|FullyQualifiedName~AgentAdapter"`.

---

## Part 3: Relax Task Decomposition Ceiling to a Guideline (`PlatformPrompts.cs` & `PlatformMcpTools.cs`)

### Objective
Convert the rigid `3–6 tasks` breakdown rule into a flexible guideline so agents decompose jobs into whatever number of atomic tasks fits the actual scope.

### Required Changes
1. **Layer 1 Platform Invariants (`src/AgentMesh.Worker/Prompts/PlatformPrompts.cs`)**:
   - Update `PlatformPrompts.PlatformInvariants` so task breakdown size is phrased as a flexible guideline (*"Decompose the objective into atomic, verifiable tasks sized to the scope of the work—typically 3–6 tasks for standard features, or more/fewer as needed so each task remains focused and independently testable"*) rather than a rigid 3–6 constraint.
2. **`manage_task` Validation (`src/AgentMesh.Api/Tools/PlatformMcpTools.cs`)**:
   - Remove any hard `< 3` or `> 6` task rejection in `manage_task` (`action: "set_checklist"`), keeping only a generous sanity bound (e.g., `1..25` tasks).

### Verification
- `dotnet test AgentMesh.slnx --configuration Release --filter "FullyQualifiedName~ManageTask|FullyQualifiedName~PlatformPrompts"`

---

## Part 4: Multi-Project Workspace Layout (`/workspace/repos/<project>`) & Persistent Memory Wiring

### Objective
Support multi-project persona containers under `/workspace/repos/<project-name>`, default CLI tool execution to the active project directory, and verify that persistent memories (`agent_memories`) are retrieved into the prompt stack and actively used by personas to track environment and branch state[cite: 4].

### Required Changes
1. **Multi-Project Working Directory (`AgentMesh.Core`, `AgentMesh.Infrastructure`, `AgentMesh.Cli`)**:
   - Ensure `Project` carries `RepositoryName` and `WorkingDirectory` defaulting to `/workspace/repos/{RepositoryName}` via `GetResolvedWorkingDirectory()` (migrating away from hardcoded `/workspace/repo` assumptions)[cite: 4].
   - Update CLI tool execution (`run_command`, `read_file`, `edit_file`, `write_file`) so relative paths and shell `WorkingDirectory` default to the active session's resolved project directory (`/workspace/repos/{RepositoryName}`) rather than `/workspace`[cite: 4, 6].
2. **Audit & Wire Persistent Memory Retrieval (`PromptStackBuilder` / `ContextActivities`)**:
   - Inspect `PromptStackBuilder` (`src/AgentMesh.Worker/Services/PromptStackBuilder.cs`) and `AgentMemoryRepository` to ensure `GetMemoriesForAgentAsync(personaId, projectId)` queries both project-scoped (`projectId`) and global (`"*"`) memories for the active **PersonaId** (not DriverId), ordered by `UpdatedAt DESC`, and injects them into Layer 5 (`[PERSISTENT MEMORY & ENVIRONMENT NOTES]`) on every turn and post-compaction recovery.
   - Expose a `search_memory` / `list_memories` read tool (or include existing memory keys in the Turn-0 envelope) so personas can inspect or update stale environment entries without duplicating keys.
3. **Layer 1 Memory & Workspace Directives (`PlatformPrompts.cs`)**:
   - Instruct personas in Layer 1:
     - Your container is isolated to you; inspect `/workspace/repos/<project-name>` and check your injected `[PERSISTENT MEMORY]` block before probing[cite: 4].
     - Whenever you discover environment-specific paths, SDK flags, auth wiring, or project conventions—or switch/create branches—immediately call `save_memory` (`category: "environment"`) so state persists across compactions and future sessions.
     - After any context compaction or container resume, run `git branch --show-current && git status --short` before making edits.

### Verification
- `dotnet test AgentMesh.slnx --configuration Release --filter "FullyQualifiedName~PromptStack|FullyQualifiedName~Memory|FullyQualifiedName~Workspace"`

---

## Part 5: Persona-Owned Git & Standardized CLI PR Harness (`agentmesh pr`)

### Objective
Move git operations and PR creation out of server-side simulated paths and into the **persona container** (which holds the persona's GitHub credentials), providing a standardized CLI harness command so personas consistently sync `main`, push, and open/merge real PRs.

### Required Changes
1. **Standardized CLI PR Harness (`src/AgentMesh.Cli/Commands/PrCommand.cs`)**:
   - Add `agentmesh pr open`, `agentmesh pr sync`, and `agentmesh pr merge` subcommands to the CLI binary:
     - **`agentmesh pr sync`**:
       1. Fetches `origin/<baseBranch>` (default `main`) and runs `git merge --no-edit origin/<baseBranch>`.
       2. If conflicts occur, captures `git diff --name-only --diff-filter=U`, cleans up or emits structured diagnostics (`MERGE_CONFLICT: <files>` with a distinct non-zero exit code), and instructs the persona to resolve the conflicts.
     - **`agentmesh pr open`**:
       1. Verifies working tree is clean (`git status --porcelain` is empty); fails fast with an actionable message if uncommitted changes exist.
       2. Runs the `sync` step (`git merge --no-edit origin/<baseBranch>`) **before** pushing; halts with `MERGE_CONFLICT` if conflicts occur.
       3. Pushes the current head branch to `origin` (`git push -u origin <headBranch>`) using the container's `GITHUB_TOKEN` / `GH_TOKEN`.
       4. Checks if an open PR already exists for `<headBranch>` via `gh pr list --head <headBranch>`; if so, updates it or returns the existing PR URL/number idempotently. Otherwise creates a real PR via `gh pr create` formatted with the **3-R template** (`Result`, `Reasoning`, `Risk`) and requests review from the configured reviewer.
       5. Calls the AgentMesh API to record the real PR number, URL, and current `HEAD` SHA on the job and emit/update the `PrReview` timeline card.
     - **`agentmesh pr merge`**:
       1. Verifies the current `HEAD` SHA matches the approved SHA (failing closed with `sha_moved` if new commits were pushed after approval) and executes `gh pr merge` (squash/merge per project setting).
2. **Retire Perpetual Simulated PR #999 Path (`GitHubPullRequestService.cs` & `PlatformMcpTools.cs`)**:
   - Remove the `SimulatedPrNumber = 999` placeholder path so fabricated PRs can never pollute job state.
   - Update `open_pull_request` MCP tool to register real PR metadata (`pr_url`, `pr_number`, `head_sha`) from `agentmesh pr open`, or return an instructional error directing the persona to run `agentmesh pr open` when invoked without a real PR.
3. **Rebuild CLI Binary**:
   - Republish `dist/cli/linux-x64/agentmesh` (`dotnet publish src/AgentMesh.Cli/AgentMesh.Cli.csproj -c Release -r linux-x64 --self-contained -o dist/cli/linux-x64`).

### Verification
- `dotnet test AgentMesh.slnx --configuration Release --filter "FullyQualifiedName~GitHubPullRequest|FullyQualifiedName~PrCommand"`

---

## Part 6: Job-Level PR & Unbounded Review Loop (`JobExecutionWorkflow`)

### Objective
Complete the remaining parts of the job-level PR & review loop redesign (building on the `merge_conflict` vs. `sha_moved` classification already landed in `9b08ea6`):
- Tasks execute sequentially on the job branch **without per-task PRs**.
- A single PR is opened at the **job level** once all checklist tasks complete.
- Review rejections, PR comments, and merge conflicts (`merge_conflict`) generate new remediation tasks and loop back into execution for **unbounded rounds** until approved and merged.

### Required Changes
1. **Task Loop Without Per-Task PRs (`JobExecutionWorkflow.cs` & `TaskExecutionWorkflow.cs`)**:
   - Remove per-task PR creation from `TaskExecutionWorkflow`; child task workflows only implement, verify, and commit to the job branch.
   - Once all checklist tasks reach `completed`, `JobExecutionWorkflow` transitions to the **Job PR & Review Stage**.
2. **Job-Level PR Gate, Two-Man Rule & Unbounded Review/Conflict Remediation Loop (`JobExecutionWorkflow.cs` & Review API)**:
   - Instruct/allow the persona to sync `main` and open the job-level PR via `agentmesh pr open`.
   - Enforce the **Two-Man Rule** on `POST /api/jobs/{jobId}/reviews` (reject self-approval if the reviewer matches the authoring persona).
   - Wait on external PR review signals (`Approved` vs. `ChangesRequested`).
   - **On `ChangesRequested` (or `merge_conflict`)**:
     - Convert review comments or conflicted files into new checklist tasks on the job, re-enter the task execution loop so the persona resolves them and pushes updates to the PR branch, and re-request review.
     - Support **unbounded review rounds** (using `Workflow.ContinueAsNew` when Temporal event history grows large across rounds).
   - **On `Approved`**:
     - Execute the merge (via `agentmesh pr merge` or `GitActivities.MergeJobPullRequestAsync` passing the approved `HEAD` SHA) using the `merge_conflict` vs. `sha_moved` guard from `9b08ea6`:
       - `merge_conflict` $\rightarrow$ route back into the task loop as a base-merge remediation task.
       - `sha_moved` $\rightarrow$ fail closed back to the reviewer gate for re-approval.
       - Merged $\rightarrow$ mark the job `Completed`.

### Final Verification
- `dotnet build AgentMesh.slnx -c Release` (0 errors, 0 new warnings)
- `dotnet test AgentMesh.slnx --configuration Release` (full suite passes green)
- `cd src/AgentMesh.Dashboard && npm test -- --run && npm run build`
- Confirm `test -x dist/cli/linux-x64/agentmesh`
