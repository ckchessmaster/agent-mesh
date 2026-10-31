using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Hand-written as guarded DDL, following the convention established by
    /// <c>20261030051800_AddAgentMemoriesTable</c>: this project applies migrations both from
    /// <c>MigrateAsync()</c> at startup and by an operator against a database a previous attempt left
    /// half-built. Unguarded <c>ALTER TABLE ADD COLUMN</c> aborts the whole migration transaction in
    /// that case, turning a recoverable state into one that needs <c>__EFMigrationsHistory</c> edited
    /// to escape. The DDL below is what EF generated for this model — same column types, same
    /// defaults, same index definitions — with guards added, so the model snapshot stays accurate and
    /// the next <c>dotnet ef migrations add</c> diffs against these columns normally.
    /// </para>
    /// <para>
    /// <strong>Two deliberate deviations from the job spec's SQL sketch</strong>, both of which change
    /// behaviour rather than cosmetics, are documented at the point they occur:
    /// <list type="number">
    /// <item>
    /// <c>working_directory</c> defaults to <c>''</c>, not to <c>'/workspace/repos/agent-mesh'</c>.
    /// </item>
    /// <item>
    /// <c>job_workspace_states</c> is re-keyed to <c>(job_id, task_id)</c> instead of staying
    /// job-scoped, which is what makes a per-task checkpoint representable at all.
    /// </item>
    /// </list>
    /// </para>
    /// </remarks>
    public partial class AddMultiRepoPathsAndTaskCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ──────────────────────────────────────────────────────────────────────
            // 1. projects: multi-repo working directory
            // ──────────────────────────────────────────────────────────────────────

            // The {repo_name} in /workspace/repos/{repo_name}. Existing rows get 'agent-mesh',
            // which is the repository every one of them has always meant, so no project's resolved
            // path changes as a result of this column being added.
            migrationBuilder.Sql("""
                ALTER TABLE public.projects
                    ADD COLUMN IF NOT EXISTS repository_name VARCHAR(128) NOT NULL DEFAULT 'agent-mesh';
                """);

            // Deviation (1): the spec sketches this as
            //   working_directory VARCHAR(256) NOT NULL DEFAULT '/workspace/repos/agent-mesh'
            // and that default is a live footgun. EF materializes column defaults into new rows, so
            // a project created as repository_name='payments-api' with no explicit working directory
            // would be born pointing at /workspace/repos/agent-mesh — a directory that exists, is
            // writable, and belongs to a different repository. Every command would succeed against
            // the wrong tree and nothing would ever report an error; the agent would simply commit
            // payments work onto agent-mesh's checkout.
            //
            // Empty string means "derive it from repository_name", which Project resolves via
            // WorkspacePathResolver. Legacy rows are backfilled below to the spec's exact literal, so
            // the spec's stated outcome (existing projects resolve to /workspace/repos/agent-mesh)
            // holds, while a new project with a new name resolves to its own directory.
            migrationBuilder.Sql("""
                ALTER TABLE public.projects
                    ADD COLUMN IF NOT EXISTS working_directory VARCHAR(256) NOT NULL DEFAULT '';
                """);

            // The backfill, and it is conditional for a reason beyond idempotency. Only rows that
            // still carry the default repository name are touched: a row whose working_directory is
            // empty because an operator deliberately cleared it on a *custom* repository must fall
            // through to the derivation, not be pinned to agent-mesh's path.
            migrationBuilder.Sql("""
                UPDATE public.projects
                    SET working_directory = '/workspace/repos/agent-mesh'
                    WHERE working_directory = ''
                      AND repository_name = 'agent-mesh';
                """);

            // ──────────────────────────────────────────────────────────────────────
            // 2. job_workspace_states: task scoping + working checkpoint
            // ──────────────────────────────────────────────────────────────────────

            // Deviation (2): the primary key moves from (job_id) to (job_id, task_id).
            //
            // Why the key and not a side table: the checkpoint describes the same working tree,
            // observed at the same instant, as head_commit/content_hash on this row. A separate
            // table would let a save update one and lose the other, producing a checkpoint that
            // confidently names a commit the snapshot does not cover — the exact class of stale-state
            // lie this feature exists to eliminate.
            //
            // The sequence matters. task_id is added NOT NULL with a sentinel default, which Postgres
            // fills onto existing rows; only then can the composite PK be built, because a PK cannot
            // contain nulls. Adding the column after dropping the PK would work equally well, but
            // dropping the PK first leaves a window in which a concurrent writer could insert a
            // duplicate job_id row that the new PK would then reject, failing this migration.
            migrationBuilder.Sql("""
                ALTER TABLE public.job_workspace_states
                    ADD COLUMN IF NOT EXISTS task_id VARCHAR(64) NOT NULL DEFAULT '_unassigned';
                """);

            // '_unassigned' rather than NULL or '': a PK column cannot be null, and a real task id is
            // a GUID, so no caller can ever pass a string that collides with the sentinel. Rows this
            // migration converts are job-level state that was never about a checklist item, which is
            // precisely what the sentinel means; JobWorkspaceService resolves a job's *active* task
            // row at read time and treats the sentinel row as the job-level fallback.
            migrationBuilder.Sql("""
                ALTER TABLE public.job_workspace_states
                    DROP CONSTRAINT IF EXISTS pk_job_workspace_states;
                """);

            // IF NOT EXISTS on the PK is not paranoia: if a previous attempt of this migration
            // committed the AddPrimaryKey and then failed on the index below, the retry must not die
            // on "constraint already exists". Postgres has no IF NOT EXISTS for ADD CONSTRAINT, so
            // the existence check is done through the catalog instead.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = 'pk_job_workspace_states'
                          AND conrelid = 'public.job_workspace_states'::regclass
                    ) THEN
                        ALTER TABLE public.job_workspace_states
                            ADD CONSTRAINT pk_job_workspace_states PRIMARY KEY (job_id, task_id);
                    END IF;
                END $$;
                """);

            // The checkpoint columns. All nullable: a row created by a lease acquisition has no
            // checkpoint, and JobWorkspaceState.HasCheckpoint keys off checkpoint_updated_at being
            // set, so "no checkpoint" must be representable as all-null rather than as an empty row.
            migrationBuilder.Sql("""
                ALTER TABLE public.job_workspace_states
                    ADD COLUMN IF NOT EXISTS checkpoint_branch VARCHAR(128),
                    ADD COLUMN IF NOT EXISTS checkpoint_status VARCHAR(32),
                    ADD COLUMN IF NOT EXISTS checkpoint_summary VARCHAR(4096),
                    ADD COLUMN IF NOT EXISTS checkpoint_modified_files_json JSONB,
                    ADD COLUMN IF NOT EXISTS checkpoint_active_stash VARCHAR(512),
                    ADD COLUMN IF NOT EXISTS checkpoint_head_commit VARCHAR(64),
                    ADD COLUMN IF NOT EXISTS checkpoint_focus VARCHAR(256),
                    ADD COLUMN IF NOT EXISTS checkpoint_turn_session_id UUID,
                    ADD COLUMN IF NOT EXISTS checkpoint_updated_at TIMESTAMPTZ;
                """);

            // checkpoint_modified_files_json is jsonb and deliberately nullable rather than
            // NOT NULL DEFAULT '[]'. The two states mean different things to the recovery note:
            // NULL is "we never inspected the tree" and renders no claim; '[]' is "we inspected it
            // and it was clean". Defaulting to '[]' would let a checkpoint written by a lease-only
            // path assert that the working tree is clean, and an agent that believes that will run
            // `git checkout .` on work that exists only in its stash.

            // "Which task was this agent last on" — the query a restarted runner makes when it knows
            // its job id but not its task id. Descending on updated_at because the caller wants the
            // newest row and must not sort the whole job's history to get it.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_job_workspace_states_checkpoint_recency
                    ON public.job_workspace_states (job_id, checkpoint_updated_at DESC);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS public.ix_job_workspace_states_checkpoint_recency;");

            migrationBuilder.Sql("""
                ALTER TABLE public.job_workspace_states
                    DROP COLUMN IF EXISTS checkpoint_branch,
                    DROP COLUMN IF EXISTS checkpoint_status,
                    DROP COLUMN IF EXISTS checkpoint_summary,
                    DROP COLUMN IF EXISTS checkpoint_modified_files_json,
                    DROP COLUMN IF EXISTS checkpoint_active_stash,
                    DROP COLUMN IF EXISTS checkpoint_head_commit,
                    DROP COLUMN IF EXISTS checkpoint_focus,
                    DROP COLUMN IF EXISTS checkpoint_turn_session_id,
                    DROP COLUMN IF EXISTS checkpoint_updated_at;
                """);

            // Restoring the single-column PK is only sound if no job actually acquired a second task
            // row. If it did, the composite key is load-bearing and a blind downgrade would silently
            // pick one task's state to represent the whole job — so this refuses rather than guesses.
            // Dropping the columns first means the guard runs against the task rows that still exist.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    duplicated integer;
                BEGIN
                    SELECT COUNT(*) INTO duplicated FROM (
                        SELECT job_id FROM public.job_workspace_states
                        GROUP BY job_id HAVING COUNT(*) > 1
                    ) AS d;

                    IF duplicated > 0 THEN
                        RAISE EXCEPTION
                            'cannot downgrade: % job(s) hold more than one task row. Collapse them onto '
                            'task_id = ''_unassigned'' first, or leave the composite key in place.', duplicated;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE public.job_workspace_states
                    DROP CONSTRAINT IF EXISTS pk_job_workspace_states;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE public.job_workspace_states
                    DROP COLUMN IF EXISTS task_id;
                """);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = 'pk_job_workspace_states'
                          AND conrelid = 'public.job_workspace_states'::regclass
                    ) THEN
                        ALTER TABLE public.job_workspace_states
                            ADD CONSTRAINT pk_job_workspace_states PRIMARY KEY (job_id);
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE public.projects
                    DROP COLUMN IF EXISTS working_directory,
                    DROP COLUMN IF EXISTS repository_name;
                """);
        }
    }
}
