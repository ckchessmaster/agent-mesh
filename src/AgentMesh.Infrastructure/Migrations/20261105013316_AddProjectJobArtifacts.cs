using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-written as guarded DDL rather than left in <c>AddColumn</c> form, for the same reason
    /// <see cref="AddProjectDocumentsTable"/> is: this project applies migrations from more than one
    /// entry point (<c>Program.cs</c> calls <c>MigrateAsync()</c> at startup, and an operator may
    /// re-apply a migration against a database a failed startup left half-built), and an unguarded
    /// <c>ALTER TABLE ... ADD COLUMN</c> aborts the whole migration transaction in that case.
    /// <para>
    /// The DDL below is what <c>dotnet ef migrations add AddProjectJobArtifacts</c> generated for this
    /// model — column type, nullability and default — with only the guard added, so
    /// <c>AgentMeshDbContextModelSnapshot</c> stays accurate and the next scaffolded migration diffs
    /// against this column normally. The <c>Designer.cs</c> beside this file is the generated one and was
    /// not edited.
    /// </para>
    /// <para>
    /// The default is <c>'[]'</c> rather than <c>'{}'</c> because a job manifest is a JSON array of
    /// <see cref="AgentMesh.Core.Models.JobArtifactDto"/>. Existing rows get <c>[]</c> — "no artifacts
    /// recorded" — which is what the reader returns for an empty manifest anyway, so no backfill pass is
    /// needed for correctness; the read path still derives artifacts from the job's turn sessions for
    /// rows whose files predate this column.
    /// </para>
    /// </remarks>
    public partial class AddProjectJobArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A job's artifact index: the mirror of whatever its turn sessions produced, so the Cockpit
            // can render an Artifacts panel for a job the way it does for a session. See
            // AgentMesh.Core.Models.ProjectJob.ArtifactsJson for why this is a column and not a table or
            // a per-read derivation.
            migrationBuilder.Sql("""
                ALTER TABLE project_jobs
                    ADD COLUMN IF NOT EXISTS artifacts jsonb NOT NULL DEFAULT '[]'::jsonb;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // IF EXISTS keeps a re-run of a partial rollback from failing for the same reason the Up
            // guard exists. The bytes themselves are never in this column — they stay in the session
            // artifact directories and in S3 — so dropping it loses the index, not the files.
            migrationBuilder.Sql("""
                ALTER TABLE project_jobs
                    DROP COLUMN IF EXISTS artifacts;
                """);
        }
    }
}
