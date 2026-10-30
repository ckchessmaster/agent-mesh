using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-written as guarded DDL rather than left in <c>CreateTable</c> form, because this project
    /// applies migrations from more than one entry point: <c>Program.cs</c> calls <c>MigrateAsync()</c>
    /// at startup, and an operator may also apply the same migration by hand against a database that a
    /// previous attempt left half-built (a failed startup rollback, or a restore taken mid-migration).
    /// <c>CREATE TABLE</c> without a guard aborts the whole migration transaction in that case, which
    /// turns a recoverable state into one that requires editing <c>__EFMigrationsHistory</c> to escape.
    /// <para>
    /// The DDL below is byte-for-byte what EF generated for this model (column types, defaults, index
    /// definitions), so <c>AgentMeshDbContextModelSnapshot</c> stays accurate and the next
    /// <c>dotnet ef migrations add</c> diffs against this table normally. Only the guards are added.
    /// </para>
    /// </remarks>
    public partial class AddAgentMemoriesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Durable agent lessons: (agent, project, key) -> a value a future session is told about.
            // `key` is a PostgreSQL-reserved-ish word but needs no quoting; EF quotes it the same way.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS agent_memories (
                    id uuid NOT NULL DEFAULT gen_random_uuid(),
                    agent_id character varying(64) NOT NULL,
                    project_id character varying(64) NOT NULL DEFAULT '*',
                    category character varying(32) NOT NULL,
                    "key" character varying(128) NOT NULL,
                    value text NOT NULL,
                    confidence_score double precision NOT NULL DEFAULT 1.0,
                    created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    CONSTRAINT pk_agent_memories PRIMARY KEY (id)
                );
                """);

            // The upsert identity. A duplicate (agent, project, key) is what the repository's retry path
            // catches, so this index is the concurrency contract, not just a nicety.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS uq_agent_project_key
                    ON agent_memories (agent_id, project_id, "key");
                """);

            // The read path: WHERE agent_id = @x AND (project_id = @y OR project_id = '*'). The OR keeps
            // PostgreSQL from seeking the unique index's project_id column, so this narrower index is
            // what the prompt-assembly sweep actually uses.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS idx_agent_memories_lookup
                    ON agent_memories (agent_id, project_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table takes both indexes with it; IF EXISTS keeps a re-run of a partial
            // rollback from failing for the same reason the Up guard exists.
            migrationBuilder.Sql("DROP TABLE IF EXISTS agent_memories;");
        }
    }
}
