using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Data.Configurations;
using AgentMesh.Infrastructure.Migrations;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace AgentMesh.Tests.Data;

public class McpServerSchemaTests
{
    private const string MigrationId = "20261118143000_AddMcpServersAndPersonaMcpServers";

    private static IEntityType McpServerEntity()
    {
        using var db = TestDbContextFactory.Postgres();
        var entity = db.Model.FindEntityType(typeof(McpServer));
        entity.Should().NotBeNull();
        return entity!;
    }

    private static IEntityType PersonaMcpServerEntity()
    {
        using var db = TestDbContextFactory.Postgres();
        var entity = db.Model.FindEntityType(typeof(PersonaMcpServer));
        entity.Should().NotBeNull();
        return entity!;
    }

    private static IProperty Column(IEntityType entity, string columnName)
    {
        var prop = entity.GetProperties().FirstOrDefault(p =>
            string.Equals(p.GetColumnName(), columnName, StringComparison.Ordinal));
        prop.Should().NotBeNull($"'{columnName}' must be mapped on {entity.GetTableName()}");
        return prop!;
    }

    [Fact]
    public void McpServerModels_AreMappedOnDbContext()
    {
        using var db = TestDbContextFactory.Create();
        db.McpServers.Should().NotBeNull();
        db.PersonaMcpServers.Should().NotBeNull();
        db.Model.FindEntityType(typeof(McpServer)).Should().NotBeNull();
        db.Model.FindEntityType(typeof(PersonaMcpServer)).Should().NotBeNull();
    }

    [Fact]
    public void McpServers_ColumnsAndTypesMatchSchema()
    {
        var entity = McpServerEntity();
        entity.GetTableName().Should().Be("mcp_servers");

        entity.GetProperties().Select(p => p.GetColumnName()).Should().BeEquivalentTo(
            "id", "name", "description", "transport", "command", "args",
            "url", "env_encrypted", "headers_encrypted", "enabled",
            "timeout_seconds", "created_at", "updated_at");

        Column(entity, "id").GetColumnType().Should().Be("character varying(64)");
        Column(entity, "name").GetColumnType().Should().Be("character varying(128)");
        Column(entity, "description").GetColumnType().Should().Be("character varying(1024)");
        Column(entity, "transport").GetColumnType().Should().Be("character varying(16)");
        Column(entity, "command").GetColumnType().Should().Be("character varying(512)");
        Column(entity, "args").GetColumnType().Should().Be("jsonb");
        Column(entity, "url").GetColumnType().Should().Be("character varying(1024)");
        Column(entity, "env_encrypted").GetColumnType().Should().Be("text");
        Column(entity, "headers_encrypted").GetColumnType().Should().Be("text");
        Column(entity, "enabled").GetColumnType().Should().Be("boolean");
        Column(entity, "timeout_seconds").GetColumnType().Should().Be("integer");
        Column(entity, "created_at").GetColumnType().Should().Be("timestamp with time zone");
        Column(entity, "updated_at").GetColumnType().Should().Be("timestamp with time zone");

        Column(entity, "command").IsColumnNullable().Should().BeTrue();
        Column(entity, "url").IsColumnNullable().Should().BeTrue();
        Column(entity, "env_encrypted").IsColumnNullable().Should().BeTrue();
        Column(entity, "headers_encrypted").IsColumnNullable().Should().BeTrue();
        Column(entity, "enabled").IsColumnNullable().Should().BeFalse();
    }

    [Fact]
    public void PersonaMcpServers_ColumnsKeyAndForeignKeysMatchSchema()
    {
        var entity = PersonaMcpServerEntity();
        entity.GetTableName().Should().Be("persona_mcp_servers");

        entity.GetProperties().Select(p => p.GetColumnName()).Should().BeEquivalentTo(
            "persona_id", "mcp_server_id", "enabled",
            "env_overrides_encrypted", "headers_overrides_encrypted",
            "allowed_tools", "blocked_tools", "assigned_at", "updated_at");

        Column(entity, "persona_id").GetColumnType().Should().Be("character varying(64)");
        Column(entity, "mcp_server_id").GetColumnType().Should().Be("character varying(64)");
        Column(entity, "enabled").GetColumnType().Should().Be("boolean");
        Column(entity, "env_overrides_encrypted").GetColumnType().Should().Be("text");
        Column(entity, "headers_overrides_encrypted").GetColumnType().Should().Be("text");
        Column(entity, "allowed_tools").GetColumnType().Should().Be("jsonb");
        Column(entity, "blocked_tools").GetColumnType().Should().Be("jsonb");
        Column(entity, "assigned_at").GetColumnType().Should().Be("timestamp with time zone");
        Column(entity, "updated_at").GetColumnType().Should().Be("timestamp with time zone");

        var pk = entity.FindPrimaryKey();
        pk.Should().NotBeNull();
        pk!.Properties.Select(p => p.GetColumnName()).Should().Equal("persona_id", "mcp_server_id");

        var fks = entity.GetForeignKeys().ToList();
        fks.Should().HaveCount(2);
        fks.Should().Contain(fk =>
            fk.PrincipalEntityType.ClrType == typeof(Persona) &&
            fk.DeleteBehavior == DeleteBehavior.Cascade);
        fks.Should().Contain(fk =>
            fk.PrincipalEntityType.ClrType == typeof(McpServer) &&
            fk.DeleteBehavior == DeleteBehavior.Cascade);
    }

