using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentMesh.Infrastructure.Data;

public class AgentMeshDbContextFactory : IDesignTimeDbContextFactory<AgentMeshDbContext>
{
    public AgentMeshDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AgentMeshDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=agentdb;Username=postgres;Password=postgres");
        optionsBuilder.UseSnakeCaseNamingConvention();

        return new AgentMeshDbContext(optionsBuilder.Options);
    }
}
