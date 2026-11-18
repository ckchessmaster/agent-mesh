using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-written as guarded idempotent DDL so startup <c>MigrateAsync()</c> and manual reruns
    /// remain safe against a partially migrated database.
    /// </remarks>
    public partial class AddMcpServersAndPersonaMcpServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS mcp_servers (
                    id character varying(64) NOT NULL,
                    name character varying(128) NOT NULL,
                    description character varying(1024) NOT NULL DEFAULT '',
                    transport character varying(16) NOT NULL DEFAULT 'Stdio',
                    command character varying(512) NULL,
                    args jsonb NOT NULL DEFAULT '[]',
                    url character varying(1024) NULL,
                    env_encrypted text NULL,
                    headers_encrypted text NULL,
                    enabled boolean NOT NULL DEFAULT true,
                    timeout_seconds integer NOT NULL DEFAULT 30,
                    created_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    CONSTRAINT pk_mcp_servers PRIMARY KEY (id)
                );
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_mcp_servers_name
                    ON mcp_servers (name);
                """);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS persona_mcp_servers (
                    persona_id character varying(64) NOT NULL,
                    mcp_server_id character varying(64) NOT NULL,
                    enabled boolean NOT NULL DEFAULT true,
                    env_overrides_encrypted text NULL,
                    headers_overrides_encrypted text NULL,
                    allowed_tools jsonb NULL,
                    blocked_tools jsonb NULL,
                    assigned_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    CONSTRAINT pk_persona_mcp_servers PRIMARY KEY (persona_id, mcp_server_id),
                    CONSTRAINT fk_persona_mcp_servers_personas_persona_id FOREIGN KEY (persona_id) REFERENCES personas (id) ON DELETE CASCADE,
                    CONSTRAINT fk_persona_mcp_servers_mcp_servers_mcp_server_id FOREIGN KEY (mcp_server_id) REFERENCES mcp_servers (id) ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_persona_mcp_servers_persona_id
                    ON persona_mcp_servers (persona_id);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_persona_mcp_servers_mcp_server_id
                    ON persona_mcp_servers (mcp_server_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS persona_mcp_servers;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS mcp_servers;");
        }
    }
}
