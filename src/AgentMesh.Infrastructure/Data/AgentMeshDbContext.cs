using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
    public DbSet<AgentMemory> AgentMemories => Set<AgentMemory>();
    public DbSet<ProjectDocument> ProjectDocuments => Set<ProjectDocument>();
    public DbSet<JobTimelineCard> JobTimelineCards => Set<JobTimelineCard>();
    public DbSet<JobCardComment> JobCardComments => Set<JobCardComment>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSnakeCaseNamingConvention();
            optionsBuilder.ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning));
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentMeshDbContext).Assembly);

        modelBuilder.Entity<JobTimelineCard>(b =>
        {
            b.ToTable("job_timeline_cards");
            b.HasKey(c => c.Id);
            b.Property(c => c.JobId).HasMaxLength(64).IsRequired();
            b.Property(c => c.TaskId).HasMaxLength(64);
            b.Property(c => c.CardType).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(c => c.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(c => c.Title).HasMaxLength(256).IsRequired();
            b.Property(c => c.StructuredPayloadJson).HasColumnType("jsonb").IsRequired();
            b.Property(c => c.CreatedAt).IsRequired();
            b.Property(c => c.UpdatedAt).IsRequired();

            b.HasIndex(c => new { c.JobId, c.CreatedAt });
            b.HasMany(c => c.Comments)
             .WithOne(m => m.Card)
             .HasForeignKey(m => m.CardId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobCardComment>(b =>
        {
            b.ToTable("job_card_comments");
            b.HasKey(m => m.Id);
            b.Property(m => m.SenderId).HasMaxLength(64).IsRequired();
            b.Property(m => m.SenderType).HasMaxLength(16).IsRequired();
            b.Property(m => m.Content).IsRequired();
            b.Property(m => m.CreatedAt).IsRequired();

            b.HasIndex(m => new { m.CardId, m.CreatedAt });
        });
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
