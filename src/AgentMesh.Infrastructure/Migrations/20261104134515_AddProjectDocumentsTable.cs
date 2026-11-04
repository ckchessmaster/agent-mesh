using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-written as guarded DDL rather than left in <c>CreateTable</c> form, for the same reason
    /// <see cref="AddAgentMemoriesTable"/> is: this project applies migrations from more than one entry
    /// point (<c>Program.cs</c> calls <c>MigrateAsync()</c> at startup, and an operator may re-apply a
    /// migration against a database a failed startup left half-built), and an unguarded
    /// <c>CREATE TABLE</c> aborts the whole migration transaction in that case.
    /// <para>
    /// The DDL below is what <c>dotnet ef migrations add AddProjectDocumentsTable</c> generated for this
    /// model — column types, lengths, defaults, and the index definition — with only the guards added, so
    /// <c>AgentMeshDbContextModelSnapshot</c> stays accurate and the next scaffolded migration diffs
    /// against this table normally. The <c>Designer.cs</c> beside this file is the generated one and was
    /// not edited.
    /// </para>
    /// </remarks>
    public partial class AddProjectDocumentsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A project's supporting-document library: pasted text or an uploaded file, addressable by
            // id, deleted one document at a time. See AgentMesh.Core.Models.ProjectDocument for why this
            // is a table rather than a jsonb column on projects.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS project_documents (
                    id uuid NOT NULL DEFAULT gen_random_uuid(),
                    project_id character varying(64) NOT NULL,
                    title character varying(255) NOT NULL,
                    kind integer NOT NULL DEFAULT 0,
                    content text NOT NULL DEFAULT '',
                    file_name character varying(255) NULL,
                    content_type character varying(128) NULL,
                    size_bytes bigint NOT NULL DEFAULT 0,
                    s3key character varying(512) NULL,
                    created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    CONSTRAINT pk_project_documents PRIMARY KEY (id)
                );
                """);

            // The library read: WHERE project_id = @x ORDER BY title. Every read of this table is one
            // project's whole set — the list endpoint, the count that enforces MaxDocumentsPerProject,
            // and the prompt index, which orders by title so its output is byte-stable across turns.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_project_documents_project_title
                    ON project_documents (project_id, title);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table takes its index with it; IF EXISTS keeps a re-run of a partial rollback
            // from failing for the same reason the Up guard exists. The bytes on disk and in S3 are not
            // touched: they live under ~/.agentmesh/artifacts/projects/{id}/documents, outside the
            // database, and a rollback that deleted user uploads would be the worse failure.
            migrationBuilder.Sql("DROP TABLE IF EXISTS project_documents;");
        }
    }
}
