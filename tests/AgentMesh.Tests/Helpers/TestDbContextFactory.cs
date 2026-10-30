using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Tests.Helpers;

public static class TestDbContextFactory
{
    public static AgentMeshDbContext Create(string? dbName = null)
    {
        var name = dbName ?? Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AgentMeshDbContext>()
            .UseInMemoryDatabase(databaseName: name)
            .Options;

        return new AgentMeshDbContext(options);
    }

    /// <summary>
    /// A context whose queries fail the way a downed database fails: an exception escaping
    /// <c>ToListAsync</c>, not one thrown at construction.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentMeshDbContext.Agents"/> is not virtual, so the context cannot be stubbed, and
    /// the InMemory provider never fails on its own. Pointing Npgsql at a closed loopback port
    /// reproduces the real failure surface - connection refused, raised inside the query - without
    /// needing an actual database in CI. Port 1 fails immediately rather than waiting out a timeout.
    /// </remarks>
    public static AgentMeshDbContext CreateUnreachable()
    {
        var options = new DbContextOptionsBuilder<AgentMeshDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=agentmesh_unreachable;Username=nobody;Password=nobody;Timeout=1;Command Timeout=1")
            .Options;

        return new AgentMeshDbContext(options);
    }

    /// <summary>
    /// A PostgreSQL-backed context that is never connected to, for asking EF what SQL or what schema it
    /// would produce.
    /// </summary>
    /// <remarks>
    /// Model and query translation run without a socket, so this is how a test can pin the relational
    /// shape (column types, index uniqueness) or a query's translation, both of which the InMemory
    /// provider is unable to model: it accepts any query and enforces no indexes.
    /// <para>
    /// The snake-case convention is applied explicitly because <see cref="AgentMeshDbContext.OnConfiguring"/>
    /// only adds it when the options builder is otherwise unconfigured, and <c>UseNpgsql</c> here counts as
    /// configured. Production sets it through <c>AddNpgsqlDbContext</c>; leaving it off here would build a
    /// model whose column names do not match the deployed database.
    /// </para>
    /// </remarks>
    public static AgentMeshDbContext Postgres()
    {
        var options = new DbContextOptionsBuilder<AgentMeshDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=schema-only;Username=nobody;Password=nobody;Timeout=1;Command Timeout=1")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AgentMeshDbContext(options);
    }
}
