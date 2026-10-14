# Phase 1: Solution Scaffolding & Data Layer

## 1. Phase Objective & Scope

The goal of Phase 1 is to establish the complete .NET 10 solution topology, configure .NET Aspire local orchestration with backing infrastructure (PostgreSQL, Redis, and Temporal), and implement the domain entities and Entity Framework Core 10 database context with PostgreSQL schema migrations.

At the conclusion of this phase, an engineer or autonomous agent will be able to run a single command (`dotnet run --project src/AgentMesh.AppHost`) to launch the entire local developer fabric with OpenTelemetry monitoring and verified database schema creation.

---

## 2. Prerequisites & Environment

* **.NET SDK**: 10.0+ (`dotnet --version` >= 10.0.100)
* **Aspire Workload / Templates**: `dotnet workload install aspire` or template packages `Aspire.ProjectTemplates`
* **Container Runtime**: Docker or Podman running locally (for Aspire container resources)
* **PostgreSQL Client / Tools**: `dotnet ef` CLI tool installed (`dotnet tool install --global dotnet-ef`)

---

## 3. Architecture & Target Directory Topology

```text
agent-mesh/
├── AgentMesh.sln
├── src/
│   ├── AgentMesh.AppHost/                 # Aspire 10 Host (App orchestration)
│   │   ├── Program.cs
│   │   └── AgentMesh.AppHost.csproj
│   ├── AgentMesh.ServiceDefaults/         # Telemetry, HealthChecks, Resiliency
│   │   ├── Extensions.cs
│   │   └── AgentMesh.ServiceDefaults.csproj
│   ├── AgentMesh.Core/                    # Pure Domain Entities, Enums, DTOs
│   │   ├── Models/
│   │   │   ├── Agent.cs
│   │   │   ├── AgentSession.cs
│   │   │   ├── SessionMessage.cs
│   │   │   └── AgentJob.cs
│   │   ├── Enums/
│   │   │   ├── RuntimeType.cs
│   │   │   ├── TrustTier.cs
│   │   │   ├── SessionStatus.cs
│   │   │   ├── ApprovalStatus.cs
│   │   │   └── ExecutionStatus.cs
│   │   └── AgentMesh.Core.csproj
│   ├── AgentMesh.Infrastructure/          # EF Core DbContext & Migrations
│   │   ├── Data/
│   │   │   ├── AgentMeshDbContext.cs
│   │   │   └── Configurations/
│   │   │       ├── AgentConfiguration.cs
│   │   │       ├── AgentSessionConfiguration.cs
│   │   │       ├── SessionMessageConfiguration.cs
│   │   │       └── AgentJobConfiguration.cs
│   │   ├── Migrations/
│   │   └── AgentMesh.Infrastructure.csproj
│   ├── AgentMesh.Api/                     # Web API Skeleton
│   │   ├── Program.cs
│   │   └── AgentMesh.Api.csproj
│   └── AgentMesh.Worker/                  # Worker Service Skeleton
│       ├── Program.cs
│       └── AgentMesh.Worker.csproj
```

---

## 4. Step-by-Step Implementation Guide

### Step 1: Solution & Project Initialization

Run the following commands from the root of the repository (`/home/ckingdon/projects/agent-mesh`):

```bash
# 1. Create Solution
dotnet new sln -n AgentMesh

# 2. Create Core and Infrastructure Class Libraries
dotnet new classlib -o src/AgentMesh.Core -f net10.0
dotnet new classlib -o src/AgentMesh.Infrastructure -f net10.0

# 3. Create Service Defaults and Aspire AppHost
dotnet new aspire-servicedefaults -o src/AgentMesh.ServiceDefaults -f net10.0
dotnet new aspire-apphost -o src/AgentMesh.AppHost -f net10.0

# 4. Create Web API and Worker Services
dotnet new webapi -o src/AgentMesh.Api -f net10.0 --no-openapi
dotnet new worker -o src/AgentMesh.Worker -f net10.0

# 5. Add All Projects to Solution
dotnet sln AgentMesh.sln add \
  src/AgentMesh.Core \
  src/AgentMesh.Infrastructure \
  src/AgentMesh.ServiceDefaults \
  src/AgentMesh.AppHost \
  src/AgentMesh.Api \
  src/AgentMesh.Worker
```

### Step 2: Configure Project References & NuGet Dependencies

#### `AgentMesh.Core`
Domain models have zero third-party dependencies. Ensure .NET 10 target:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

