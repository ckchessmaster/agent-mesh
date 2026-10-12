using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Infrastructure.Data;

public class AgentMeshDbContext(DbContextOptions<AgentMeshDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<SessionMessage> SessionMessages => Set<SessionMessage>();
    public DbSet<AgentJob> AgentJobs => Set<AgentJob>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseSnakeCaseNamingConvention();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentMeshDbContext).Assembly);
    }
}
