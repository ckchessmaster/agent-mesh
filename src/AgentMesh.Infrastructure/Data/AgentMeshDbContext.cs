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
    public DbSet<JobWorkspaceState> JobWorkspaceStates => Set<JobWorkspaceState>();
    public DbSet<JobHandoff> JobHandoffs => Set<JobHandoff>();

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

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SanitizeStrings();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        SanitizeStrings();
        return base.SaveChanges();
    }

    private void SanitizeStrings()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                foreach (var prop in entry.CurrentValues.Properties)
                {
                    if (prop.ClrType == typeof(string))
                    {
                        var val = entry.CurrentValues[prop] as string;
                        if (val != null && val.IndexOf('\0') >= 0)
                        {
                            entry.CurrentValues[prop] = val.Replace("\0", string.Empty);
                        }
                    }
                }
            }
        }
    }
}