#### `AgentMesh.Infrastructure`
Add EF Core, PostgreSQL Npgsql provider, and reference to `AgentMesh.Core`:
```bash
dotnet add src/AgentMesh.Infrastructure reference src/AgentMesh.Core
dotnet add src/AgentMesh.Infrastructure package Npgsql.EntityFrameworkCore.PostgreSQL --version 9.*
dotnet add src/AgentMesh.Infrastructure package Microsoft.EntityFrameworkCore.Design --version 9.*
```

#### `AgentMesh.AppHost`
Add Aspire Hosting packages:
```bash
dotnet add src/AgentMesh.AppHost package Aspire.Hosting.PostgreSQL --version 9.*
dotnet add src/AgentMesh.AppHost package Aspire.Hosting.Redis --version 9.*
dotnet add src/AgentMesh.AppHost reference src/AgentMesh.Api
dotnet add src/AgentMesh.AppHost reference src/AgentMesh.Worker
```

#### `AgentMesh.Api` and `AgentMesh.Worker`
Add references to `ServiceDefaults`, `Infrastructure`, and `Core`:
```bash
dotnet add src/AgentMesh.Api reference src/AgentMesh.ServiceDefaults
dotnet add src/AgentMesh.Api reference src/AgentMesh.Infrastructure
dotnet add src/AgentMesh.Api reference src/AgentMesh.Core
dotnet add src/AgentMesh.Api package Aspire.Npgsql.EntityFrameworkCore.PostgreSQL --version 9.*

dotnet add src/AgentMesh.Worker reference src/AgentMesh.ServiceDefaults
dotnet add src/AgentMesh.Worker reference src/AgentMesh.Infrastructure
dotnet add src/AgentMesh.Worker reference src/AgentMesh.Core
dotnet add src/AgentMesh.Worker package Aspire.Npgsql.EntityFrameworkCore.PostgreSQL --version 9.*
```

---

## 5. Domain Entities & Database Schema (`AgentMesh.Core`)

### Enums (`src/AgentMesh.Core/Enums/`)

```csharp
// src/AgentMesh.Core/Enums/RuntimeType.cs
namespace AgentMesh.Core.Enums;

public enum RuntimeType
{
    McpCloud,       // Gemini Spark, external cloud agents
    LocalOllama,    // Ollama, vLLM self-hosted models
    CliAgy          // agy / Antigravity terminal harness
}

// src/AgentMesh.Core/Enums/TrustTier.cs
namespace AgentMesh.Core.Enums;

public enum TrustTier
{
    Intern = 1,
    SeniorManager = 2,
    LeadHuman = 3
}

// src/AgentMesh.Core/Enums/ApprovalStatus.cs
namespace AgentMesh.Core.Enums;

public enum ApprovalStatus
{
    AutoApproved,
    PendingHuman,
    Approved,
    Rejected
}

// src/AgentMesh.Core/Enums/ExecutionStatus.cs
namespace AgentMesh.Core.Enums;

public enum ExecutionStatus
{
    Staged,
    Running,
    Completed,
    Failed
}
```

### Entities (`src/AgentMesh.Core/Models/`)

```csharp
// src/AgentMesh.Core/Models/Agent.cs
using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

public class Agent
{
    public string Id { get; set; } = string.Empty; // e.g. 'gemini-spark'
    public string Name { get; set; } = string.Empty;
    public RuntimeType RuntimeType { get; set; }
    public TrustTier TrustTier { get; set; } = TrustTier.Intern;
    public string PersonaPrompt { get; set; } = string.Empty;
    public string MemorySummary { get; set; } = string.Empty;
    public string? ApiKeyHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<SessionMessage> SentMessages { get; set; } = new List<SessionMessage>();
    public ICollection<AgentJob> RequestedJobs { get; set; } = new List<AgentJob>();
}

// src/AgentMesh.Core/Models/AgentSession.cs
namespace AgentMesh.Core.Models;

public class AgentSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<SessionMessage> Messages { get; set; } = new List<SessionMessage>();
    public ICollection<AgentJob> Jobs { get; set; } = new List<AgentJob>();
}

// src/AgentMesh.Core/Models/SessionMessage.cs
namespace AgentMesh.Core.Models;

public class SessionMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public AgentSession? Session { get; set; }
    public string SenderAgentId { get; set; } = string.Empty;
    public Agent? SenderAgent { get; set; }
    public string? TargetAgentId { get; set; }
    public Agent? TargetAgent { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? StructuredPayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// src/AgentMesh.Core/Models/AgentJob.cs
using AgentMesh.Core.Enums;

namespace AgentMesh.Core.Models;

public class AgentJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public AgentSession? Session { get; set; }
    public string RequestedByAgentId { get; set; } = string.Empty;
    public Agent? RequestedByAgent { get; set; }
    public string TargetService { get; set; } = string.Empty;
    public string ActionVerb { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.AutoApproved;
    public ExecutionStatus ExecutionStatus { get; set; } = ExecutionStatus.Staged;
    public string? TemporalWorkflowId { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

---

## 6. Entity Framework Core DbContext (`AgentMesh.Infrastructure`)

```csharp
// src/AgentMesh.Infrastructure/Data/AgentMeshDbContext.cs
using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Infrastructure.Data;

