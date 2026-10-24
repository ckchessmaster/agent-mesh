using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Storage;
using AgentMesh.Worker.Activities;
using Microsoft.EntityFrameworkCore;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<AgentMeshDbContext>("agentdb", configureDbContextOptions: options => options.UseSnakeCaseNamingConvention());
builder.AddRedisClient("redis");

// Configure SeaweedFS S3 Storage
builder.Services.AddSeaweedS3Storage(builder.Configuration);

var temporalHost = builder.Configuration["Temporal:Host"] ?? "localhost:7233";
var temporalNamespace = builder.Configuration["Temporal:Namespace"] ?? "default";

builder.Services.AddHttpClient<NotificationActivities>();

builder.Services.AddHostedTemporalWorker(
    clientTargetHost: temporalHost,
    clientNamespace: temporalNamespace,
    taskQueue: "agent-task-queue")
    .AddWorkflow<AgentTaskWorkflow>()
    .AddScopedActivities<DatabaseActivities>()
    .AddScopedActivities<ExecutionActivities>()
    .AddScopedActivities<NotificationActivities>();

var host = builder.Build();
host.Run();