    [Fact]
    public async Task EnabledFalse_IsPersistedExplicitlyWithoutSentinelLoss()
    {
        await using var db = TestDbContextFactory.Create();
        db.Personas.Add(new Persona
        {
            Id = "astra",
            Name = "Astra",
            DriverId = "agy",
            PersonaPrompt = "Prompt"
        });
        db.McpServers.Add(new McpServer
        {
            Id = "disabled-srv",
            Name = "Disabled Server",
            Transport = McpTransportType.Stdio,
            Command = "npx",
            Enabled = false
        });
        db.PersonaMcpServers.Add(new PersonaMcpServer
        {
            PersonaId = "astra",
            McpServerId = "disabled-srv",
            Enabled = false
        });

        await db.SaveChangesAsync();

        var reloadedServer = await db.McpServers.AsNoTracking().SingleAsync(m => m.Id == "disabled-srv");
        var reloadedLink = await db.PersonaMcpServers.AsNoTracking().SingleAsync(pm => pm.PersonaId == "astra");

        reloadedServer.Enabled.Should().BeFalse();
        reloadedLink.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Migration_IsIdempotentSql_AndMatchesModelColumns()
    {
        var upOps = Operations(up: true);
        var downOps = Operations(up: false);

        upOps.Should().OnlyContain(o => o is SqlOperation);
        downOps.Should().OnlyContain(o => o is SqlOperation);

        var upSql = string.Join('\n', upOps.OfType<SqlOperation>().Select(o => o.Sql));
        upSql.Should().Contain("CREATE TABLE IF NOT EXISTS mcp_servers");
        upSql.Should().Contain("CREATE TABLE IF NOT EXISTS persona_mcp_servers");
        upSql.Should().Contain("CREATE INDEX IF NOT EXISTS ix_mcp_servers_name");
        upSql.Should().Contain("CREATE INDEX IF NOT EXISTS ix_persona_mcp_servers_persona_id");
        upSql.Should().Contain("CREATE INDEX IF NOT EXISTS ix_persona_mcp_servers_mcp_server_id");

        var statements = upSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mcpCreate = statements.First(s => s.Contains("CREATE TABLE IF NOT EXISTS mcp_servers", StringComparison.OrdinalIgnoreCase));
        var joinCreate = statements.First(s => s.Contains("CREATE TABLE IF NOT EXISTS persona_mcp_servers", StringComparison.OrdinalIgnoreCase));

        foreach (var prop in McpServerEntity().GetProperties())
        {
            var col = prop.GetColumnName();
            var type = prop.GetColumnType();
            var nullability = prop.IsColumnNullable() ? "NULL" : "NOT NULL";
            mcpCreate.Should().Contain($"{col} {type} {nullability}");
        }

        foreach (var prop in PersonaMcpServerEntity().GetProperties())
        {
            var col = prop.GetColumnName();
            var type = prop.GetColumnType();
            var nullability = prop.IsColumnNullable() ? "NULL" : "NOT NULL";
            joinCreate.Should().Contain($"{col} {type} {nullability}");
        }

        var downSql = string.Join('\n', downOps.OfType<SqlOperation>().Select(o => o.Sql));
        var dropJoinIdx = downSql.IndexOf("DROP TABLE IF EXISTS persona_mcp_servers", StringComparison.Ordinal);
        var dropMcpIdx = downSql.IndexOf("DROP TABLE IF EXISTS mcp_servers", StringComparison.Ordinal);
        dropJoinIdx.Should().BeGreaterThanOrEqualTo(0);
        dropMcpIdx.Should().BeGreaterThan(dropJoinIdx, "child join table must be dropped before parent mcp_servers");
    }

    [Fact]
    public void MigrationSnapshot_HasNoPendingModelChanges()
    {
        using var db = TestDbContextFactory.Postgres();
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        migrations.Last().Should().Be(MigrationId);

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "AgentMeshDbContextModelSnapshot must stay in exact sync with McpServerConfiguration and PersonaMcpServerConfiguration");
    }

    private static IReadOnlyList<MigrationOperation> Operations(bool up)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        var migration = new AddMcpServersAndPersonaMcpServers();
        var method = typeof(Migration).GetMethod(
            up ? "Up" : "Down",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(migration, [builder]);
        return builder.Operations;
    }
}
