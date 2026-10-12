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
}