public class AgentMeshDbContext(DbContextOptions<AgentMeshDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();
    public DbSet<SessionMessage> SessionMessages => Set<SessionMessage>();
    public DbSet<AgentJob> AgentJobs => Set<AgentJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentMeshDbContext).Assembly);
    }
}
```

### Entity Configurations

```csharp
// src/AgentMesh.Infrastructure/Data/Configurations/AgentJobConfiguration.cs
using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class AgentJobConfiguration : IEntityTypeConfiguration<AgentJob>
{
    public void Configure(EntityTypeBuilder<AgentJob> builder)
    {
        builder.ToTable("agent_jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.PayloadJson)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(j => j.ResultJson)
            .HasColumnType("jsonb");

        builder.Property(j => j.ApprovalStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(j => j.ExecutionStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.HasIndex(j => j.ExecutionStatus).HasDatabaseName("idx_jobs_status");
        builder.HasIndex(j => j.SessionId).HasDatabaseName("idx_jobs_session");

        builder.HasOne(j => j.Session)
            .WithMany(s => s.Jobs)
            .HasForeignKey(j => j.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(j => j.RequestedByAgent)
            .WithMany(a => a.RequestedJobs)
            .HasForeignKey(j => j.RequestedByAgentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

---

## 7. Aspire AppHost Configuration (`AgentMesh.AppHost`)

```csharp
// src/AgentMesh.AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

// 1. Local Postgres with persistent volume
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("agentdb");

// 2. Local Redis with persistent volume
var redis = builder.AddRedis("redis")
    .WithDataVolume();

// 3. Local Temporal Dev Server Container (with Web UI on 8233)
var temporal = builder.AddContainer("temporal", "temporalio/admin-tools", "latest")
    .WithArgs("temporal", "server", "start-dev", "--ip", "0.0.0.0")
    .WithHttpEndpoint(targetPort: 7233, name: "grpc")
    .WithHttpEndpoint(targetPort: 8233, name: "ui");

// 4. API Service
var api = builder.AddProject<Projects.AgentMesh_Api>("api")
    .WithReference(postgres)
    .WithReference(redis)
    .WithEnvironment("Temporal__Host", "localhost:7233")
    .WaitFor(temporal);

// 5. Worker Service
var worker = builder.AddProject<Projects.AgentMesh_Worker>("worker")
    .WithReference(postgres)
    .WithReference(redis)
    .WithEnvironment("Temporal__Host", "localhost:7233")
    .WaitFor(temporal);

builder.Build().Run();
```

---

## 8. Verification & Acceptance Criteria

### Automated Build & Compilation
```bash
dotnet build AgentMesh.sln
```
* **Success**: Zero compilation errors and zero warnings across all projects.

### Migration Generation
```bash
dotnet ef migrations add InitialCreate \
  --project src/AgentMesh.Infrastructure \
  --startup-project src/AgentMesh.Api
```
* **Success**: Migration file generated in `src/AgentMesh.Infrastructure/Migrations/` with PostgreSQL JSONB types and indexes.

### Runtime Sanity Check
```bash
dotnet run --project src/AgentMesh.AppHost
```
* Open Aspire Dashboard (URL logged in stdout, e.g. `http://localhost:18888`).
* Verify resources `postgres`, `redis`, `temporal`, `api`, and `worker` show healthy status.
* Verify Temporal Web UI at `http://localhost:8233` is responsive.

---

## 9. Agent Execution Prompt & Checklist

```markdown
### AI Developer Task: Phase 1 Execution
Please execute Phase 1 of AgentMesh according to docs/phases/phase-1-scaffolding-and-data-layer.md.
Follow these steps:
- [ ] Initialize dotnet solution and all 6 projects with exact names.
- [ ] Add project references and Aspire NuGet packages.
- [ ] Implement domain models and enums in AgentMesh.Core.
- [ ] Configure DbContext and entity configurations in AgentMesh.Infrastructure.
- [ ] Configure Aspire AppHost Program.cs with postgres, redis, temporal, api, worker.
- [ ] Generate initial EF Core migration.
- [ ] Run `dotnet build` and ensure clean exit.
```
