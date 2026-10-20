using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Infrastructure.Data;

public class AgentMeshDbContext(DbContextOptions<AgentMeshDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Agent> Drivers => Set<Agent>();
    public DbSet<Persona> Personas => Set<Persona>();
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<SessionMessage> SessionMessages => Set<SessionMessage>();
    public DbSet<AgentJob> AgentJobs => Set<AgentJob>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectPersona> ProjectPersonas => Set<ProjectPersona>();
    public DbSet<ProjectJob> ProjectJobs => Set<ProjectJob>();
    public DbSet<JobMessage> JobMessages => Set<JobMessage>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSnakeCaseNamingConvention();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentMeshDbContext).Assembly);
    }
}
